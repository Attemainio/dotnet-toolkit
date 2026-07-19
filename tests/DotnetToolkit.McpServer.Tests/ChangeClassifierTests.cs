using DotnetToolkit.McpServer.Validation;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    private static (Solution Solution, DocumentId DocId) NewSolution()
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
        var document = workspace.AddDocument(project.Id, "Widget.cs", SourceText.From(BaseSource));
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
}
