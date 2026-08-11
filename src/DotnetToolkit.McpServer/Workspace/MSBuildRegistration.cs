using Microsoft.Build.Locator;

namespace DotnetToolkit.McpServer.Workspace;

/// <summary>
/// Chooses which installed .NET SDK's MSBuild the workspace loads projects with, and registers it
/// before any <c>Microsoft.CodeAnalysis.Workspaces.MSBuild</c> code runs.
/// </summary>
/// <remarks>
/// This used to be a shell script's job: the launcher preferred <c>~/.dotnet/dotnet</c> over the
/// <c>dotnet</c> on <c>PATH</c>, because a system-wide install older than the repo's target framework
/// silently degrades the workspace — projects load with missing references rather than failing
/// outright. A shell launcher cannot run on Windows, so the preference moved here, where it is one
/// implementation for every platform.
/// <para>
/// <see cref="MSBuildLocator.RegisterDefaults"/> alone is not enough, because its .NET SDK discovery
/// runs relative to the <c>dotnet</c> host that started this process. Launched from a system-wide
/// host, it never sees a newer user-local SDK at all. So the candidates from that query are pooled
/// with the SDKs found under the user-local install and any explicit override, and the highest version
/// wins.
/// </para>
/// </remarks>
internal static class MSBuildRegistration
{
    /// <summary>Environment variable naming a .NET install root whose SDK to use, overriding discovery.</summary>
    public const string OverrideVariable = "DOTNET_TOOLKIT_DOTNET_ROOT";

    /// <summary>The <c>dotnet</c> host belonging to the SDK install <see cref="Register"/> registered.</summary>
    /// <value>
    /// The host executable's full path, or null when nothing was registered or no host sits where the SDK
    /// layout implies. Code that shells out to <c>dotnet</c> uses this rather than resolving the name on
    /// <c>PATH</c>: on a machine with several installs those are different SDKs, and a restore run by the
    /// one that did not register writes a NuGet assets cache the registered MSBuild then cannot open.
    /// </value>
    public static string? HostPath { get; private set; }

    /// <summary>Registers the newest MSBuild this machine offers, if one is not registered already.</summary>
    /// <returns>
    /// A one-line description of what was registered, for the startup log — or a description of why
    /// nothing was, since a failure here is what makes every later workspace symptom confusing.
    /// </returns>
    public static string Register()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return "MSBuild already registered";
        }

        var discovered = QueryDefaults();
        var best = discovered.MaxBy(candidate => candidate.Version);

        var explicitRoot = Environment.GetEnvironmentVariable(OverrideVariable);
        var localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet");

        // An explicit override is an instruction, not a candidate: honor it even when a newer SDK
        // exists elsewhere, because the point of setting it is to pin a specific install.
        var pinned = string.IsNullOrWhiteSpace(explicitRoot) ? null : FindNewestSdk(explicitRoot);
        if (pinned is not null)
        {
            MSBuildLocator.RegisterMSBuildPath(pinned.Path);
            HostPath = HostFor(pinned.Path);
            return $"MSBuild {pinned.Version} from {OverrideVariable} ({pinned.Path})";
        }

        var local = FindNewestSdk(localRoot);
        if (local is not null && (best is null || local.Version > best.Version))
        {
            MSBuildLocator.RegisterMSBuildPath(local.Path);
            HostPath = HostFor(local.Path);
            return $"MSBuild {local.Version} from the user-local install ({local.Path})";
        }

        if (best is not null)
        {
            MSBuildLocator.RegisterInstance(best.Instance!);
            HostPath = HostFor(best.Path);
            return $"MSBuild {best.Version} ({best.Path})";
        }

        // Nothing discoverable. RegisterDefaults throws when it finds no instance, and a throw here
        // takes the whole server down before it can report anything useful over MCP.
        return "no MSBuild instance found; project loading will be unavailable";
    }

    private static List<SdkCandidate> QueryDefaults()
    {
        try
        {
            return [.. MSBuildLocator.QueryVisualStudioInstances()
                .Select(instance => new SdkCandidate(instance.Version, instance.MSBuildPath, instance))];
        }
        catch (Exception)
        {
            // Discovery shells out to the dotnet host; a failure there is a reason to fall back to the
            // other candidates, not to take the process down.
            return [];
        }
    }

    /// <summary>Derives the <c>dotnet</c> host from the SDK directory MSBuild was registered against.</summary>
    /// <param name="sdkPath">An SDK directory, i.e. <c>&lt;root&gt;/sdk/&lt;version&gt;</c>.</param>
    /// <returns>The host executable's full path, or null when none sits where that layout implies.</returns>
    private static string? HostFor(string sdkPath)
    {
        // <root>/sdk/<version> -> <root>, where the muxer sits. A queried instance's MSBuildPath can carry
        // a trailing separator, which would otherwise make the first GetDirectoryName a no-op.
        var trimmed = sdkPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(trimmed));
        if (root is null)
        {
            return null;
        }

        var host = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(host) ? host : null;
    }

    /// <summary>Finds the newest SDK under a .NET install root.</summary>
    /// <param name="dotnetRoot">An install root, i.e. the directory holding an <c>sdk</c> folder.</param>
    /// <returns>The highest-versioned SDK found, or null when the root holds none.</returns>
    private static SdkCandidate? FindNewestSdk(string dotnetRoot)
    {
        var sdkRoot = Path.Combine(dotnetRoot, "sdk");
        if (!Directory.Exists(sdkRoot))
        {
            return null;
        }

        SdkCandidate? newest = null;
        foreach (var directory in Directory.EnumerateDirectories(sdkRoot))
        {
            // A prerelease directory is named e.g. 10.0.100-preview.5; Version cannot parse the
            // suffix, and ordering prereleases against each other is not worth the precision here.
            var name = Path.GetFileName(directory);
            var numeric = name.Split('-')[0];
            if (!Version.TryParse(numeric, out var version))
            {
                continue;
            }

            // MSBuild.dll beside the SDK is what RegisterMSBuildPath expects to find.
            if (!File.Exists(Path.Combine(directory, "MSBuild.dll")))
            {
                continue;
            }

            if (newest is null || version > newest.Version)
            {
                newest = new SdkCandidate(version, directory, Instance: null);
            }
        }

        return newest;
    }

    private sealed record SdkCandidate(Version Version, string Path, VisualStudioInstance? Instance);
}
