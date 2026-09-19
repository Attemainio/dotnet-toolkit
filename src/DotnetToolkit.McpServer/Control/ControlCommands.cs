namespace DotnetToolkit.McpServer.Control;

/// <summary>
/// The command words the loopback control channel understands, named once so the hook that sends one
/// and the server that dispatches it cannot drift apart.
/// </summary>
/// <remarks>
/// The protocol is one line in, one line out. <see cref="Rescan"/> and <see cref="Reload"/> are bare
/// words; <see cref="Meter"/> and <see cref="Refresh"/> are a prefix followed by a space and one line
/// of JSON, which is why the server matches them by prefix rather than by equality. JSON is safe on a
/// line-oriented channel because a serialized document escapes the newlines it may contain.
/// </remarks>
internal static class ControlCommands
{
    /// <summary>Runs a full syntax-index sweep and reports the result. Synchronous.</summary>
    public const string Rescan = "rescan";

    /// <summary>Starts an MSBuild workspace reload and returns immediately.</summary>
    public const string Reload = "reload";

    /// <summary>Records one metered tool call. Followed by a space and a JSON object.</summary>
    public const string Meter = "meter";

    /// <summary>
    /// Brings the syntax index, the live workspace and the symbol store up to date for the listed
    /// files. Followed by a space and a JSON array of paths. Waits for the scoped store refresh
    /// (bounded), but only starts a reload when a file was added or removed.
    /// </summary>
    public const string Refresh = "refresh";
}
