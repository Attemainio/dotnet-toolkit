using System.Globalization;
using System.Net.Sockets;

namespace DotnetToolkit.McpServer.Hooks;

/// <summary>
/// Client half of the running server's loopback control channel, used by a hook process to trigger a
/// rescan or reload it has no other way to request.
/// </summary>
/// <remarks>
/// A hook is a separate OS process with no access to the MCP stdio pipe Claude Code and the server talk
/// over; <c>Control.ControlServer</c> exists specifically to give it another way in. Every failure —
/// no port file, a server built before the channel existed, a refused connection, a timeout — is
/// reported the same way, as a null response, because they all mean the same thing to the caller: fall
/// back to reminder text.
/// <para>
/// The shell version used bash's <c>/dev/tcp</c>, which exists in neither <c>cmd.exe</c> nor a
/// POSIX <c>sh</c>.
/// </para>
/// </remarks>
internal static class ControlClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Sends one command to the control channel and reads its single-line response.</summary>
    /// <param name="root">The repo root whose cache directory holds <c>control.port</c>.</param>
    /// <param name="command">The command word, <c>rescan</c> or <c>reload</c>.</param>
    /// <returns>
    /// The server's response line, or null when the channel could not be reached for any reason. One
    /// connection per command, matching what the server's accept loop expects.
    /// </returns>
    public static async Task<string?> SendAsync(string root, string command)
    {
        var portFile = Path.Combine(root, ".claude", "dotnet-toolkit", "cache", "control.port");
        if (!File.Exists(portFile))
        {
            return null;
        }

        int port;
        try
        {
            var text = (await File.ReadAllTextAsync(portFile)).Trim();
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
            {
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            using var client = new TcpClient();
            using var connectCts = new CancellationTokenSource(ConnectTimeout);
            await client.ConnectAsync("127.0.0.1", port, connectCts.Token);

            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            using var reader = new StreamReader(stream);

            using var responseCts = new CancellationTokenSource(ResponseTimeout);
            await writer.WriteLineAsync(command.AsMemory(), responseCts.Token);
            return await reader.ReadLineAsync(responseCts.Token);
        }
        catch (SocketException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
