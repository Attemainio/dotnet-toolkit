namespace DotnetToolkit.McpServer.Output;

/// <summary>
/// The counted facts one symbol's <see cref="SymbolShape"/> column is rendered from — its own surface
/// (parameters, members, nested types), what fetching it costs (lines, body-outline landmarks), and what
/// that fetch would contain (doc lines, comment lines, attributes).
/// </summary>
/// <remarks>
/// A null count is a fact the symbol's KIND cannot have — a method has no members, a field has no
/// parameters — while a zero is a measured absence. Both render as nothing, so the distinction matters
/// only to the code that POPULATES this, which decides which letters a kind can ever show by leaving the
/// rest null rather than passing 0. That is deliberately the only place kind is reasoned about: the
/// renderer stays a renderer, and there is no kind-to-letters table to drift out of step with the
/// gatherers.
/// </remarks>
/// <param name="ParameterCount">Declared parameters, for a method-like symbol or a delegate; null otherwise.</param>
/// <param name="MemberCount">Members a type declares, private ones included; null on anything but a type.</param>
/// <param name="NestedCount">Types declared inside this type; null on anything but a type.</param>
/// <param name="LineCount">Lines the declaration itself occupies; null when its location did not resolve.</param>
/// <param name="LandmarkCount">Control-flow landmarks a body outline would report; null without an executable body.</param>
/// <param name="DocLines">Lines of XML doc comment on the declaration.</param>
/// <param name="CommentLines">Lines of non-doc comment; on a type, the transitive total across its members.</param>
/// <param name="AttributeCount">C# attributes applied to the declaration.</param>
public readonly record struct ShapeFacts(
    int? ParameterCount = null,
    int? MemberCount = null,
    int? NestedCount = null,
    int? LineCount = null,
    int? LandmarkCount = null,
    int DocLines = 0,
    int CommentLines = 0,
    int AttributeCount = 0)
{
    /// <summary>
    /// Line count for an inclusive start/end line pair, or null when either is unknown or the pair is
    /// inverted — the one place that arithmetic lives, so every producer of a shape agrees on it.
    /// </summary>
    /// <param name="line">The declaration's first line, or null for a site that did not resolve.</param>
    /// <param name="endLine">The declaration's last line, or null as for <paramref name="line"/>.</param>
    /// <returns>The inclusive line count, or null when it cannot be computed.</returns>
    public static int? LinesBetween(int? line, int? endLine) =>
        line is { } start && endLine is { } end && end >= start ? end - start + 1 : null;
}
