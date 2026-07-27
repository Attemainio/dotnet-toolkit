using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetToolkit.McpServer.Fingerprint;

/// <summary>
/// One control-flow landmark inside a member's body (spec §9 <c>bodyOutline</c>): a construct that
/// carries a name or condition worth navigating to directly, with its absolute file line span and its
/// nesting depth among other landmarks. Anonymous constructs (a bare <c>try</c>, an <c>else</c>, a
/// <c>finally</c>) carry no such text and are omitted — their span is inferable from the parent row that
/// does appear, so listing them would only restate a boundary the reader already has.
/// </summary>
public sealed record OutlineRow(string Text, int StartLine, int EndLine, int Depth);

/// <summary>
/// Extracts <see cref="OutlineRow"/>s from a declaration's syntax tree — purely syntactic, no semantic
/// model needed, so it costs the same tier as <c>source</c> rather than the semantic-model tier
/// <see cref="MechanicalFactsExtractor"/> pays. Text is truncated to a fixed character budget, not
/// semantically summarized: it names the construct well enough to decide whether to fetch that span, not
/// to replace reading it.
/// </summary>
public static class BodyOutlineExtractor
{
    private const int MaxTextLength = 28;

    /// <summary>Landmark rows for one member's declaration, in document order. Never null; empty when the body has none.</summary>
    public static IReadOnlyList<OutlineRow> Extract(SyntaxNode declaration)
    {
        var landmarks = new List<(SyntaxNode Node, string Text)>();
        foreach (var node in declaration.DescendantNodes())
        {
            if (LandmarkText(node) is { } text)
                landmarks.Add((node, text));
        }

        // Depth is nesting among landmarks specifically, not raw syntax depth - a landmark buried inside
        // several plain blocks/statements should not look deeper than one actually nested inside another
        // landmark by one level.
        var nodeSet = new HashSet<SyntaxNode>(landmarks.Select(l => l.Node));
        var rows = new List<OutlineRow>(landmarks.Count);
        foreach (var (node, text) in landmarks)
        {
            var span = node.GetLocation().GetLineSpan();
            var depth = node.Ancestors().Count(nodeSet.Contains);
            rows.Add(new OutlineRow(text, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, depth));
        }
        return rows;
    }

    private static string? LandmarkText(SyntaxNode node) => node switch
    {
        SwitchStatementSyntax s => $"switch({Truncate(s.Expression.ToString())})",
        SwitchExpressionSyntax s => $"switch({Truncate(s.GoverningExpression.ToString())})",
        SwitchSectionSyntax s => $"case {string.Join(", ", s.Labels.Select(LabelText))}",
        SwitchExpressionArmSyntax s => $"case {Truncate(s.Pattern.ToString())}",
        IfStatementSyntax s => $"if ({Truncate(s.Condition.ToString())})",
        ForEachStatementSyntax s => $"foreach({s.Identifier.Text})",
        ForEachVariableStatementSyntax s => $"foreach({Truncate(s.Variable.ToString())})",
        ForStatementSyntax s => $"for({Truncate(ForHeaderText(s))})",
        WhileStatementSyntax s => $"while({Truncate(s.Condition.ToString())})",
        DoStatementSyntax s => $"do-while({Truncate(s.Condition.ToString())})",
        // CatchDeclarationSyntax.ToString() already includes its own "(Type id)" parens - do not wrap
        // it in another pair, or a typed catch renders as "catch((Type id))".
        CatchClauseSyntax { Declaration: { } d } => $"catch{Truncate(d.ToString())}",
        CatchClauseSyntax => "catch",
        UsingStatementSyntax s => $"using({Truncate(UsingHeaderText(s))})",
        LockStatementSyntax s => $"lock({Truncate(s.Expression.ToString())})",
        _ => null,
    };

    private static string LabelText(SwitchLabelSyntax label) => label switch
    {
        CasePatternSwitchLabelSyntax { Pattern: DeclarationPatternSyntax d } => StripSyntaxSuffix(d.Type.ToString()),
        CasePatternSwitchLabelSyntax p => Truncate(p.Pattern.ToString()),
        CaseSwitchLabelSyntax c => Truncate(c.Value.ToString()),
        DefaultSwitchLabelSyntax => "default",
        _ => Truncate(label.ToString()),
    };

    private static string ForHeaderText(ForStatementSyntax s) =>
        s.Condition?.ToString()
        ?? s.Declaration?.ToString()
        ?? string.Join(", ", s.Initializers.Select(i => i.ToString()));

    private static string UsingHeaderText(UsingStatementSyntax s) =>
        s.Declaration?.ToString() ?? s.Expression?.ToString() ?? string.Empty;

    // Roslyn syntax type names all end in "Syntax" ("ThrowStatementSyntax"), which is redundant noise in
    // a case label that already reads as C# pattern-matching code - trimming it is the one C#-specific
    // shorthand this extractor applies, everything else is a plain character-budget truncation.
    private static string StripSyntaxSuffix(string typeName) =>
        typeName.EndsWith("Syntax", StringComparison.Ordinal) ? typeName[..^"Syntax".Length] : typeName;

    private static string Truncate(string text)
    {
        // Collapse to one line before measuring - a multi-line condition would otherwise blow the row's
        // line budget worse than its character budget, and a landmark row is meant to stay one line.
        var flattened = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flattened.Length <= MaxTextLength ? flattened : flattened[..MaxTextLength].TrimEnd() + "..";
    }
}
