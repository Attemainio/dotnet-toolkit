using DotnetToolkit.McpServer.Fingerprint;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetToolkit.McpServer.Git;

/// <summary>
/// Diffs the symbol shape of two git refs (spec §14). Works purely from syntax read out of git — no
/// checkout, no MSBuild load per ref — so it stays cheap. Because the comparison is over trivia-blind
/// fingerprints, a reformat or comment-only commit reports zero semantic change, which is the whole
/// point of asking "what changed" rather than reading a textual diff.
/// </summary>
public sealed class SemanticDiff
{
    private readonly GitAnalyzer _git;

    public SemanticDiff(GitAnalyzer git) => _git = git;

    public sealed record ChangedSymbol(
        string Key, string DisplayString, IReadOnlyList<string> LayersChanged, string ApiImpact);

    public sealed record Result(
        int Commits,
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Removed,
        IReadOnlyList<ChangedSymbol> Changed);

    public async Task<Result> CompareAsync(string fromRef, string toRef, CancellationToken ct = default)
    {
        var commits = await _git.CommitCountAsync(fromRef, toRef, ct);
        var files = await _git.ChangedCSharpFilesAsync(fromRef, toRef, ct);

        var added = new List<string>();
        var removed = new List<string>();
        var changed = new List<ChangedSymbol>();

        foreach (var file in files)
        {
            var beforeText = await _git.FileAtRefAsync(fromRef, file.Path, ct);
            var afterText = await _git.FileAtRefAsync(toRef, file.Path, ct);

            var before = beforeText is null ? [] : Declarations(beforeText);
            var after = afterText is null ? [] : Declarations(afterText);

            foreach (var (key, info) in after)
            {
                if (!before.TryGetValue(key, out var prior))
                {
                    added.Add(info.Display);
                    continue;
                }

                var layers = new List<string>();
                if (prior.Decl != info.Decl)
                    layers.Add("decl");
                if (prior.Body != info.Body)
                    layers.Add("body");
                if (layers.Count > 0)
                    changed.Add(new ChangedSymbol(key, info.Display, layers, ImpactOf(layers, info.IsPublicSurface)));
            }

            foreach (var (key, info) in before)
            {
                if (!after.ContainsKey(key))
                    removed.Add(info.Display);
            }
        }

        return new Result(commits, added, removed, changed);
    }

    /// <summary>
    /// A declaration change is breaking to dependents; a body-only change is not. Public surface widens
    /// the blast radius beyond the assembly, which is the distinction the verdicts encode.
    /// </summary>
    private static string ImpactOf(IReadOnlyList<string> layers, bool isPublicSurface)
    {
        if (!layers.Contains("decl"))
            return "non-breaking";
        return isPublicSurface ? "breaking-public" : "breaking-internal";
    }

    private sealed record DeclInfo(string Display, string Decl, string? Body, bool IsPublicSurface);

    /// <summary>
    /// Keys every declaration in a file by container path + descriptor, so the same logical member can
    /// be matched across two revisions without a semantic model.
    /// </summary>
    private static Dictionary<string, DeclInfo> Declarations(string source)
    {
        var map = new Dictionary<string, DeclInfo>(StringComparer.Ordinal);
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();

        foreach (var node in root.DescendantNodes())
        {
            if (node is not (BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseMethodDeclarationSyntax
                or PropertyDeclarationSyntax or EventDeclarationSyntax or BaseFieldDeclarationSyntax))
                continue;

            var (decl, body) = SyntaxFingerprint.Compute(node);
            var key = KeyOf(node);
            map[key] = new DeclInfo(key, decl, body, IsPublicSurface(node));
        }
        return map;
    }

    private static bool IsPublicSurface(SyntaxNode node) =>
        node is MemberDeclarationSyntax member
        && member.Modifiers.Any(m => m.Text is "public" or "protected");

    private static string KeyOf(SyntaxNode decl)
    {
        var container = new List<string>();
        foreach (var ancestor in decl.Ancestors())
        {
            switch (ancestor)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    container.Insert(0, ns.Name.ToString());
                    break;
                case BaseTypeDeclarationSyntax t:
                    container.Insert(0, t.Identifier.Text);
                    break;
            }
        }
        return string.Join('.', container) + "::" + Descriptor(decl);
    }

    private static string Descriptor(SyntaxNode decl) => decl switch
    {
        BaseTypeDeclarationSyntax t => "type " + t.Identifier.Text,
        DelegateDeclarationSyntax d => "delegate " + d.Identifier.Text,
        MethodDeclarationSyntax m => $"method {m.Identifier.Text}/{m.ParameterList.Parameters.Count}",
        ConstructorDeclarationSyntax c => $"ctor/{c.ParameterList.Parameters.Count}",
        PropertyDeclarationSyntax p => "prop " + p.Identifier.Text,
        EventDeclarationSyntax e => "event " + e.Identifier.Text,
        BaseFieldDeclarationSyntax f => "field " + f.Declaration.Variables.First().Identifier.Text,
        _ => decl.Kind().ToString(),
    };
}
