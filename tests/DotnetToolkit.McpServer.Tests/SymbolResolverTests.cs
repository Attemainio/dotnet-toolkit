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
