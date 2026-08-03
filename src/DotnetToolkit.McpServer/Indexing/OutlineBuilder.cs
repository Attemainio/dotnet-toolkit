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
    /// <summary>Parses one C# file into its outline of namespaces and declared types.</summary>
    /// <param name="synthesizeEntryPoint">
    /// Whether a top-level-statements file should also yield the compiler-synthesized <c>Program.Main</c>.
    /// The CALLER decides: the synthesized type is always named Program regardless of the file, so every
    /// such file in a repo claims the same name. Legal C# allows only one entry point per project, so this
    /// is unambiguous within a solution — but the index scans the whole tree, including sample solutions
    /// under tests/ and stray `dotnet run` scripts that no project compiles. Synthesizing for all of them
    /// gives ProjectIndex.Disambiguate several equally-good candidates and it correctly resolves to none,
    /// dropping the location for the one entry point that actually mattered.
    /// </param>
    public static FileEntry Build(string text, long mtimeTicks, long length, bool synthesizeEntryPoint = true)
    {
        var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
        var namespaces = new List<string>();
        var types = new List<TypeEntry>();
        Collect(root.Members, "", "", namespaces, types);
        if (synthesizeEntryPoint)
            AddTopLevelStatementEntryPoint(root, types);
        return new FileEntry(mtimeTicks, length, namespaces, types);
    }

    /// <summary>
    /// Records the compiler-synthesized entry point of a top-level-statements file as an ordinary
    /// <c>Program.Main</c> type-and-member, so the syntax tier can locate it like any other declaration.
    /// </summary>
    /// <remarks>
    /// Top-level statements declare no type node, so <c>Collect</c> walks straight past them and the file
    /// contributes nothing here. <c>SymbolIndexBuilder.IndexTopLevelStatements</c> does record the semantic
    /// entry point, deliberately under the name "Program.Main" so it is findable — but
    /// <c>ProjectIndex.LocateWithDocs</c> only offers location keys built from <see cref="TypeEntry"/> and
    /// <see cref="MemberEntry"/>, so that row matched nothing and came back locationless. Every response
    /// needing a site then dropped it silently: search_index returned the row with no file/line, and ANY
    /// pathPrefix filter excluded it outright, since an unlocated hit is treated as out of scope. That is
    /// the same shape as the generic-method key bug fixed in LocateWithDocs — the row exists, the location
    /// key never matches — and it made Program.cs look unreachable through the tools.
    /// </remarks>
    private static void AddTopLevelStatementEntryPoint(CompilationUnitSyntax root, List<TypeEntry> types)
    {
        if (!root.Members.OfType<GlobalStatementSyntax>().Any())
            return;

        // The synthesized type is named Program whatever the file is called, and the whole compilation
        // unit is its declaration — matching the span get_symbol reports for the semantic entry point, so
        // the two tiers agree rather than offering a caller two different line ranges for one symbol.
        var span = root.GetLocation().GetLineSpan();
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        const string EntryPointType = "Program";

        types.Add(new TypeEntry(
            "Type", EntryPointType, EntryPointType, Namespace: "", Doc: null, Bases: [], Modifiers: "",
            startLine, endLine,
            [new MemberEntry("Method", "Main", "Main()", Doc: null, startLine, endLine, IsPublic: false)],
            [], IsPublic: false));
    }

    private static void Collect(
        SyntaxList<MemberDeclarationSyntax> members,
        string containerFq,
        string ns,
        List<string> namespaces,
        List<TypeEntry> types)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax nsDecl:
                    var nsName = Combine(containerFq, nsDecl.Name.ToString());
                    if (!namespaces.Contains(nsName))
                        namespaces.Add(nsName);
                    Collect(nsDecl.Members, nsName, nsName, namespaces, types);
                    break;
                case BaseTypeDeclarationSyntax type:
                    types.Add(BuildType(type, containerFq, ns));
                    break;
                case DelegateDeclarationSyntax del:
                    types.Add(BuildDelegate(del, containerFq, ns));
                    break;
            }
        }
    }

    private static TypeEntry BuildType(BaseTypeDeclarationSyntax type, string containerFq, string ns)
    {
        var name = type.Identifier.Text + (type is TypeDeclarationSyntax { TypeParameterList: { } tp } ? RenderTypeParameters(tp) : "");
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
                members.Add(new MemberEntry("F", m.Identifier.Text, sig, DocSummary(m), Line(m), EndLine(m), true, DocSectionTags(m)));
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
                        nested.Add(BuildType(nestedType, fq, ns));
                        break;
                    case DelegateDeclarationSyntax nestedDel:
                        nested.Add(BuildDelegate(nestedDel, fq, ns));
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
                members.Insert(0, new MemberEntry("K", td.Identifier.Text, $"{td.Identifier.Text}{RenderParams(rp)}", null, Line(td), EndLine(td), true));
        }

        return new TypeEntry(kind, name, fq, ns, DocSummary(type), bases, type.Modifiers.ToString(), Line(type), EndLine(type), members, nested, IsPublic(type.Modifiers), DocSectionTags(type));
    }

    private static TypeEntry BuildDelegate(DelegateDeclarationSyntax del, string containerFq, string ns)
    {
        var name = del.Identifier.Text + RenderTypeParameters(del.TypeParameterList);
        var fq = Combine(containerFq, name);
        var sigMember = new MemberEntry(
            "M", name, $"{name}{RenderParams(del.ParameterList)} -> {del.ReturnType}", null, Line(del), EndLine(del), true);
        return new TypeEntry("D", name, fq, ns, DocSummary(del), [], del.Modifiers.ToString(), Line(del), EndLine(del),
            [sigMember], [], IsPublic(del.Modifiers), DocSectionTags(del));
    }

    // The stored name is matched against the form the Roslyn-derived symbol store asks for, and that form
    // is canonical -- "<TKey, TValue>", the identifiers only, one space after each comma. ToString() would
    // reproduce whatever the source wrote instead, so "class Cache<TKey,TValue>" or an attributed or
    // variant parameter list came back locationless however the parameters were named.
    private static string RenderTypeParameters(TypeParameterListSyntax? list) =>
        list is null ? "" : $"<{string.Join(", ", list.Parameters.Select(p => p.Identifier.Text))}>";

    private static MemberEntry? BuildMember(MemberDeclarationSyntax member, bool isInterface)
    {
        var isPublic = isInterface || IsPublicOrProtected(member.Modifiers);
        switch (member)
        {
            case MethodDeclarationSyntax m:
            {
                var name = m.Identifier.Text + RenderTypeParameters(m.TypeParameterList);
                return new MemberEntry("M", m.Identifier.Text,
                    $"{name}{RenderParams(m.ParameterList)} -> {m.ReturnType}",
                    DocSummary(m), Line(m), EndLine(m), isPublic, DocSectionTags(m));
            }
            case ConstructorDeclarationSyntax c:
                return new MemberEntry("K", c.Identifier.Text,
                    $"{c.Identifier.Text}{RenderParams(c.ParameterList)}",
                    DocSummary(c), Line(c), EndLine(c), isPublic, DocSectionTags(c));
            case PropertyDeclarationSyntax p:
                return new MemberEntry("P", p.Identifier.Text,
                    $"{p.Identifier.Text}: {p.Type} {Accessors(p)}",
                    DocSummary(p), Line(p), EndLine(p), isPublic, DocSectionTags(p));
            case IndexerDeclarationSyntax ix:
                return new MemberEntry("P", "this[]",
                    $"this[{RenderParamList(ix.ParameterList.Parameters)}]: {ix.Type}",
                    DocSummary(ix), Line(ix), EndLine(ix), isPublic, DocSectionTags(ix));
            case FieldDeclarationSyntax f:
            {
                var v = f.Declaration.Variables.First();
                return new MemberEntry("F", v.Identifier.Text,
                    $"{v.Identifier.Text}: {f.Declaration.Type}",
                    DocSummary(f), Line(f), EndLine(f), isPublic, DocSectionTags(f));
            }
            case EventFieldDeclarationSyntax ef:
            {
                var v = ef.Declaration.Variables.First();
                return new MemberEntry("V", v.Identifier.Text,
                    $"{v.Identifier.Text}: {ef.Declaration.Type}",
                    DocSummary(ef), Line(ef), EndLine(ef), isPublic, DocSectionTags(ef));
            }
            case EventDeclarationSyntax e:
                return new MemberEntry("V", e.Identifier.Text,
                    $"{e.Identifier.Text}: {e.Type}",
                    DocSummary(e), Line(e), EndLine(e), isPublic, DocSectionTags(e));
            case OperatorDeclarationSyntax op:
                return new MemberEntry("M", $"operator {op.OperatorToken.Text}",
                    $"operator {op.OperatorToken.Text}{RenderParams(op.ParameterList)} -> {op.ReturnType}",
                    DocSummary(op), Line(op), EndLine(op), isPublic, DocSectionTags(op));
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

    /// <summary>
    /// The last line a declaration's own syntax occupies (trailing trivia excluded) — a cheap
    /// fetch-strategy signal for whether get_symbol's <c>source</c> component is worth requesting,
    /// without changing what <see cref="Line"/> itself points at.
    /// </summary>
    private static int EndLine(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

    private static string Combine(string container, string name) =>
        container.Length == 0 ? name : $"{container}.{name}";

    // A type with no explicit access modifier defaults to internal at namespace scope and private when
    // nested (C# language default, ECMA-334 15.3.6) -- neither case is public, so this is a plain check
    // for an explicit `public` keyword rather than any container-dependent fallback.
    private static bool IsPublic(SyntaxTokenList modifiers) =>
        modifiers.Any(t => t.IsKind(SyntaxKind.PublicKeyword));

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

    /// <summary>
    /// Comma-joined list of XML doc tags present on a declaration's doc comment beyond plain
    /// summary text — e.g. "summary,remarks,returns" — the presence signal search_index's xmlDoc
    /// filter checks against. Null when the doc comment has none of the recognized tags, same
    /// absent-means-absent convention as <see cref="DocSummary"/>.
    /// </summary>
    internal static string? DocSectionTags(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia()
            .Select(t => t.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .FirstOrDefault();
        if (trivia is null)
            return null;

        var sections = SectionsFromXml(trivia.ToFullString());
        if (sections is null)
            return null;

        var tags = new List<string>();
        if (sections.Summary is not null) tags.Add("summary");
        if (sections.Returns is not null) tags.Add("returns");
        if (sections.Remarks is not null) tags.Add("remarks");
        if (sections.Value is not null) tags.Add("value");
        if (sections.Inheritdoc is true) tags.Add("inheritdoc");
        if (sections.Params is { Count: > 0 }) tags.Add("params");
        if (sections.TypeParams is { Count: > 0 }) tags.Add("typeparams");
        if (sections.Exceptions is { Count: > 0 }) tags.Add("exceptions");
        return tags.Count == 0 ? null : string.Join(",", tags);
    }

    /// <summary>Extracts the &lt;summary&gt; text from raw doc-comment XML (also used for ISymbol docs).</summary>
    public static string? SummaryFromXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        var match = SummaryRegex().Match(xml);
        return match.Success ? Clean(match.Groups[1].Value) : null;
    }

    /// <summary>
    /// Extracts every documented section from raw doc-comment XML (also used for ISymbol docs) as a
    /// structured breakdown instead of just the &lt;summary&gt;. Null when none of the recognized tags
    /// are present, so a symbol with no doc comment at all omits the field entirely rather than
    /// returning an all-null shell.
    /// </summary>
    public static XmlDocSections? SectionsFromXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        var summary = SummaryRegex().Match(xml) is { Success: true } s ? Clean(s.Groups[1].Value) : null;
        var returns = ReturnsRegex().Match(xml) is { Success: true } r ? Clean(r.Groups[1].Value) : null;
        var remarks = RemarksRegex().Match(xml) is { Success: true } m ? Clean(m.Groups[1].Value) : null;
        var value = ValueRegex().Match(xml) is { Success: true } v ? Clean(v.Groups[1].Value) : null;
        var inheritdoc = InheritdocRegex().IsMatch(xml) ? true : (bool?)null;
        var exceptions = ExceptionRegex().Matches(xml)
            .Select(m => new XmlDocException(MemberNameFromCref(m.Groups[1].Value), Clean(m.Groups[2].Value) ?? ""))
            .Where(e => e.Text.Length > 0)
            .ToArray();
        var parameters = ParamRegex().Matches(xml)
            .Select(m => new XmlDocParam(m.Groups[1].Value, Clean(m.Groups[2].Value) ?? ""))
            .Where(p => p.Text.Length > 0)
            .ToArray();
        var typeParams = TypeParamRegex().Matches(xml)
            .Select(m => new XmlDocParam(m.Groups[1].Value, Clean(m.Groups[2].Value) ?? ""))
            .Where(p => p.Text.Length > 0)
            .ToArray();

        if (summary is null && returns is null && remarks is null && value is null && inheritdoc is null
            && exceptions.Length == 0 && parameters.Length == 0 && typeParams.Length == 0)
            return null;

        return new XmlDocSections(
            summary, returns, remarks, value, inheritdoc,
            parameters.Length == 0 ? null : parameters,
            typeParams.Length == 0 ? null : typeParams,
            exceptions.Length == 0 ? null : exceptions);
    }

    private static string? Clean(string raw)
    {
        var text = raw.Replace("///", " ");
        text = CrefRegex().Replace(text, m => MemberNameFromCref(m.Groups[1].Value));
        text = TagRegex().Replace(text, "");
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// The last dotted segment of a cref's member name — but a Roslyn-compiled cref (from
    /// ISymbol.GetDocumentationCommentXml, not the raw source trivia) packs a generic method's arity
    /// and a parameterized member's whole encoded parameter list into the same attribute value, e.g.
    /// <c>Of``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Collections.Generic.
    /// IReadOnlyList{System.Object}})</c>. Splitting that whole string on '.' lands inside the parameter
    /// list's own dots instead of on the member name, so the arity marker (`` ` ``) or parameter list
    /// (<c>(</c>) is truncated off first.
    /// </summary>
    private static string MemberNameFromCref(string raw)
    {
        var cut = raw.IndexOfAny(['`', '(']);
        if (cut >= 0)
            raw = raw[..cut];
        return raw.Split('.').Last();
    }

    [GeneratedRegex(@"<summary>([\s\S]*?)</summary>")]
    private static partial Regex SummaryRegex();

    [GeneratedRegex(@"<returns>([\s\S]*?)</returns>")]
    private static partial Regex ReturnsRegex();

    [GeneratedRegex(@"<remarks>([\s\S]*?)</remarks>")]
    private static partial Regex RemarksRegex();

    [GeneratedRegex(@"<value>([\s\S]*?)</value>")]
    private static partial Regex ValueRegex();

    [GeneratedRegex(@"<param\s+name=""([^""]+)""\s*>([\s\S]*?)</param>")]
    private static partial Regex ParamRegex();

    [GeneratedRegex(@"<typeparam\s+name=""([^""]+)""\s*>([\s\S]*?)</typeparam>")]
    private static partial Regex TypeParamRegex();

    [GeneratedRegex(@"<inheritdoc(?:\s+cref=""(?:[A-Z]:)?[^""]+"")?\s*/>")]
    private static partial Regex InheritdocRegex();

    [GeneratedRegex(@"<exception\s+cref=""(?:[A-Z]:)?([^""]+)""\s*>([\s\S]*?)</exception>")]
    private static partial Regex ExceptionRegex();

    [GeneratedRegex(@"<see\w*\s+\w+=""(?:[A-Z]:)?([^""]+)""\s*/?>")]
    private static partial Regex CrefRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

/// <summary>Structured breakdown of a symbol's XML doc comment beyond the plain &lt;summary&gt; string.</summary>
public sealed record XmlDocSections(
    string? Summary, string? Returns, string? Remarks, string? Value, bool? Inheritdoc,
    IReadOnlyList<XmlDocParam>? Params, IReadOnlyList<XmlDocParam>? TypeParams,
    IReadOnlyList<XmlDocException>? Exceptions);

/// <summary>One &lt;param&gt;/&lt;typeparam&gt; entry: the parameter's name and its documented text.</summary>
public sealed record XmlDocParam(string Name, string Text);

/// <summary>One &lt;exception cref="..."&gt; entry: the exception's simple type name and its documented text.</summary>
public sealed record XmlDocException(string Type, string Text);
