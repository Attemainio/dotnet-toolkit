namespace DotnetToolkit.McpServer.Store;

/// <summary>
/// Decides whether the filesystem holding the knowledge-store file can safely run SQLite's
/// write-ahead log.
/// </summary>
/// <remarks>
/// WAL keeps its index in a shared-memory file (<c>-shm</c>) and coordinates readers and writers
/// through POSIX advisory locks. SQLite's own documentation states that WAL does not work over a
/// network filesystem: every accessor has to be on one machine with working shared memory. A WSL2
/// <c>/mnt/c</c> mount is 9p/DrvFs and is neither, and a WAL store on one has been observed to end up
/// with torn B-tree pages through the tail of its largest table. A rollback journal needs only
/// ordinary file IO and no shared memory at all, so it is what this reports for such a mount.
/// </remarks>
internal static class CacheFilesystem
{
    /// <summary>The name reported when no mount could be matched to the path.</summary>
    public const string Unknown = "unknown";

    // Filesystems that proxy every operation to another machine or another OS kernel, so the shared
    // memory and advisory locking WAL is built on are emulated rather than real. Spelled as
    // /proc/self/mountinfo spells them.
    private static readonly string[] RemoteFilesystems =
        ["9p", "drvfs", "cifs", "smbfs", "smb2", "smb3", "nfs", "nfs4", "afs", "lustre"];

    /// <summary>Whether a store kept at <paramref name="path"/> may use WAL mode.</summary>
    /// <param name="path">A file or directory path inside the cache directory.</param>
    /// <param name="filesystem">
    /// The detected filesystem name, or <see cref="Unknown"/> when the platform offers no mount table
    /// this can read.
    /// </param>
    /// <returns>
    /// <c>true</c> when WAL is safe, including when detection found nothing — an undetected mount is
    /// far more often an ordinary local disk than a network share, and forcing every such store onto a
    /// rollback journal would slow the common case to guard against the rare one.
    /// </returns>
    public static bool SupportsWriteAheadLog(string path, out string filesystem)
    {
        filesystem = Unknown;
        try
        {
            if (OperatingSystem.IsWindows())
                return WindowsSupportsWal(path, ref filesystem);
            if (OperatingSystem.IsLinux())
                return LinuxSupportsWal(path, ref filesystem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Detection is advisory. An unreadable mount table or a path the OS will not resolve must
            // never stop the store from opening, so fall through to the permissive default.
        }

        return true;
    }

    /// <summary>Matches a path against <c>/proc/self/mountinfo</c>, taking the longest mount point that contains it.</summary>
    private static bool LinuxSupportsWal(string path, ref string filesystem)
    {
        var target = Path.GetFullPath(path);
        var longestMatch = -1;

        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            // "36 35 98:0 /src /dst rw,noatime shared:1 - ext4 /dev/sda rw" — the fields before the
            // " - " separator are positional, those after it start with the filesystem type.
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0)
                continue;

            var positional = line[..separator].Split(' ');
            if (positional.Length < 5)
                continue;

            var mountPoint = Unescape(positional[4]);
            if (mountPoint.Length <= longestMatch || !Contains(mountPoint, target))
                continue;

            var afterSeparator = line[(separator + 3)..].Split(' ');
            if (afterSeparator.Length == 0 || afterSeparator[0].Length == 0)
                continue;

            longestMatch = mountPoint.Length;
            filesystem = afterSeparator[0];
        }

        return longestMatch < 0 || !RemoteFilesystems.Contains(filesystem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Treats a UNC path or a mapped network drive as remote, and every local volume as safe.</summary>
    private static bool WindowsSupportsWal(string path, ref string filesystem)
    {
        var target = Path.GetFullPath(path);
        if (target.StartsWith(@"\\", StringComparison.Ordinal))
        {
            filesystem = "unc";
            return false;
        }

        var root = Path.GetPathRoot(target);
        if (string.IsNullOrEmpty(root))
            return true;

        var drive = new DriveInfo(root);
        if (drive.DriveType == DriveType.Network)
        {
            filesystem = "network";
            return false;
        }

        // DriveFormat throws on a volume that is not ready, which says nothing about WAL either way.
        filesystem = drive.IsReady ? drive.DriveFormat : Unknown;
        return true;
    }

    /// <summary>Whether <paramref name="mountPoint"/> is <paramref name="target"/> or an ancestor directory of it.</summary>
    private static bool Contains(string mountPoint, string target) =>
        mountPoint == "/"
        || string.Equals(mountPoint, target, StringComparison.Ordinal)
        || target.StartsWith(mountPoint + "/", StringComparison.Ordinal);

    /// <summary>Decodes the octal escapes mountinfo uses for space, tab, newline and backslash.</summary>
    private static string Unescape(string field)
    {
        if (!field.Contains('\\', StringComparison.Ordinal))
            return field;

        var decoded = new System.Text.StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length
                && int.TryParse(field.AsSpan(i + 1, 3), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var octal))
            {
                decoded.Append((char)Convert.ToInt32(octal.ToString(System.Globalization.CultureInfo.InvariantCulture), 8));
                i += 3;
                continue;
            }
            decoded.Append(field[i]);
        }
        return decoded.ToString();
    }
}
