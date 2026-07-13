using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetToolkit.McpServer.Indexing;

/// <summary>
/// Builds a per-file symbol outline from syntax alone (no compilation, no MSBuild):
/// namespaces, type declarations, member signatures, and XML doc summaries.
/// </summary>
public static partial class OutlineBuilder
{
    public static FileEntry Build(string text, long mtimeTicks, long length)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        var namespaces = new List<string>();
        var types = new List<TypeEntry>();
        Collect(root.Members, "", namespaces, types);
        return new FileEntry(mtimeTicks, length, namespaces, types);
    }

    private static void Collect(
        SyntaxList<MemberDeclarationSyntax> members,
        string containerFq,
        List<string> namespaces,
        List<TypeEntry> types)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    var nsName = Combine(containerFq, ns.Name.ToString());
                    if (!namespaces.Contains(nsName))
                        namespaces.Add(nsName);
                    Collect(ns.Members, nsName, namespaces, types);
                    break;
                case BaseTypeDeclarationSyntax type:
                    types.Add(BuildType(type, containerFq));
                    break;
                case DelegateDeclarationSyntax del:
                    types.Add(BuildDelegate(del, containerFq));
                    break;
            }
        }
    }

    private static TypeEntry BuildType(BaseTypeDeclarationSyntax type, string containerFq)
    {
        var name = type.Identifier.Text + (type is TypeDeclarationSyntax { TypeParameterList: { } tp } ? tp.ToString() : "");
        var fq = Combine(containerFq, name);
        var kind = type switch
        {
            InterfaceDeclarationSyntax => "I",
            StructDeclarationSyntax => "S",
            RecordDeclarationSyntax => "R",
            EnumDeclarationSyntax => "E",
            _ => "C",
        };
        var bases = type.BaseList?.Types.Select(b => b.Type.ToString()).ToArray() ?? [];
        var members = new List<MemberEntry>();
        var nested = new List<TypeEntry>();

        if (type is EnumDeclarationSyntax en)
        {
            foreach (var m in en.Members)
            {
                var sig = m.EqualsValue is { } eq ? $"{m.Identifier.Text} = {eq.Value}" : m.Identifier.Text;
                members.Add(new MemberEntry("F", m.Identifier.Text, sig, DocSummary(m), Line(m), true));
            }
        }
        else if (type is TypeDeclarationSyntax td)
        {
            var isInterface = td is InterfaceDeclarationSyntax;
            foreach (var m in td.Members)
            {
                switch (m)
                {
                    case BaseTypeDeclarationSyntax nestedType:
                        nested.Add(BuildType(nestedType, fq));
                        break;
                    case DelegateDeclarationSyntax nestedDel:
                        nested.Add(BuildDelegate(nestedDel, fq));
                        break;
                    default:
                        var entry = BuildMember(m, isInterface);
                        if (entry is not null)
                            members.Add(entry);
                        break;
                }
            }
            // Primary-constructor records: surface the parameter list as a constructor.
            if (td is RecordDeclarationSyntax { ParameterList: { } rp })
                members.Insert(0, new MemberEntry("K", td.Identifier.Text, $"{td.Identifier.Text}{RenderParams(rp)}", null, Line(td), true));
        }

        return new TypeEntry(kind, name, fq, DocSummary(type), bases, type.Modifiers.ToString(), Line(type), members, nested, IsPublic(type.Modifiers, containerHasNamespaceOnly: true));
    }

    private static TypeEntry BuildDelegate(DelegateDeclarationSyntax del, string containerFq)
    {
        var name = del.Identifier.Text + (del.TypeParameterList?.ToString() ?? "");
        var fq = Combine(containerFq, name);
        var sigMember = new MemberEntry(
            "M", name, $"{name}{RenderParams(del.ParameterList)} -> {del.ReturnType}", null, Line(del), true);
        return new TypeEntry("D", name, fq, DocSummary(del), [], del.Modifiers.ToString(), Line(del),
            [sigMember], [], IsPublic(del.Modifiers, containerHasNamespaceOnly: true));
    }

    private static MemberEntry? BuildMember(MemberDeclarationSyntax member, bool isInterface)
    {
        var isPublic = isInterface || IsPublicOrProtected(member.Modifiers);
        switch (member)
        {
            case MethodDeclarationSyntax m:
            {
                var name = m.Identifier.Text + (m.TypeParameterList?.ToString() ?? "");
                return new MemberEntry("M", m.Identifier.Text,
                    $"{name}{RenderParams(m.ParameterList)} -> {m.ReturnType}",
                    DocSummary(m), Line(m), isPublic);
            }
            case ConstructorDeclarationSyntax c:
                return new MemberEntry("K", c.Identifier.Text,
                    $"{c.Identifier.Text}{RenderParams(c.ParameterList)}",
                    DocSummary(c), Line(c), isPublic);
            case PropertyDeclarationSyntax p:
                return new MemberEntry("P", p.Identifier.Text,
                    $"{p.Identifier.Text}: {p.Type} {Accessors(p)}",
                    DocSummary(p), Line(p), isPublic);
            case IndexerDeclarationSyntax ix:
                return new MemberEntry("P", "this[]",
                    $"this[{RenderParamList(ix.ParameterList.Parameters)}]: {ix.Type}",
                    DocSummary(ix), Line(ix), isPublic);
            case FieldDeclarationSyntax f:
            {
                var v = f.Declaration.Variables.First();
                return new MemberEntry("F", v.Identifier.Text,
                    $"{v.Identifier.Text}: {f.Declaration.Type}",
                    DocSummary(f), Line(f), isPublic);
            }
            case EventFieldDeclarationSyntax ef:
            {
                var v = ef.Declaration.Variables.First();
                return new MemberEntry("V", v.Identifier.Text,
                    $"{v.Identifier.Text}: {ef.Declaration.Type}",
                    DocSummary(ef), Line(ef), isPublic);
            }
            case EventDeclarationSyntax e:
                return new MemberEntry("V", e.Identifier.Text,
                    $"{e.Identifier.Text}: {e.Type}",
                    DocSummary(e), Line(e), isPublic);
            case OperatorDeclarationSyntax op:
                return new MemberEntry("M", $"operator {op.OperatorToken.Text}",
                    $"operator {op.OperatorToken.Text}{RenderParams(op.ParameterList)} -> {op.ReturnType}",
                    DocSummary(op), Line(op), isPublic);
            default:
                return null;
        }
    }

    private static string Accessors(PropertyDeclarationSyntax p)
    {
        if (p.ExpressionBody is not null)
            return "{get}";
        if (p.AccessorList is null)
            return "";
        var parts = p.AccessorList.Accessors.Select(a => a.Keyword.Text);
        return "{" + string.Join("; ", parts) + "}";
    }

    private static string RenderParams(ParameterListSyntax list) => $"({RenderParamList(list.Parameters)})";

    private static string RenderParamList(IEnumerable<ParameterSyntax> parameters) =>
        string.Join(", ", parameters.Select(p =>
        {
            var mods = p.Modifiers.Count > 0 ? p.Modifiers.ToString() + " " : "";
            var def = p.Default is not null ? $" = {p.Default.Value}" : "";
            return $"{mods}{p.Type} {p.Identifier.Text}{def}";
        }));

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string Combine(string container, string name) =>
        container.Length == 0 ? name : $"{container}.{name}";

    private static bool IsPublic(SyntaxTokenList modifiers, bool containerHasNamespaceOnly) =>
        modifiers.Any(t => t.IsKind(SyntaxKind.PublicKeyword))
        || (containerHasNamespaceOnly && !modifiers.Any(t =>
            t.IsKind(SyntaxKind.PrivateKeyword) || t.IsKind(SyntaxKind.InternalKeyword)));

    private static bool IsPublicOrProtected(SyntaxTokenList modifiers) =>
        modifiers.Any(t => t.IsKind(SyntaxKind.PublicKeyword) || t.IsKind(SyntaxKind.ProtectedKeyword));

    /// <summary>Extracts the &lt;summary&gt; text of a declaration's XML doc comment, if any.</summary>
    internal static string? DocSummary(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia()
            .Select(t => t.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();
        return trivia is null ? null : SummaryFromXml(trivia.ToFullString());
    }

    /// <summary>Extracts the &lt;summary&gt; text from raw doc-comment XML (also used for ISymbol docs).</summary>
    public static string? SummaryFromXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        var match = SummaryRegex().Match(xml);
        if (!match.Success)
            return null;

        var text = match.Groups[1].Value;
        text = text.Replace("///", " ");
        text = CrefRegex().Replace(text, m => m.Groups[1].Value.Split('.').Last());
        text = TagRegex().Replace(text, "");
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex(@"<summary>([\s\S]*?)</summary>")]
    private static partial Regex SummaryRegex();

    [GeneratedRegex(@"<see\w*\s+\w+=""(?:[A-Z]:)?([^""]+)""\s*/?>")]
    private static partial Regex CrefRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
