using DotnetToolkit.McpServer.Workspace;
using Xunit;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// The name search_index emits. It has to stay unambiguous and resolvable while not spending most of its
/// characters repeating a namespace once per parameter.
/// </summary>
public class CompactNameTests
{
    /// <summary>
    /// The container path is what disambiguates across namespaces, so it stays; parameter types are
    /// matched by short name anyway, so their prefixes are dead weight. The whitespace SEPARATING them
    /// stays too — without it, a tuple element or a modifier reads as part of the type name.
    /// </summary>
    [Fact]
    public void KeepsTheContainerPathAndShortensParameterTypes()
    {
        var compact = SymbolResolver.CompactName(
            "DotnetToolkit.McpServer.Tools.ContextTools.SearchIndex("
            + "DotnetToolkit.McpServer.Store.SymbolStore, DotnetToolkit.McpServer.Indexing.ProjectIndex, int)");

        Assert.Equal(
            "DotnetToolkit.McpServer.Tools.ContextTools.SearchIndex(SymbolStore, ProjectIndex, int)", compact);
    }

    [Fact]
    public void LeavesAParameterlessNameAlone()
    {
        const string Name = "DotnetToolkit.McpServer.Store.SymbolStore";

        Assert.Equal(Name, SymbolResolver.CompactName(Name));
    }

    /// <summary>Generic arguments survive; they are part of what tells two overloads apart.</summary>
    [Fact]
    public void PreservesGenericArgumentsInsideParameterTypes()
    {
        var compact = SymbolResolver.CompactName(
            "Lib.Store.Write(System.Collections.Generic.IReadOnlyList<string>, int)");

        Assert.Equal("Lib.Store.Write(IReadOnlyList<string>, int)", compact);
    }

    /// <summary>Overloads must not collapse into the same string — that is the whole reason to keep params.</summary>
    [Fact]
    public void KeepsOverloadsDistinguishable()
    {
        var one = SymbolResolver.CompactName("Lib.Store.Get(System.String)");
        var two = SymbolResolver.CompactName("Lib.Store.Get(System.String, System.Int32)");

        Assert.NotEqual(one, two);
    }
}

/// <summary>
/// Covers <see cref="SymbolResolver.ParameterArity"/>, which is what tells the members of an overload
/// set apart once the syntax index has dropped their parameter lists.
/// </summary>
public class ParameterArityTests
{
    /// <summary>A name with no parameter list is not a zero-parameter member — it is not a member call at all.</summary>
    [Fact]
    public void ReportsMinusOneForANameWithoutAParameterList()
    {
        Assert.Equal(-1, SymbolResolver.ParameterArity("Lib.Store.Count"));
    }

    [Fact]
    public void CountsAnEmptyParameterListAsZero()
    {
        Assert.Equal(0, SymbolResolver.ParameterArity("Lib.Store.Clear()"));
    }

    /// <summary>The store's form: types only, no spaces, no names.</summary>
    [Fact]
    public void CountsTheStoresTypeOnlyParameterList()
    {
        Assert.Equal(2, SymbolResolver.ParameterArity("Lib.Store.Get(System.String,System.Int32)"));
    }

    /// <summary>The syntax index's form: named parameters, defaults, and a return type after the list.</summary>
    [Fact]
    public void CountsTheIndexSignatureFormIncludingDefaults()
    {
        Assert.Equal(2, SymbolResolver.ParameterArity("Get(string key, int limit = 10) -> string"));
    }

    /// <summary>A comma inside a generic argument or tuple belongs to the type, not to the parameter list.</summary>
    [Fact]
    public void IgnoresCommasNestedInsideAParameterType()
    {
        Assert.Equal(1, SymbolResolver.ParameterArity("Lib.Store.Put(Dictionary<string, int> entries)"));
        Assert.Equal(1, SymbolResolver.ParameterArity("Lib.Store.Put(params (string Key, string Value)[] pairs)"));
    }
}

/// <summary>
/// Covers the two parameter-type keys, which are what tell SAME-ARITY overloads apart once the syntax
/// index has collapsed them onto one bare name and parameter count has run out of discriminating power.
/// </summary>
public class ParameterTypeKeyTests
{
    [Fact]
    public void ReportsNullForANameWithoutAParameterList()
    {
        Assert.Null(SymbolResolver.ParameterTypeKey("Lib.Store.Count"));
        Assert.Null(SymbolResolver.SignatureParameterTypeKey("Count: int"));
    }

    /// <summary>
    /// The store's type-only form and the index's named form must reduce to the same key. Both sides spell
    /// a special type the same way (<c>string</c>, not <c>System.String</c>) — a name that spells it the
    /// other way reduces differently and stays ambiguous, which is the safe outcome, not a match.
    /// </summary>
    [Fact]
    public void StoreAndSignatureFormsAgreeOnTheSameMember()
    {
        var stored = SymbolResolver.ParameterTypeKey("Lib.Store.Get(string, Lib.SortOrder)");
        var indexed = SymbolResolver.SignatureParameterTypeKey("Get(string key, SortOrder order) -> int");

        Assert.Equal(stored, indexed);
    }

    /// <summary>Modifiers are part of the type on both sides; a default value is part of neither.</summary>
    [Fact]
    public void KeepsModifiersAndDropsDefaults()
    {
        Assert.Equal(
            SymbolResolver.ParameterTypeKey("Lib.Store.TryGet(int, out T)"),
            SymbolResolver.SignatureParameterTypeKey("TryGet(int key, out T value) -> bool"));
        Assert.Equal(
            SymbolResolver.ParameterTypeKey("Lib.Log.Warn(string?, params object[])"),
            SymbolResolver.SignatureParameterTypeKey("Warn(string? message, params object[] args) -> void"));
        Assert.Equal(
            SymbolResolver.ParameterTypeKey("Lib.Store.Get(string)"),
            SymbolResolver.SignatureParameterTypeKey("Get(string key = \"a\") -> int"));
    }

    /// <summary>
    /// A tuple element name belongs to the type on both sides, so it must not be mistaken for the
    /// parameter's own name and dropped.
    /// </summary>
    [Fact]
    public void KeepsTupleElementNamesInsideAParameterType()
    {
        Assert.Equal(
            SymbolResolver.ParameterTypeKey(
                "Lib.Store.Put(System.Collections.Generic.List<(System.DateTime time, decimal amount)>)"),
            SymbolResolver.SignatureParameterTypeKey("Put(List<(DateTime time, decimal amount)> entries) -> void"));
    }

    /// <summary>The case parameter count alone cannot separate — and the one that lost its location.</summary>
    [Fact]
    public void SeparatesTwoOverloadsOfTheSameArity()
    {
        Assert.NotEqual(
            SymbolResolver.SignatureParameterTypeKey("Solve(string name, Comparison<double> compare)"),
            SymbolResolver.SignatureParameterTypeKey("Solve(string name, Comparer<double> compare)"));
    }
}

/// <summary>
/// Covers <see cref="SymbolResolver.MemberWithContainingType"/> — the display form call-hierarchy nodes
/// carry, which has to match what get_references renders from a live symbol.
/// </summary>
public class MemberWithContainingTypeTests
{
    [Fact]
    public void DropsTheNamespacePathAndKeepsTheContainingType()
    {
        Assert.Equal("ContextTools.GetSymbol",
            SymbolResolver.MemberWithContainingType("DotnetToolkit.McpServer.Tools.ContextTools.GetSymbol"));
    }

    /// <summary>A dot inside a generic argument is not a segment boundary of the name itself.</summary>
    [Fact]
    public void IgnoresDotsInsideGenericArguments()
    {
        Assert.Equal("FullStrategy<T>.PlotStrategyResults",
            SymbolResolver.MemberWithContainingType("Lib.Core.Strategy.FullStrategy<T>.PlotStrategyResults"));
    }

    [Fact]
    public void LeavesANameWithNoNamespacePathAlone()
    {
        Assert.Equal("Store.Get", SymbolResolver.MemberWithContainingType("Store.Get"));
    }
}
