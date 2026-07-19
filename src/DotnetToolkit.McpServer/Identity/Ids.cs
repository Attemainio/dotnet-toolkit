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

    /// <summary>Task id used when a caller supplies none; groups all unattributed work.</summary>
    public const string UnattributedTask = "tsk_unattributed";
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
