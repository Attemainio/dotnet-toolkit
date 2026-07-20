using Microsoft.CodeAnalysis.Text;

namespace DotnetToolkit.McpServer.Workspace;

/// <summary>
/// Whether an in-memory copy of a file still matches what is on disk.
///
/// Both tiers need this and they must agree: the write path refuses to apply over a file that moved
/// (a patch writes the whole document back, so it would revert the part it did not edit), and the read
/// path marks an answer <c>limitedBy: "stale"</c> rather than presenting drifted content as current.
/// One implementation, so a file cannot be fresh enough to read but stale enough to refuse.
/// </summary>
public static class DiskDrift
{
    /// <summary>
    /// True when <paramref name="absPath"/> differs from <paramref name="inMemory"/>. A file that has
    /// vanished counts as drift; an unreadable one does not, since a transient IO error is not evidence
    /// that the content moved.
    /// </summary>
    public static async Task<bool> DriftedAsync(string absPath, SourceText inMemory)
    {
        try
        {
            if (!File.Exists(absPath))
                return true;
            var onDisk = await File.ReadAllTextAsync(absPath);
            return !string.Equals(Normalize(onDisk), Normalize(inMemory.ToString()), StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Line endings are normalised before comparing so a CRLF working tree — the default on Windows and
    /// on WSL <c>/mnt/*</c> checkouts — is not read as drift on every single file.
    /// </summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
