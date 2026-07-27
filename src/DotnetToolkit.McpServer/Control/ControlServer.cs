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
    private Task? _runTask;

    public ControlServer(
        SolutionLocator locator, ProjectIndex index, WorkspaceHost workspace,
        SymbolIndexBuilder indexBuilder, ILogger<ControlServer> log)
    {
        _locator = locator;
        _index = index;
        _workspace = workspace;
        _indexBuilder = indexBuilder;
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
            var command = await reader.ReadLineAsync();
            var response = command?.Trim() switch
            {
                "rescan" => await RescanAsync(),
                "reload" => Reload(),
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
}
