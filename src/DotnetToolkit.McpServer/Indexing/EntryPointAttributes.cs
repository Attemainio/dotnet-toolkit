using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace DotnetToolkit.McpServer.Indexing;

/// <summary>
/// Recognises a method invoked by a framework's own reflection scan or routing convention — never by
/// any call site a static reference search, including get_references, can index. A zero-caller result
/// on one of these is indistinguishable from dead code unless something names the mechanism.
///
/// Grew out of a single case: TestAttributes existed first, for [Fact]-style methods a test runner
/// discovers by reflection. A later benchmark run (2026-08-17) caught the identical failure on an
/// [McpServerTool]-attributed method — this repo's own tool surface is exactly that shape — with zero
/// compile-time callers read as "safe to delete" by a route that trusted get_references' raw count.
/// The fix generalises past tests on purpose, to the other frameworks a .NET repo actually reflects
/// over, rather than growing one exception at a time as each is separately caught missing.
/// </summary>
public static class EntryPointAttributes
{
    /// <summary>
    /// Attribute names recognised as reflection-invoked entry points. Matched on the attribute
    /// class's own simple name, so this repo references none of the frameworks it recognises.
    /// </summary>
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        // ModelContextProtocol SDK: discovered by the server host's own reflection scan over the
        // assembly — exactly how this repo's own [McpServerTool] methods in Tools/*.cs are found.
        "McpServerToolAttribute", "McpServerPromptAttribute", "McpServerResourceAttribute",

        // ASP.NET Core: dispatched to by the routing middleware's endpoint table, never called
        // directly from the repo's own source.
        "HttpGetAttribute", "HttpPostAttribute", "HttpPutAttribute", "HttpDeleteAttribute",
        "HttpPatchAttribute", "HttpHeadAttribute", "HttpOptionsAttribute", "RouteAttribute",

        // System.Text.Json/Newtonsoft: the serializer constructs and invokes the converter once the
        // attribute wires it to a type or member, not from this repo's own call graph.
        "JsonConverterAttribute",

        // Run by the CLR when the containing module loads, before any of this repo's own code runs.
        "ModuleInitializerAttribute",
    };

    /// <summary>
    /// The reason this symbol is a known reflection-invoked entry point, or null if it carries none
    /// of the recognised attributes and is not the process entry point. Checks test-framework
    /// attributes first via <see cref="TestAttributes"/>, so a caller needs only this one method.
    /// </summary>
    public static string? MatchedReason(ISymbol symbol)
    {
        if (TestAttributes.IsTestMethod(symbol))
            return "a test-framework attribute ([Fact]/[Theory]/[Test]/[TestMethod] or similar) discovered by "
                + "its test runner's own reflection scan";

        var matchedName = symbol.GetAttributes()
            .Select(a => a.AttributeClass?.Name)
            .FirstOrDefault(name => name is not null && Names.Contains(name));
        if (matchedName is not null)
        {
            var display = matchedName.EndsWith("Attribute", StringComparison.Ordinal)
                ? matchedName[..^"Attribute".Length]
                : matchedName;
            return $"a [{display}] attribute discovered by its framework's own reflection scan or routing convention";
        }

        if (symbol is IMethodSymbol { IsStatic: true, Name: "Main" })
            return "the process entry point (a static Main), invoked by the runtime rather than by any call site "
                + "in this repo's own source";

        return null;
    }
}
