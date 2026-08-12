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
    /// The whole schema a tool ships -- its own description PLUS every parameter's -- is what reaches the
    /// model on every request, not just the method-level one <see cref="MaxDescriptionBytes"/> guards. A tool
    /// can therefore sit comfortably inside that budget while its arguments quietly cost several times as
    /// much, which is how get_symbol's include grammar grew: every addition was individually small, and
    /// nothing measured the sum. This is the ceiling on the sum.
    /// </summary>
    /// <remarks>
    /// Looser per byte than the description budget on purpose, because a parameter description carries
    /// grammar a caller cannot reach anywhere else at the moment of the call. What it enforces is the
    /// division of labour, not brevity for its own sake: a description says what an argument is FOR and what
    /// shape it takes, and <c>docs/tools/&lt;tool&gt;.md</c> owns the full grammar, the worked examples and
    /// the response contract.
    /// </remarks>
    /// <para>
    /// 5000 comes from measuring every shipped tool rather than from taste. The spread was 241 (ping) to
    /// 7404 (search_index), with the next-largest at 3511 -- so search_index alone cost more than twice what
    /// a filter-heavy tool needs, and the ceiling is set to bind on that one outlier while leaving every
    /// other tool real headroom. Lower it when the fleet moves down; a budget nothing can fail is not one.
    /// </para>
    private const int MaxSchemaBytes = 5000;

    public static TheoryData<string, int> ToolSchemas()
    {
        var data = new TheoryData<string, int>();
        foreach (var (tool, method) in DiscoverTools())
        {
            var total = Encoding.UTF8.GetByteCount(
                method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty);
            foreach (var parameter in method.GetParameters())
            {
                total += Encoding.UTF8.GetByteCount(
                    parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty);
            }

            data.Add(tool, total);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ToolSchemas))]
    public void ToolSchema_StaysWithinItsTotalBudget(string tool, int byteCount)
    {
        Assert.True(
            byteCount is > 0 and <= MaxSchemaBytes,
            $"{tool}'s schema (description + every parameter description) is {byteCount} bytes; the budget "
                + $"is {MaxSchemaBytes}. That whole total ships on every request. Move grammar and worked "
                + "examples into docs/tools/, and keep each parameter description to what the argument is for "
                + "and what shape it takes.");
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
