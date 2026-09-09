using DotnetToolkit.McpServer.Store;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetToolkit.McpServer.Tools;

/// <summary>
/// Turns an exception escaping a tool into an error the caller can act on, and notices when the
/// knowledge store was the thing that failed.
/// </summary>
/// <remarks>
/// The MCP SDK's own handler catches whatever a tool throws and answers with the bare string
/// <c>An error occurred invoking '&lt;tool&gt;'</c> — no exception type, no message, no path. A caller
/// given that cannot tell a corrupt cache from a bug in its own arguments, and this plugin's skills
/// push it toward re-fetching and re-submitting, which is exactly what a damaged page will never
/// satisfy. One filter around every tool call is the whole fix, and it keeps tool signatures alone:
/// a <c>try</c>/<c>catch</c> per tool method would be eighteen copies of this drifting apart.
/// </remarks>
internal static class ToolFailureReporting
{
    /// <summary>Wraps every tool call so a failure reports what actually went wrong.</summary>
    /// <param name="builder">The MCP server builder to add the filter to.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IMcpServerBuilder WithFailureDetail(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(
            next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Describe(context, ex);
                }
            }));
    }

    /// <summary>Builds the error payload for a failed call, naming the store when the store is the cause.</summary>
    private static CallToolResult Describe(RequestContext<CallToolRequestParams> context, Exception ex)
    {
        var tool = context.Params?.Name ?? "tool";
        var services = context.Services;

        // Resolved per call rather than captured: the filter is registered while the container is
        // still being built, and a store that failed to open is still a registered singleton.
        var store = services?.GetService<KnowledgeStore>();
        var storeIsDamaged = store is not null && store.ReportFailure(ex);

        services?.GetService<ILoggerFactory>()?
            .CreateLogger(typeof(ToolFailureReporting))
            .LogError(ex, "Tool {Tool} failed", tool);

        var detail = storeIsDamaged && store?.HealthMessage is { } health
            ? health
            : $"{ex.GetType().Name}: {ex.Message}";

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"{tool} failed — {detail}" }],
        };
    }
}
