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
    /// matched by short name anyway, so their prefixes are dead weight.
    /// </summary>
    [Fact]
    public void KeepsTheContainerPathAndShortensParameterTypes()
    {
        var compact = SymbolResolver.CompactName(
            "DotnetToolkit.McpServer.Tools.ContextTools.SearchIndex("
            + "DotnetToolkit.McpServer.Store.SymbolStore, DotnetToolkit.McpServer.Indexing.ProjectIndex, int)");

        Assert.Equal(
            "DotnetToolkit.McpServer.Tools.ContextTools.SearchIndex(SymbolStore,ProjectIndex,int)", compact);
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

        Assert.Equal("Lib.Store.Write(IReadOnlyList<string>,int)", compact);
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
