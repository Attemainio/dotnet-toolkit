using System.Diagnostics;
using System.Text.Json;

namespace DotnetToolkit.McpServer.Diagnostics;

/// <summary>What a live launch of an MCP server actually achieved, stage by stage.</summary>
/// <param name="Started">Whether the process was launched at all.</param>
/// <param name="Failure">Why the probe stopped short, when it did.</param>
/// <param name="ProtocolVersion">The MCP protocol version the server negotiated.</param>
/// <param name="ServerName">The server's self-reported name.</param>
/// <param name="ServerVersion">The server's self-reported version.</param>
/// <param name="Tools">Every tool name <c>tools/list</c> returned, in the order returned.</param>
/// <param name="Ping">What the <c>ping</c> tool replied.</param>
/// <param name="WorkspaceStatus">What the <c>workspace_status</c> tool replied.</param>
/// <param name="Stderr">Everything the server logged, which is where it names the SDK it registered.</param>
/// <remarks>
/// The stages are separate fields rather than one success flag because they fail independently and the
/// repairs differ: a process that never started is a path or permission problem, a handshake that never
/// completed is a startup crash, an empty tool list is a registration problem, and a degraded
/// <c>workspace_status</c> is an SDK or restore problem. Collapsing them into "it didn't work" throws
/// away the only information that says which.
/// </remarks>
internal sealed record McpProbeResult(
    bool Started,
    string? Failure,
    string? ProtocolVersion,
    string? ServerName,
    string? ServerVersion,
    IReadOnlyList<string> Tools,
    string? Ping,
    string? WorkspaceStatus,
    string Stderr);

/// <summary>
/// Launches an MCP server over stdio and drives a real session against it — initialize, tools/list,
/// ping, workspace_status — so a verifier can report what a session would actually get.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because inventory commands cannot fail.</strong> A registration listing reports
/// what a configuration file says; it launches nothing, so it reports a server as present and enabled
/// whether or not the command it names can run. That gap is how an installation reaches a user looking
/// healthy while every tool call is unavailable. Only actually starting the process and completing the
/// handshake distinguishes the two, so that is what this does.
/// </para>
/// <para>
/// The probe speaks the protocol by hand rather than through an MCP client library on purpose: a client
/// would negotiate on its own terms and surface a failure as an exception, while a diagnostic needs the
/// raw stage each step reached, plus the server's stderr, which is where startup reports the SDK it
/// registered and the projects that failed to load.
/// </para>
/// </remarks>
internal static class McpHandshakeProbe
{
    /// <summary>The protocol version the probe offers during initialization.</summary>
    private const string ProtocolVersion = "2025-06-18";

    /// <summary>How long to wait between polls while the server's background load finishes.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Runs one full probe session against a server command.</summary>
    /// <param name="command">The executable to launch.</param>
    /// <param name="arguments">Its arguments, in order.</param>
    /// <param name="environment">Extra environment variables to set for the launched process.</param>
    /// <param name="workingDirectory">The directory to start it in, which is the repository it will analyze.</param>
    /// <param name="timeout">How long to allow for the whole session before giving up.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>
    /// How far the session got. A probe that fails partway still returns everything it observed before
    /// the failure, because the last stage reached is the diagnosis.
    /// </returns>
    public static async Task<McpProbeResult> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }

        // The server shells out to `dotnet restore`, which spawns persistent MSBuild worker nodes that
        // inherit these redirected pipes and outlive the child we kill. Without both variables the probe
        // can hang on a pipe held open by a process it never started.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new McpProbeResult(
                Started: false,
                Failure: ex.Message,
                ProtocolVersion: null,
                ServerName: null,
                ServerVersion: null,
                Tools: [],
                Ping: null,
                WorkspaceStatus: null,
                Stderr: string.Empty);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;

        // Started before the first write and awaited only after the child is gone: an unread stderr pipe
        // fills its OS buffer and blocks the child forever, so both streams have to be in flight at once
        // rather than drained one after the other.
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        string? protocolVersion = null;
        string? serverName = null;
        string? serverVersion = null;
        IReadOnlyList<string> tools = [];
        string? ping = null;
        string? workspaceStatus = null;
        string? failure = null;

        try
        {
            var initialize = await ExchangeAsync(process, 1, "initialize", new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "dotnet-toolkit-doctor", version = "1" },
            }, token);

            if (initialize is null)
            {
                failure = "the server closed stdout before answering initialize";
            }
            else
            {
                protocolVersion = Text(initialize.Value, "protocolVersion");
                if (initialize.Value.TryGetProperty("serverInfo", out var info))
                {
                    serverName = Text(info, "name");
                    serverVersion = Text(info, "version");
                }

                await SendAsync(process, new { jsonrpc = "2.0", method = "notifications/initialized" }, token);

                var listed = await ExchangeAsync(process, 2, "tools/list", new { }, token);
                if (listed is { } list && list.TryGetProperty("tools", out var toolArray))
                {
                    tools = [.. toolArray.EnumerateArray()
                        .Select(tool => Text(tool, "name"))
                        .OfType<string>()];
                }

                ping = await CallToolAsync(process, 3, "ping", token);
                workspaceStatus = await AwaitSettledWorkspaceAsync(process, firstId: 4, token);
            }
        }
        catch (OperationCanceledException)
        {
            failure = $"no response within {timeout.TotalSeconds:0}s";
        }
        catch (IOException ex)
        {
            failure = $"the server's stdio closed: {ex.Message}";
        }
        catch (JsonException ex)
        {
            failure = $"the server wrote something other than JSON-RPC to stdout: {ex.Message}";
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // The child exited between the check and the kill, which is the outcome we wanted.
            }
        }

        return new McpProbeResult(
            Started: true,
            failure,
            protocolVersion,
            serverName,
            serverVersion,
            tools,
            ping,
            workspaceStatus,
            await stderr);
    }

    /// <summary>Polls <c>workspace_status</c> until the index and the workspace stop moving.</summary>
    /// <param name="process">The running server.</param>
    /// <param name="firstId">The request id to use for the first poll; each retry takes the next one.</param>
    /// <param name="cancellationToken">Cancels the wait, which is what the probe's overall timeout does.</param>
    /// <returns>The last reply received, settled or not; null when the server answered none.</returns>
    /// <remarks>
    /// A single call answers a question the verifier is not asking. Indexing and the MSBuild load both
    /// start in the background so the handshake completes inside the host's startup timeout, so an
    /// immediate <c>workspace_status</c> reliably reports work in progress — on a perfectly healthy
    /// repository. Reporting that as a warning trains the reader to ignore the one check that would
    /// otherwise catch a genuinely degraded workspace, so the wait is what makes the verdict mean
    /// anything. A timeout returns the last observation rather than nothing, since "still loading after
    /// three minutes" is itself the finding.
    /// </remarks>
    private static async Task<string?> AwaitSettledWorkspaceAsync(
        Process process,
        int firstId,
        CancellationToken cancellationToken)
    {
        string? latest = null;

        try
        {
            for (var id = firstId; ; id++)
            {
                latest = await CallToolAsync(process, id, "workspace_status", cancellationToken);
                if (latest is null || IsSettled(latest))
                {
                    return latest;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return latest;
        }
    }

    /// <summary>Has the server finished both background load phases?</summary>
    /// <param name="status">A <c>workspace_status</c> reply.</param>
    /// <returns>True once neither the index nor the workspace reports work still in flight.</returns>
    /// <remarks>
    /// Judged on the two lines that describe progress, so a workspace that settled into a <em>failed</em>
    /// state counts as settled — the verifier's job is to report that failure, not to keep waiting for it
    /// to become something else.
    /// </remarks>
    private static bool IsSettled(string status)
    {
        var lines = status.Split('\n').Select(line => line.Trim()).ToList();
        var indexing = lines.Any(line => line.StartsWith("index: building", StringComparison.Ordinal));
        var loading = lines.Any(line => line.StartsWith("workspace: loading", StringComparison.Ordinal));
        return !indexing && !loading;
    }

    /// <summary>Calls one MCP tool and returns its text content.</summary>
    /// <param name="process">The running server.</param>
    /// <param name="id">The request id to correlate the response by.</param>
    /// <param name="tool">The tool's name.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The reply's concatenated text content, or null when the tool returned none.</returns>
    private static async Task<string?> CallToolAsync(
        Process process,
        int id,
        string tool,
        CancellationToken cancellationToken)
    {
        var response = await ExchangeAsync(
            process, id, "tools/call", new { name = tool, arguments = new { } }, cancellationToken);

        if (response is not { } result || !result.TryGetProperty("content", out var content))
        {
            return null;
        }

        var parts = content.EnumerateArray()
            .Select(part => Text(part, "text"))
            .OfType<string>();

        return string.Join('\n', parts);
    }

    /// <summary>Sends one request and waits for the response carrying the same id.</summary>
    /// <param name="process">The running server.</param>
    /// <param name="id">The request id.</param>
    /// <param name="method">The JSON-RPC method name.</param>
    /// <param name="parameters">The method's parameters.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>
    /// The response's <c>result</c>, or null when the server reported an error or closed its output
    /// first.
    /// </returns>
    /// <remarks>
    /// Responses are matched by id rather than taken in order, so a notification the server emits
    /// mid-exchange is skipped instead of being mistaken for the answer.
    /// </remarks>
    private static async Task<JsonElement?> ExchangeAsync(
        Process process,
        int id,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        await SendAsync(process, new { jsonrpc = "2.0", id, method, @params = parameters }, cancellationToken);

        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // Anything non-JSON on stdout is a protocol violation by the server, but skipping the
                // line lets the probe still report the stages that did work.
                continue;
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("id", out var responseId)
                    || responseId.ValueKind != JsonValueKind.Number
                    || responseId.GetInt32() != id)
                {
                    continue;
                }

                return document.RootElement.TryGetProperty("result", out var result)
                    ? result.Clone()
                    : null;
            }
        }

        return null;
    }

    /// <summary>Writes one JSON-RPC message to the server's stdin.</summary>
    /// <param name="process">The running server.</param>
    /// <param name="message">The message to serialize.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task completing once the message has been flushed.</returns>
    private static async Task SendAsync(Process process, object message, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(
            JsonSerializer.Serialize(message).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    /// <summary>Reads one string property, tolerating its absence.</summary>
    /// <param name="element">The object to read from.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The property's text, or null when it is absent or not a string.</returns>
    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
