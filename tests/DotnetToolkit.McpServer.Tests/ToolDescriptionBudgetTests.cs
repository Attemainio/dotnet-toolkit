using System.ComponentModel;
using System.Reflection;
using System.Text;

using ModelContextProtocol.Server;
using Xunit;

using DotnetToolkit.McpServer.Tools;

namespace DotnetToolkit.McpServer.Tests;

/// <summary>
/// Guards every MCP tool's method-level <see cref="DescriptionAttribute"/> against the client-side
/// size limit that silently truncates an over-long one.
/// </summary>
public class ToolDescriptionBudgetTests
{
    /// <summary>
    /// The client truncates a tool description at 2 KB, dropping its tail rather than erroring — and
    /// the tail is where the <c>docs/tools/&lt;tool&gt;.md</c> pointer sits, so a tool that overruns
    /// loses the one link out to its own manual. Measured: 2032 bytes arrived intact and 2105 did
    /// not. This budget keeps ~150 bytes of headroom so a single added sentence does not silently
    /// cross the line between two edits.
    /// </summary>
    private const int MaxDescriptionBytes = 1900;

    public static TheoryData<string, int> ToolDescriptions()
    {
        var data = new TheoryData<string, int>();
        foreach (var (tool, method) in DiscoverTools())
        {
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
            data.Add(tool, Encoding.UTF8.GetByteCount(description));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ToolDescriptions))]
    public void ToolDescription_StaysWithinClientTruncationBudget(string tool, int byteCount)
    {
        Assert.True(
            byteCount is > 0 and <= MaxDescriptionBytes,
            $"{tool}'s [Description] is {byteCount} bytes; the budget is {MaxDescriptionBytes}. "
                + "Move grammar, performance figures and response detail into docs/tools/, and keep the "
                + "description to what the tool is for, how it is commonly called, and its manual's name.");
    }

    /// <summary>
    /// Without this, a reflection query that matched nothing would leave the theory with no cases and
    /// pass vacuously — the budget would stop being enforced with no test turning red.
    /// </summary>
    [Fact]
    public void ToolDiscovery_FindsTheRegisteredTools() =>
        Assert.NotEmpty(DiscoverTools());

    private static List<(string Tool, MethodInfo Method)> DiscoverTools() =>
        typeof(ContextTools).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Select(method => (Attribute: method.GetCustomAttribute<McpServerToolAttribute>(), Method: method))
            .Where(candidate => candidate.Attribute is not null)
            .Select(candidate => (Tool: candidate.Attribute!.Name ?? candidate.Method.Name, candidate.Method))
            .ToList();
}
