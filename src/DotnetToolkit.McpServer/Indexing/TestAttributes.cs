using Microsoft.CodeAnalysis;

namespace DotnetToolkit.McpServer.Indexing;

/// <summary>
/// Recognises a test method by the attributes on its own declaration.
///
/// This replaced a project-level check (does the project reference xunit?), which decided test-ness
/// from how completely MSBuild had loaded the project on a given pass. Nothing could invalidate that:
/// the incremental indexer rewrites rows only when a CONTENT hash moves, and content cannot express
/// "the environment that produced this row was wrong", so a degraded load left rows permanently
/// mismarked (Schema migration 8). An attribute lives in the declaration, so changing it moves the
/// declaration hash and the row is rewritten exactly when the answer changes.
///
/// One definition, used by both the indexer that stores the flag and the read path that reports it —
/// two copies of this rule could disagree, which is the failure it exists to prevent.
/// </summary>
public static class TestAttributes
{
    /// <summary>
    /// Attribute names across the frameworks a .NET repo actually uses: xunit ([Fact], [Theory]),
    /// NUnit ([Test], [TestCase], [TestCaseSource]) and MSTest ([TestMethod], [DataTestMethod]).
    /// Matched on the attribute class's own name, so the plugin references none of them.
    /// </summary>
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "FactAttribute", "TheoryAttribute",
        "TestAttribute", "TestCaseAttribute", "TestCaseSourceAttribute",
        "TestMethodAttribute", "DataTestMethodAttribute",
    };

    /// <summary>
    /// True when this symbol is itself a test — not merely a member of a test project. A helper in a
    /// test file is not a test, and counting it as one overstates coverage.
    /// </summary>
    public static bool IsTestMethod(ISymbol symbol) =>
        symbol is IMethodSymbol
        && symbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name is { } name && Names.Contains(name));
}
