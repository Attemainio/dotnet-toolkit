using System.Diagnostics;

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

    /// <summary>The variables MSBuildLocator sets process-wide when it registers an SDK.</summary>
    private static readonly string[] InjectedVariables =
        ["MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath"];

    /// <summary>Removes this process's MSBuild registration from a child process's environment.</summary>
    /// <param name="startInfo">The child to launch.</param>
    /// <remarks>
    /// <para>
    /// <strong>Call this before starting any <c>dotnet</c> child.</strong> Registering an SDK points
    /// these variables at that one install for the lifetime of the process, and a child inherits them.
    /// When the child's own host belongs to a different install — which is the normal case for a bare
    /// <c>dotnet</c> resolved on <c>PATH</c> — it is told to load a foreign MSBuild and exits nonzero
    /// having written nothing to either stream.
    /// </para>
    /// <para>
    /// The silence is what makes this worth a named helper rather than three lines at each call site: a
    /// child that fails with no output is indistinguishable from one whose pipe is being held open, so
    /// the symptom points at process plumbing and the cause is an environment variable. It cost a whole
    /// integration collection — 205 tests — once already.
    /// </para>
    /// </remarks>
    public static void ScrubInheritedRegistration(ProcessStartInfo startInfo)
    {
        foreach (var injected in InjectedVariables)
        {
            startInfo.Environment.Remove(injected);
        }
    }

    /// <summary>Registers the MSBuild this repository is built with, if one is not registered already.</summary>
    /// <param name="root">
    /// The repository root, so a <c>global.json</c> there is honored. Null selects the newest SDK on
    /// the machine, which is the right answer when no repository governs the choice.
    /// </param>
    /// <returns>
    /// A one-line description of what was registered, for the log — or a description of why nothing
    /// was, since a failure here is what makes every later workspace symptom confusing.
    /// </returns>
    /// <remarks>
    /// <strong>Called at the first workspace load rather than at startup, because the root is the
    /// input.</strong> Picking the highest-versioned SDK on the machine is a machine-global answer to
    /// a per-repository question, and it went wrong in two ways at once: a repository pinned to one
    /// feature band had its workspace loaded by MSBuild from another, and a developer's own
    /// <c>dotnet build</c> resolved a different SDK than this server's restore — so whichever ran last
    /// owned the NuGet assets cache and the other reported the workspace degraded. <c>global.json</c>
    /// is the mechanism the SDK already provides for exactly that conflict.
    /// </remarks>
    public static string Register(string? root = null)
    {
        if (MSBuildLocator.IsRegistered)
        {
            return "MSBuild already registered";
        }

        var explicitRoot = Environment.GetEnvironmentVariable(OverrideVariable);

        // An explicit override is an instruction, not a candidate: honor it even when a newer SDK
        // exists elsewhere, and even against a global.json, because the point of setting it is to pin
        // a specific install.
        var pinned = string.IsNullOrWhiteSpace(explicitRoot) ? null : FindNewestSdk(explicitRoot);
        if (pinned is not null)
        {
            return Use(pinned, $"MSBuild {pinned.Version} from {OverrideVariable} ({pinned.Path})");
        }

        var candidates = Pool();
        var best = candidates.MaxBy(candidate => candidate.Version);

        if (root is not null && GlobalJsonPolicy.Read(root) is { } policy)
        {
            if (policy.Select(candidates.Select(candidate => candidate.Version)) is { } wanted
                && candidates.FirstOrDefault(candidate => candidate.Version == wanted) is { } chosen)
            {
                return Use(
                    chosen,
                    $"MSBuild {chosen.Version} for global.json {policy.Requested} "
                    + $"rollForward={policy.Mode} ({chosen.Path})");
            }

            // Nothing installed satisfies the pin. Loading with the newest available is still better
            // than not loading at all -- and saying so is the whole point, because this is precisely
            // the state whose symptoms are otherwise a silently degraded workspace.
            if (best is not null)
            {
                return Use(
                    best,
                    $"MSBuild {best.Version} ({best.Path}); no installed SDK satisfies global.json "
                    + $"{policy.Requested} rollForward={policy.Mode}, so the workspace may not match "
                    + "what this repository builds with");
            }
        }

        if (best is not null)
        {
            return Use(best, $"MSBuild {best.Version} ({best.Path})");
        }

        // Nothing discoverable. RegisterDefaults throws when it finds no instance, and a throw here
        // takes the whole server down before it can report anything useful over MCP.
        return "no MSBuild instance found; project loading will be unavailable";
    }

    /// <summary>Every SDK this process can discover, pooled from both discovery routes.</summary>
    /// <returns>One candidate per usable SDK, deduplicated by directory, in no particular order.</returns>
    /// <remarks>
    /// <see cref="MSBuildLocator.QueryVisualStudioInstances"/> runs relative to the <c>dotnet</c> host
    /// that started this process, so launched from a system-wide host it never sees a user-local SDK.
    /// Pooling both routes is what makes <see cref="Register"/>'s choice a choice at all. It is factored
    /// out of <see cref="Register"/> so <see cref="InstalledSdks"/> reports the same inventory the
    /// selection actually ran against — an inventory assembled a second way could disagree with the
    /// choice it is printed beside, which is the one thing that diagnostic must never do.
    /// </remarks>
    private static List<SdkCandidate> Pool()
    {
        var localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet");

        var candidates = QueryDefaults();
        candidates.AddRange(EnumerateSdks(localRoot)
            .Where(local => !candidates.Any(known =>
                string.Equals(known.Path.TrimEnd(Path.DirectorySeparatorChar),
                    local.Path.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))));

        return candidates;
    }

    /// <summary>The SDKs <see cref="Register"/> chooses between, for diagnostic reporting.</summary>
    /// <returns>Version and directory for each discoverable SDK, highest version first.</returns>
    /// <remarks>
    /// Registers nothing, so a caller may ask before or instead of registering. The <c>doctor</c> verb
    /// prints this beside the line <see cref="Register"/> returns: a user told only which SDK won cannot
    /// tell a bad choice from an absent candidate, and those two have opposite repairs.
    /// </remarks>
    internal static IReadOnlyList<(Version Version, string Path)> InstalledSdks() =>
        [.. Pool()
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => (candidate.Version, candidate.Path))];

    /// <summary>Registers one candidate and records the host that belongs to it.</summary>
    /// <param name="candidate">The SDK to register.</param>
    /// <param name="description">What to report about the choice.</param>
    /// <returns><paramref name="description"/>, so a caller can return it directly.</returns>
    /// <remarks>
    /// A queried instance registers through <see cref="MSBuildLocator.RegisterInstance"/> and a
    /// path-discovered one through <see cref="MSBuildLocator.RegisterMSBuildPath"/>; keeping that in
    /// one place is what stops <see cref="HostPath"/> being set on some paths and not others, which is
    /// the state that lets a restore run on a different SDK than the workspace.
    /// </remarks>
    private static string Use(SdkCandidate candidate, string description)
    {
        if (candidate.Instance is { } instance)
        {
            MSBuildLocator.RegisterInstance(instance);
        }
        else
        {
            MSBuildLocator.RegisterMSBuildPath(candidate.Path);
        }

        HostPath = HostFor(candidate.Path);
        return description;
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
    private static SdkCandidate? FindNewestSdk(string dotnetRoot) =>
        EnumerateSdks(dotnetRoot).MaxBy(static candidate => candidate.Version);

    /// <summary>Every SDK installed under a .NET install root.</summary>
    /// <param name="dotnetRoot">An install root, i.e. the directory holding an <c>sdk</c> folder.</param>
    /// <returns>One candidate per usable SDK directory, in no particular order.</returns>
    /// <remarks>
    /// All of them rather than only the newest, because a <c>global.json</c> policy may name any one:
    /// <c>rollForward: feature</c> wants the <em>nearest</em> qualifying band, not the highest
    /// installed.
    /// </remarks>
    private static List<SdkCandidate> EnumerateSdks(string dotnetRoot)
    {
        var found = new List<SdkCandidate>();
        var sdkRoot = Path.Combine(dotnetRoot, "sdk");

        if (!Directory.Exists(sdkRoot))
        {
            return found;
        }

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

            found.Add(new SdkCandidate(version, directory, Instance: null));
        }

        return found;
    }

    private sealed record SdkCandidate(Version Version, string Path, VisualStudioInstance? Instance);
}
