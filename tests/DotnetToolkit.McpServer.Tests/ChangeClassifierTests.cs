using DotnetToolkit.McpServer.Validation;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Fast (no-MSBuild) coverage of the write-path core using an in-memory <see cref="AdhocWorkspace"/>:
/// change detection and the validation ladder over a forked solution.
/// </summary>
public sealed class ChangeClassifierTests
{
    private const string BaseSource = """
        namespace Demo;

        public interface IWidget
        {
            int Spin(int turns);
        }

        public class Widget : IWidget
        {
            public int Spin(int turns) => turns * 2;
        }
        """;

    private static (Solution Solution, DocumentId DocId) NewSolution(string? source = null)
    {
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "Demo", "Demo", LanguageNames.CSharp,
            metadataReferences: refs,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
        var document = workspace.AddDocument(project.Id, "Widget.cs", SourceText.From(source ?? BaseSource));
        return (workspace.CurrentSolution, document.Id);
    }

    private static Solution Fork(Solution solution, DocumentId docId, string newSource) =>
        solution.WithDocumentText(docId, SourceText.From(newSource));

    [Fact]
    public async Task ArityChange_IsDetectedAsSignature()
    {
        var (solution, docId) = NewSolution();
        var forked = Fork(solution, docId, BaseSource.Replace(
            "public int Spin(int turns) => turns * 2;",
            "public int Spin(int turns, int extra) => turns * 2 + extra;"));

        var changes = await ChangeClassifier.DetectAsync(solution, forked, [docId]);

        var change = Assert.Single(changes, c => c.DisplayString.Contains("Spin"));
        Assert.Contains(ChangeKind.Signature, change.Kinds);
        Assert.Equal(ValidationLevel.DependentCompile,
            EscalationTable.RequiredForPatch(changes.Select(c => ((IReadOnlyCollection<ChangeKind>)c.Kinds, false))));
    }

    [Fact]
    public async Task ArityChange_BreaksInterfaceImplementation_LadderFails()
    {
        var (solution, docId) = NewSolution();
        var forked = Fork(solution, docId, BaseSource.Replace(
            "public int Spin(int turns) => turns * 2;",
            "public int Spin(int turns, int extra) => turns * 2 + extra;"));

        var ladder = await ValidationLadder.RunAsync(forked, [docId], ValidationLevel.DependentCompile);

        Assert.False(ladder.Succeeded); // Widget no longer implements IWidget.Spin (CS0535)
    }

    // Reproduces the validate_patch path: a line-span edit applied via PatchSandbox (not a full-document
    // fork), keyed by relative file path through SolutionLocator, must still be detected.
    [Fact]
    public async Task PatchSandbox_LineEdit_IsAppliedAndDetected()
    {
        var root = Directory.CreateTempSubdirectory("sandbox-tests-").FullName;
        try
        {
            var filePath = Path.Combine(root, "Lib", "Widget.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, BaseSource);

            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var docId = DocumentId.CreateNewId(projectId);
            var solution = workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), "Demo", "Demo", LanguageNames.CSharp,
                    metadataReferences: refs, compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)))
                .AddDocument(DocumentInfo.Create(docId, "Widget.cs",
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(BaseSource), VersionStamp.Create())),
                    filePath: filePath));

            var locator = new SolutionLocator(NullLogger<SolutionLocator>.Instance, root);
            // Spin is line 10 in BaseSource.
            var edit = new PatchEdit("Lib/Widget.cs", 10, 10, "    public int Spin(int turns, int extra) => turns * 2 + extra;");
            var sandbox = await PatchSandbox.ApplyAsync(solution, locator, [edit]);

            Assert.Null(sandbox.Error);
            var forkedText = (await sandbox.Forked.GetDocument(docId)!.GetTextAsync()).ToString();
            Assert.Contains("int extra", forkedText);

            var changes = await ChangeClassifier.DetectAsync(solution, sandbox.Forked, sandbox.ChangedDocuments);
            Assert.Contains(changes, c => c.DisplayString.Contains("Spin") && c.Kinds.Contains(ChangeKind.Signature));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BodyChange_IsBodyKind_AndLadderSucceeds()
    {
        var (solution, docId) = NewSolution();
        var forked = Fork(solution, docId, BaseSource.Replace("=> turns * 2;", "=> turns * 3;"));

        var changes = await ChangeClassifier.DetectAsync(solution, forked, [docId]);
        var change = Assert.Single(changes, c => c.DisplayString.Contains("Spin"));
        Assert.Equal([ChangeKind.Body], change.Kinds);

        var ladder = await ValidationLadder.RunAsync(forked, [docId], ValidationLevel.ProjectCompile);
        Assert.True(ladder.Succeeded);
    }
    [Fact]
    public async Task IdentifierRename_PairsWithOldSymbolId()
    {
        var (solution, docId) = NewSolution();
        var forked = Fork(solution, docId, BaseSource.Replace(
            "public int Spin(int turns) => turns * 2;",
            "public int Turn(int turns) => turns * 2;"));

        // Compute the pre-rename symbol's own id independently (not by calling ChangeClassifier itself),
        // so the assertion isn't just restating the production code's output back at it.
        var baseDoc = solution.GetDocument(docId)!;
        var baseModel = await baseDoc.GetSemanticModelAsync();
        var baseRoot = await baseDoc.GetSyntaxRootAsync();
        var spinDecl = baseRoot!.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "Spin" && m.Parent is ClassDeclarationSyntax);
        var expectedOldId = SymbolKey.IdOf(baseModel!.GetDeclaredSymbol(spinDecl)!);

        var changes = await ChangeClassifier.DetectAsync(solution, forked, [docId]);

        // IWidget.Spin is untouched (same key, same content) so it's silently skipped; only Widget.Turn
        // should show up, as a single paired change rather than a separate remove + add.
        var change = Assert.Single(changes, c => c.DisplayString.Contains("Turn"));
        Assert.Contains(ChangeKind.Signature, change.Kinds);
        Assert.Equal(expectedOldId, change.OldSymbolId);
        Assert.NotEqual(change.SymbolId, change.OldSymbolId);
    }

    [Fact]
    public async Task AmbiguousRename_FallsThroughToSeparateAddAndRemove()
    {
        const string source = """
            namespace Demo;

            public class Widget
            {
                public int Spin(int turns) => turns * 2;
                public int Whirl(int turns) => turns * 3;
            }
            """;
        const string renamed = """
            namespace Demo;

            public class Widget
            {
                public int A(int turns) => turns * 2;
                public int B(int turns) => turns * 3;
            }
            """;
        var (solution, docId) = NewSolution(source);
        var forked = Fork(solution, docId, renamed);

        var changes = await ChangeClassifier.DetectAsync(solution, forked, [docId]);

        // Two same-signature removals and two same-signature additions in one container is ambiguous --
        // nothing here distinguishes "Spin became A, Whirl became B" from "Spin became B, Whirl became A",
        // so neither pairs; each shows up as an independent added/removed change instead of being guessed.
        Assert.Equal(4, changes.Count);
        Assert.Equal(2, changes.Count(c => c.Kinds.Contains(ChangeKind.Added)));
        Assert.Equal(2, changes.Count(c => c.Kinds.Contains(ChangeKind.Removed)));
        Assert.DoesNotContain(changes, c => c.Kinds.Contains(ChangeKind.Signature));
    }

    [Fact]
    public async Task PureAddition_IsDetectedAndAnchoredToContainingType()
    {
        var (solution, docId) = NewSolution();
        var forked = Fork(solution, docId, BaseSource.Replace(
            "public int Spin(int turns) => turns * 2;",
            "public int Spin(int turns) => turns * 2;\n    public int Extra() => 1;"));

        var changes = await ChangeClassifier.DetectAsync(solution, forked, [docId]);

        var change = Assert.Single(changes, c => c.DisplayString.Contains("Extra"));
        Assert.Contains(ChangeKind.Added, change.Kinds);
        // Anchored to the containing type's id, not its own -- a brand-new member has no prior version to
        // lease against, so the caller's lease on Widget itself is what gets checked instead.
        Assert.NotEqual(change.SymbolId, change.OldSymbolId);
    }

    [Fact]
    public async Task PureRemoval_IsDetectedAndAnchoredToItsOwnOldSymbol()
    {
        var (solution, docId) = NewSolution();
        var forked = Fork(solution, docId, BaseSource.Replace(
            "    public int Spin(int turns) => turns * 2;\n", ""));

        var changes = await ChangeClassifier.DetectAsync(solution, forked, [docId]);

        var change = Assert.Single(changes, c => c.DisplayString.Contains("Spin"));
        Assert.Contains(ChangeKind.Removed, change.Kinds);
        Assert.Equal(change.SymbolId, change.OldSymbolId);
    }
}
