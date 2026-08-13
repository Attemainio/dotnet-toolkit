using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Extensions.Logging;

namespace DotnetToolkit.McpServer.Control;

/// <summary>
/// Loopback TCP side channel for triggering an index rescan or workspace reload from outside the MCP
/// session - a hook script is a separate OS process with no access to the stdio pipe Claude Code and
/// this server talk over, so it has no other way to ask the running server to do anything. Bound to
/// 127.0.0.1 on an OS-assigned port; the resolved port is written to CacheDir/control.port as plain text
/// so a caller with filesystem access can find it. Not a security boundary - loopback-only, single-user,
/// single-machine dev tool, same trust level as the MCP stdio session itself.
///
/// One line in, one line out: "rescan" waits for a full ProjectIndex sweep and reports the result;
/// "reload" starts a background MSBuildWorkspace reload and returns immediately, since that can take
/// far longer than a hook's timeout - the caller polls workspace_status for completion the same way a
/// manual reload_workspace call would.
/// </summary>
public sealed class ControlServer
{
    private readonly SolutionLocator _locator;
    private readonly ProjectIndex _index;
    private readonly WorkspaceHost _workspace;
    private readonly SymbolIndexBuilder _indexBuilder;
    private readonly ILogger<ControlServer> _log;
    private readonly Telemetry.TelemetryRecorder _telemetry;
    private Task? _runTask;

    public ControlServer(
        SolutionLocator locator, ProjectIndex index, WorkspaceHost workspace,
        SymbolIndexBuilder indexBuilder, Telemetry.TelemetryRecorder telemetry, ILogger<ControlServer> log)
    {
        _locator = locator;
        _index = index;
        _workspace = workspace;
        _indexBuilder = indexBuilder;
        _telemetry = telemetry;
        _log = log;
    }

    public void Start() => _runTask = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        TcpListener listener;
        string portFile;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            portFile = Path.Combine(_locator.EnsureCacheDir(), "control.port");
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await File.WriteAllTextAsync(portFile, port.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Control channel failed to start; hooks will fall back to reminder text");
            return;
        }

        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = HandleAsync(client);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Control channel accept loop stopped");
        }
        finally
        {
            listener.Stop();
            try { File.Delete(portFile); } catch { /* best effort cleanup */ }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            var command = (await reader.ReadLineAsync())?.Trim();
            var response = command switch
            {
                ControlCommands.Rescan => await RescanAsync(),
                ControlCommands.Reload => Reload(),
                // Prefix rather than equality: this one carries a JSON payload after the command word.
                _ when command is not null
                    && command.StartsWith(ControlCommands.Meter + " ", StringComparison.Ordinal) =>
                    Meter(command[(ControlCommands.Meter.Length + 1)..]),
                _ => "err:unknown command",
            };
            await writer.WriteLineAsync(response);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Control channel request failed");
        }
    }

    private async Task<string> RescanAsync()
    {
        await _index.ForceRescanAsync();
        return $"ok:index rescanned ({_index.FileCount} files, {_index.TypeCount} types)";
    }

    private string Reload()
    {
        _workspace.TriggerReload();
        _indexBuilder.Start();
        return "ok:reload started";
    }

    /// <summary>Records one metered tool call reported by the <c>meter-tool-call</c> hook.</summary>
    /// <param name="json">The measurement, one line of JSON following the command word.</param>
    /// <returns>A one-line status the hook ignores — it can do nothing useful about a failure.</returns>
    /// <remarks>
    /// Recording happens here rather than in the hook process so the row carries THIS server's session id.
    /// A hook is a separate process with its own ambient id, and a row stamped with that would be
    /// invisible to every read, since get_retrieval_metrics is scoped to the server's own session.
    /// Routing through the channel also keeps SQLite single-writer, which is what makes a hook firing on
    /// every tool call safe against a store that sets no busy timeout.
    /// </remarks>
    private string Meter(string json)
    {
        try
        {
            var measurement = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (measurement?["toolName"]?.GetValue<string>() is not { } toolName
                || measurement["toolUseId"]?.GetValue<string>() is not { } toolUseId)
            {
                return "err:meter payload missing toolName or toolUseId";
            }

            // A hook runs inside the LIVE Claude Code session and reports the session id it will itself look
            // a suspension up under. This process holds only the id it inherited at launch, which goes stale
            // the moment the session is resumed - so the hook's value outranks it.
            Hooks.GuardSuspension.ObserveSessionId(measurement["guardSessionId"]?.GetValue<string>());

            _telemetry.RecordToolCall(new Telemetry.TelemetryRecorder.ToolCallEvent
            {
                ToolName = toolName,
                ToolUseId = toolUseId,
                RequestTokens = measurement["requestTokens"]?.GetValue<int>() ?? 0,
                ResponseTokens = measurement["responseTokens"]?.GetValue<int>() ?? 0,
                TokenEstimator = measurement["estimator"]?.GetValue<string>() ?? "unknown",
                ClaudeSessionId = measurement["claudeSessionId"]?.GetValue<string>(),
                AgentId = measurement["agentId"]?.GetValue<string>(),
                AgentType = measurement["agentType"]?.GetValue<string>(),
            });
            return "ok:metered";
        }
        catch (Exception ex)
        {
            // A measurement must never break the channel the reload hint also depends on.
            _log.LogWarning(ex, "Control channel could not record a metered tool call");
            return "err:meter failed";
        }
    }
}
