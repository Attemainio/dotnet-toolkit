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

    /// <summary>
    /// <c>symidx_{sha256[:16]}</c> of a syntax-only fully-qualified name -- a namespace disjoint from
    /// <see cref="SymbolId"/> by construction, since the index_only fallback has no semantic model to
    /// derive a documentation-comment id from at all. A caller must never be able to mistake this for a
    /// live symbolId: the two are computed from incompatible inputs (a bare dotted name here vs. an
    /// assembly-qualified doc-comment id there) and will never agree even for the same logical symbol,
    /// so keeping them in one hash space invites exactly the silent baseVersions mismatch this prefix
    /// exists to make loud instead.
    /// </summary>
    /// <param name="fullyQualifiedName">The syntax index's dotted name for the declaration.</param>
    /// <returns>A provisional id that <c>validate_patch</c> rejects outright.</returns>
    public static string IndexOnlySymbolId(string fullyQualifiedName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullyQualifiedName));
        return "symidx_" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// <c>symfb_{sha256[:16]}</c> for a symbol that is not a <c>get_symbol</c> fetch target: one Roslyn could
    /// not mint a documentation-comment id for at all (some symbol kinds structurally lack one, or a symbol was
    /// bound against a transiently incomplete compilation), or one whose minted id does not identify it
    /// uniquely (a local function or lambda, whose id is built as though it were a member of the containing
    /// type). Disjoint from both <see cref="SymbolId"/> and <see cref="IndexOnlySymbolId"/> so this can never be
    /// silently confused with either. A symbol that resolves cleanly one moment and hits this fallback the next
    /// would otherwise mint two different sym_ ids for the same logical symbol -- the exact divergence class
    /// <see cref="IndexOnlySymbolId"/> exists to make visible instead of silent.
    /// </summary>
    /// <param name="displayName">The symbol's fully-qualified display string.</param>
    /// <param name="assemblyName">The containing assembly's name, or empty.</param>
    /// <returns>A provisional id that <c>validate_patch</c> rejects outright.</returns>
    public static string FallbackSymbolId(string displayName, string assemblyName)
    {
        var input = $"{assemblyName}|{displayName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "symfb_" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>Mints an id for a validated-but-unapplied patch draft held by <c>PatchDraftStore</c>.</summary>
    public static string Draft() => $"draft_{Ulid.NewString()}";
}
