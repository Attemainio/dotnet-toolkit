using System.Security.Cryptography;
using System.Text;

namespace DotnetToolkit.McpServer.Identity;

/// <summary>
/// Issues the prefixed stable identifiers of spec §6. ULID-backed identifiers sort
/// chronologically; <see cref="SymbolId"/> is content-derived so it is stable across
/// file renames and changes only on symbol rename.
/// </summary>
public static class Ids
{
    public static string ToolCall() => $"tcl_{Ulid.NewString()}";

    /// <summary>
    /// Session id used when a caller supplies none. Attribution is instrumentation, so it must never
    /// be a precondition for retrieval: an agent that has not read the retrieval skill still gets a
    /// working tool, and its calls still group together (one id per server process) rather than
    /// being dropped. Calls carrying this id are auto-attributed, not caller-attributed.
    /// </summary>
    public static readonly string AmbientSession = $"ses_auto{Ulid.NewString()}";

    /// <summary>
    /// Resolves the task id a telemetry row is attributed to: the caller's own id when it supplied one,
    /// otherwise the ambient session id.
    /// </summary>
    /// <param name="supplied">The caller-supplied id, or null/blank when the caller did not attribute the call.</param>
    /// <returns>The caller's trimmed id, or <see cref="AmbientSession"/> when none was supplied.</returns>
    /// <remarks>
    /// Falling back to the session id keeps every row attributed to something groupable, so an
    /// unattributed caller still aggregates normally instead of writing a null axis. Callers that do
    /// supply an id (parallel agents measuring their own token cost) are the only ones that can be
    /// separated from each other, since the session id is per server process and therefore shared.
    /// </remarks>
    public static string TaskId(string? supplied) =>
        string.IsNullOrWhiteSpace(supplied) ? AmbientSession : supplied.Trim();

    public static string Event() => $"evt_{Ulid.NewString()}";
    public static string Patch() => $"pch_{Ulid.NewString()}";
    public static string ValidationAttempt() => $"val_{Ulid.NewString()}";
    public static string Log() => $"log_{Ulid.NewString()}";

    /// <summary>
    /// <c>sym_{sha256[:16]}</c> of the fully-qualified metadata name plus the containing
    /// assembly name (spec §6). Deterministic across machines and restarts.
    /// </summary>
    public static string SymbolId(string fullyQualifiedMetadataName, string assemblyName)
    {
        var input = $"{assemblyName}|{fullyQualifiedMetadataName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "sym_" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
