using System.Globalization;
using System.Text.Json;

using DotnetToolkit.McpServer.Workspace;

namespace DotnetToolkit.McpServer.Diagnostics;

/// <summary>
/// Entry point for the <c>doctor</c> verb: verifies an installation end to end and exits, instead of
/// starting the MCP server.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This replaces a checklist nobody can run identically twice.</strong> Verifying an install
/// used to mean reading configuration listings and eyeballing a tool menu, and both of those report
/// <em>configuration</em>: a server shows as registered and enabled whether or not the command it names
/// can launch. An installation therefore reached users looking healthy while no tool was callable. The
/// verb exists to collapse that gap into one command whose stages are distinct — configured, launched,
/// initialized, tools discovered, workspace healthy — because each stage fails for a different reason
/// and has a different repair.
/// </para>
/// <para>
/// It is a subcommand of the published server binary for the same reason the hooks are: the plugin
/// already requires a .NET runtime and nothing else, so a verifier shipped as a shell or Python script
/// would be the one piece of the plugin that could not run on every host it supports — and the host
/// hardest to verify by hand is precisely the one where a script would be least likely to run.
/// </para>
/// <para>
/// <strong>Writing to stdout is correct here</strong>, unlike anywhere in the server proper: this verb
/// never starts the MCP transport, so stdout is a report rather than a protocol channel. The same is
/// true of the <c>hook</c> verb.
/// </para>
/// </remarks>
internal static class Doctor
{
    /// <summary>The first argument that selects verifier mode.</summary>
    public const string Verb = "doctor";

    /// <summary>The MCP server name this plugin is registered under, in both hosts.</summary>
    private const string ServerName = "dotnet";

    /// <summary>The published server binary, relative to the plugin root.</summary>
    private static readonly string[] ServerDllPath = ["dist", "DotnetToolkit.McpServer.dll"];

    /// <summary>Manifest paths, relative to a marketplace source, that mark a directory as loadable.</summary>
    /// <remarks>
    /// Any one of them is enough. Requiring the pair this plugin ships would fail a Claude-only clone that
    /// Codex reads perfectly well through the shared marketplace file, and requiring either of ours would
    /// fail every marketplace we did not write: Codex's own bundled and runtime marketplaces use the third
    /// layout, so a verifier that knows only the first two reports the host's own plugins as broken.
    /// </remarks>
    private static readonly string[][] MarketplaceManifests =
    [
        [".codex-plugin", "plugin.json"],
        [".claude-plugin", "marketplace.json"],
        [".agents", "plugins", "marketplace.json"],
    ];

    /// <summary>How long to allow the live probe before giving up on it.</summary>
    /// <remarks>
    /// Generous because the probe's first workspace load may run a <c>dotnet restore</c> on a cold NuGet
    /// cache, over a Windows volume mounted into WSL. A timeout short enough to be brisk on a warm repo
    /// would report a false failure on exactly the setup this verb was written for.
    /// </remarks>
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Runs the verifier if the command line asks for it.</summary>
    /// <param name="args">The process arguments.</param>
    /// <returns>
    /// The process exit code when <paramref name="args"/> starts with <see cref="Verb"/> — nonzero when
    /// any check failed — or null when it does not, in which case the caller should start the MCP server
    /// as usual.
    /// </returns>
    public static async Task<int?> TryRunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != Verb)
        {
            return null;
        }

        var options = DoctorOptions.Parse(args[1..]);
        var checks = await RunAsync(options, CancellationToken.None);

        Console.Out.WriteLine(options.Json ? RenderJson(checks) : RenderText(checks, options));
        return checks.Any(check => check.Status == CheckStatus.Fail) ? 1 : 0;
    }

    /// <summary>Runs every check.</summary>
    /// <param name="options">What to verify and how.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>Every check's verdict, in report order.</returns>
    private static async Task<IReadOnlyList<DoctorCheck>> RunAsync(
        DoctorOptions options,
        CancellationToken cancellationToken)
    {
        var host = HostProfile.Detect();
        var pluginRoot = PluginLocation.Resolve(options.Repo);
        var codex = CodexProfile.Load(host.CodexHome);

        var checks = new List<DoctorCheck>();
        checks.AddRange(CheckEnvironment(host, options));
        checks.AddRange(CheckBuildIdentity(pluginRoot));
        checks.AddRange(CheckCodexProfile(host, codex, pluginRoot));
        checks.AddRange(CheckDotnet(options.Repo));
        checks.AddRange(CheckSandbox(host));
        checks.AddRange(await CheckLiveSessionAsync(options, codex, pluginRoot, cancellationToken));
        return checks;
    }

    /// <summary>Reports the execution environment every later check is relative to.</summary>
    /// <param name="host">The observed environment.</param>
    /// <param name="options">The run's options, for the repository being verified.</param>
    /// <returns>The environment section's checks.</returns>
    private static IEnumerable<DoctorCheck> CheckEnvironment(HostProfile host, DoctorOptions options)
    {
        const string Group = "environment";

        yield return DoctorCheck.Ok(Group, "executor", host.OsDescription
            + (host.IsWsl ? $" (WSL{(host.WslDistro is { } distro ? $", distro {distro}" : string.Empty)})" : string.Empty));

        yield return DoctorCheck.Ok(Group, "topology", host.Topology switch
        {
            ExecutionTopology.NativeWindows => "Windows host, Windows tool processes",
            ExecutionTopology.NativeUnix => "Unix host, Unix tool processes",
            ExecutionTopology.StandaloneWsl => "WSL throughout, including the agent profile",
            _ => "a Windows-owned agent profile, executed in WSL",
        });

        yield return DoctorCheck.Ok(Group, "path namespace", $"paths must be written as {host.RequiredPathNamespace}");

        yield return host.Topology == ExecutionTopology.WindowsProfileThroughWsl
            ? DoctorCheck.Warn(
                Group,
                "profile ownership",
                $"{HostProfile.CodexHomeVariable} points at a Windows-owned profile ({host.CodexHome}) while this "
                + "process is Linux, so a separate WSL profile at ~/.codex is NOT the one in effect",
                "register paths in this profile using the /mnt/... spelling, not C:\\...")
            : DoctorCheck.Ok(
                Group,
                "profile ownership",
                host.CodexHomeIsExplicit
                    ? $"{HostProfile.CodexHomeVariable}={host.CodexHome}"
                    : $"default profile location ({host.CodexHome})");

        yield return Directory.Exists(options.Repo)
            ? DoctorCheck.Ok(Group, "target repository", options.Repo)
            : DoctorCheck.Fail(Group, "target repository", $"{options.Repo} does not exist",
                "pass --repo <path> naming the repository to verify");
    }

    /// <summary>Checks that the manifests, the running assembly and the published binary agree.</summary>
    /// <param name="pluginRoot">The plugin's installation directory.</param>
    /// <returns>The build-identity section's checks.</returns>
    private static IEnumerable<DoctorCheck> CheckBuildIdentity(string pluginRoot)
    {
        const string Group = "build identity";

        yield return DoctorCheck.Ok(Group, "running build", $"{BuildIdentity.Full} from {BuildIdentity.Location ?? "memory"}");

        foreach (var (label, path) in new[]
        {
            ("claude manifest", Path.Combine(pluginRoot, ".claude-plugin", "plugin.json")),
            ("codex manifest", Path.Combine(pluginRoot, ".codex-plugin", "plugin.json")),
        })
        {
            var declared = ManifestVersion(path);
            if (declared is null)
            {
                yield return DoctorCheck.Warn(Group, label, $"no version readable at {path}");
                continue;
            }

            // Build metadata after '+' is per-host packaging information, not a different release, so
            // the comparison is against the release version both manifests share.
            var release = declared.Split('+')[0];
            yield return release == BuildIdentity.Version
                ? DoctorCheck.Ok(Group, label, declared)
                : DoctorCheck.Fail(Group, label, $"{declared} but the running server is {BuildIdentity.Version}",
                    "rebuild and republish so the manifest and the binary describe one release");
        }

        var published = Path.Combine(pluginRoot, Path.Combine(ServerDllPath));
        if (!File.Exists(published))
        {
            yield return DoctorCheck.Fail(Group, "published binary", $"{published} is missing",
                "run scripts/build-plugin.sh (or .cmd) to publish dist/");
            yield break;
        }

        var runningFromPublished = BuildIdentity.Location is { } location
            && string.Equals(
                Path.GetFullPath(location), Path.GetFullPath(published), StringComparison.OrdinalIgnoreCase);

        yield return runningFromPublished
            ? DoctorCheck.Ok(Group, "published binary", published)
            : DoctorCheck.Warn(
                Group,
                "published binary",
                $"this verifier is running from {BuildIdentity.Location ?? "memory"}, not {published}",
                "run doctor from dist/ to verify the binary a session would actually load");

        yield return CheckPublishedSources(Group, pluginRoot);
    }

    /// <summary>Checks that the published binary was built from the source tree as it stands.</summary>
    /// <param name="group">The report section.</param>
    /// <param name="pluginRoot">The plugin's installation directory, which for a clone is its root.</param>
    /// <returns>The check.</returns>
    /// <remarks>
    /// The gate that makes <c>doctor --no-probe</c> usable as a pre-commit and CI check, and the one check
    /// here for a defect nobody can see: a stale committed <c>dist/</c> ships to every consumer who pulls
    /// while every version string in the repository agrees with every other.
    /// </remarks>
    private static DoctorCheck CheckPublishedSources(string group, string pluginRoot)
    {
        if (SourceStamp.Compute(pluginRoot) is not { } current)
        {
            return DoctorCheck.Skip(
                group, "dist vs src", "no src/ here, so this is an installed plugin rather than a clone");
        }

        if (SourceStamp.Read(pluginRoot) is not { } stamped)
        {
            return DoctorCheck.Warn(group, "dist vs src", $"dist/ carries no {SourceStamp.FileName}",
                "run scripts/build-plugin.sh (or .cmd), which writes it");
        }

        return string.Equals(stamped, current, StringComparison.Ordinal)
            ? DoctorCheck.Ok(group, "dist vs src", $"published from the current sources ({current[..12]})")
            : DoctorCheck.Fail(
                group,
                "dist vs src",
                $"dist/ was published from sources {stamped[..12]}, but src/ is now {current[..12]} — "
                + "the committed binary does not implement the current source",
                "run scripts/build-plugin.sh (or .cmd) and commit dist/ in the same commit as the src/ change");
    }

    /// <summary>Checks that the paths a Codex profile stores can be opened by this process.</summary>
    /// <param name="host">The observed environment.</param>
    /// <param name="codex">The profile in effect.</param>
    /// <param name="pluginRoot">The plugin's installation directory, for the repair text.</param>
    /// <returns>The Codex section's checks.</returns>
    private static IEnumerable<DoctorCheck> CheckCodexProfile(
        HostProfile host,
        CodexProfile codex,
        string pluginRoot)
    {
        const string Group = "codex profile";

        if (!codex.Exists)
        {
            yield return DoctorCheck.Skip(Group, "config.toml", $"none at {codex.ConfigPath}");
            yield break;
        }

        if (codex.ReadError is { } error)
        {
            yield return DoctorCheck.Fail(Group, "config.toml", $"{codex.ConfigPath}: {error}");
            yield break;
        }

        yield return DoctorCheck.Ok(Group, "config.toml", codex.ConfigPath);

        var server = codex.Server(ServerName);
        if (server is null)
        {
            yield return DeclaredInManifest(pluginRoot) is { } declared
                ? DoctorCheck.Ok(Group, $"mcp_servers.{ServerName}", $"declared by the plugin manifest ({declared})")
                : DoctorCheck.Fail(
                    Group,
                    $"mcp_servers.{ServerName}",
                    "not registered, so this profile's sessions get skills and hooks but no MCP tools",
                    $"codex mcp add {ServerName} -- dotnet "
                    + $"{Path.Combine(pluginRoot, Path.Combine(ServerDllPath))}");
        }
        else
        {
            foreach (var check in CheckServerEntry(Group, host, server, pluginRoot))
            {
                yield return check;
            }
        }

        foreach (var marketplace in codex.Marketplaces)
        {
            yield return CheckMarketplace(Group, marketplace, pluginRoot);
        }

        if (codex.Marketplaces.Count == 0)
        {
            yield return DoctorCheck.Warn(Group, "marketplaces", "none registered",
                $"codex plugin marketplace add {pluginRoot}");
        }
    }

    /// <summary>Finds the server binary a Codex plugin manifest declares, if it declares one.</summary>
    /// <param name="pluginRoot">The plugin's installation directory.</param>
    /// <returns>The declared binary's path, or null when the manifest declares no usable server.</returns>
    /// <remarks>
    /// <para>
    /// <strong>This is the only registration that survives moving between Windows and WSL.</strong> A
    /// <c>config.toml</c> entry stores one absolute path, and a profile shared between a Windows-native
    /// Codex and one executing in WSL can only ever spell that path for one of them. A manifest
    /// declaration stores a path relative to the plugin, which Codex resolves against wherever the plugin
    /// is installed — so the same manifest is correct in both, the way <c>${CLAUDE_PLUGIN_ROOT}</c> is on
    /// the Claude side.
    /// </para>
    /// <para>
    /// The declared path is resolved and its existence checked here rather than merely reported, because a
    /// manifest naming a binary that was never published fails exactly like no registration at all, and
    /// the two have different repairs.
    /// </para>
    /// </remarks>
    internal static string? DeclaredInManifest(string pluginRoot)
    {
        try
        {
            var manifest = Path.Combine(pluginRoot, ".codex-plugin", "plugin.json");
            if (!File.Exists(manifest))
            {
                return null;
            }

            using var plugin = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!plugin.RootElement.TryGetProperty("mcpServers", out var reference)
                || reference.GetString() is not { } relative)
            {
                return null;
            }

            var servers = Path.Combine(pluginRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(servers))
            {
                return null;
            }

            using var declaration = JsonDocument.Parse(File.ReadAllText(servers));
            if (!declaration.RootElement.TryGetProperty("mcpServers", out var entries)
                || !entries.TryGetProperty(ServerName, out var entry)
                || !entry.TryGetProperty("args", out var args)
                || args.EnumerateArray().FirstOrDefault().GetString() is not { } binary)
            {
                return null;
            }

            var resolved = Path.Combine(pluginRoot, binary.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(resolved) ? resolved : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Checks one registered MCP server entry.</summary>
    /// <param name="group">The report section.</param>
    /// <param name="host">The observed environment.</param>
    /// <param name="server">The entry to check.</param>
    /// <param name="pluginRoot">The plugin's installation directory, for the repair text.</param>
    /// <returns>The entry's checks.</returns>
    private static IEnumerable<DoctorCheck> CheckServerEntry(
        string group,
        HostProfile host,
        CodexMcpServer server,
        string pluginRoot)
    {
        var name = $"mcp_servers.{server.Name}";

        if (!server.Enabled)
        {
            yield return DoctorCheck.Warn(group, name, "registered but enabled = false",
                $"set enabled = true under [{name}], or remove the key");
        }

        yield return DoctorCheck.Ok(group, $"{name}.command", server.Command ?? "(none)");

        foreach (var argument in server.Args.Where(argument => argument.Contains('/') || argument.Contains('\\')))
        {
            yield return CheckConfiguredPath(group, $"{name}.args", argument, host, pluginRoot);
        }

        foreach (var (key, value) in server.Env)
        {
            yield return PathNamespace.IsUsableHere(value) || !value.Contains(Path.DirectorySeparatorChar)
                ? DoctorCheck.Ok(group, $"{name}.env", $"{key}={value}")
                : DoctorCheck.Fail(group, $"{name}.env", $"{key}={value} is not a path this process can open",
                    PathNamespace.Localize(value) is { } fixedPath ? $"{key}={fixedPath}" : null);
        }
    }

    /// <summary>Judges one stored path against this process's namespace and the filesystem.</summary>
    /// <param name="group">The report section.</param>
    /// <param name="name">The label to report it under.</param>
    /// <param name="path">The path as configuration stores it.</param>
    /// <param name="host">The observed environment.</param>
    /// <param name="pluginRoot">The plugin's installation directory, for the repair text.</param>
    /// <returns>The check.</returns>
    /// <remarks>
    /// The namespace test comes before the existence test on purpose. A Windows path handed to a Linux
    /// process does not fail as "missing file" in a way that suggests the real cause — the launcher
    /// reports a command it could not find — so naming the namespace mismatch first is what turns the
    /// symptom into the diagnosis.
    /// </remarks>
    private static DoctorCheck CheckConfiguredPath(
        string group,
        string name,
        string path,
        HostProfile host,
        string pluginRoot)
    {
        if (!PathNamespace.IsUsableHere(path))
        {
            var translated = PathNamespace.Localize(path);
            return DoctorCheck.Fail(
                group,
                name,
                $"{path} is a {(OperatingSystem.IsWindows() ? "Unix" : "Windows")} path, but this process is "
                + $"{host.OsDescription.Split(' ')[0]} and cannot open it",
                translated is null
                    ? $"rewrite it in this process's namespace ({host.RequiredPathNamespace})"
                    : $"store it as {translated}");
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            return DoctorCheck.Ok(group, name, path);
        }

        return DoctorCheck.Fail(group, name, $"{path} does not exist",
            $"point it at {Path.Combine(pluginRoot, Path.Combine(ServerDllPath))}");
    }

    /// <summary>Checks that a marketplace source resolves to something loadable.</summary>
    /// <param name="group">The report section.</param>
    /// <param name="marketplace">The entry to check.</param>
    /// <param name="pluginRoot">This plugin's installation directory, used to tell our entry from another's.</param>
    /// <returns>The check.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Only this plugin's own marketplace is this verb's business.</strong> A Codex profile
    /// registers the host's bundled and runtime marketplaces alongside ours, and an earlier version judged
    /// every one of them against this repository's layout — so it failed <c>openai-bundled</c> and advised
    /// pointing it at a dotnet-toolkit clone, which would have broken a working installation had anyone
    /// followed it. A verifier that fails on things it does not own teaches its readers to ignore it.
    /// </para>
    /// <para>
    /// A foreign entry is still worth reporting when its path cannot be opened from here, because that is
    /// the same namespace mismatch this verb exists to find and the reader may well own the fix. It is
    /// reported as a warning rather than a failure: it is information, not a verdict on this install.
    /// </para>
    /// </remarks>
    internal static DoctorCheck CheckMarketplace(string group, CodexMarketplace marketplace, string pluginRoot)
    {
        var name = $"marketplaces.{marketplace.Name}";

        if (marketplace.Source is not { } source)
        {
            return DoctorCheck.Warn(group, name, "no source recorded");
        }

        if (!string.Equals(marketplace.SourceType, "local", StringComparison.OrdinalIgnoreCase))
        {
            return DoctorCheck.Skip(group, name, $"{marketplace.SourceType ?? "remote"} source, not checked locally");
        }

        var isOurs = IsThisPlugin(source, pluginRoot);

        if (!PathNamespace.IsUsableHere(source))
        {
            return isOurs
                ? DoctorCheck.Fail(group, name, $"{source} is not a path this process can open",
                    PathNamespace.Localize(source) is { } translated ? $"store it as {translated}" : null)
                : DoctorCheck.Warn(group, name,
                    $"{source} belongs to another plugin and is not a path this process can open");
        }

        if (!Directory.Exists(source))
        {
            return isOurs
                ? DoctorCheck.Fail(group, name, $"{source} does not exist")
                : DoctorCheck.Warn(group, name, $"{source} belongs to another plugin and does not exist");
        }

        if (MarketplaceManifests.Any(manifest => File.Exists(Path.Combine(source, Path.Combine(manifest)))))
        {
            return DoctorCheck.Ok(group, name, source);
        }

        return isOurs
            ? DoctorCheck.Fail(group, name, $"{source} contains no supported plugin manifest",
                "point the source at the repository root of a dotnet-toolkit clone")
            : DoctorCheck.Skip(group, name, $"{source} belongs to another plugin, in a layout this verifier does not read");
    }

    /// <summary>Decides whether a marketplace source names this plugin's own installation.</summary>
    /// <param name="source">The source path as configuration stores it, in either namespace.</param>
    /// <param name="pluginRoot">This plugin's installation directory.</param>
    /// <returns>True when the two resolve to the same directory.</returns>
    /// <remarks>
    /// The comparison is made after translation, so a Windows-spelled entry is still recognized as ours —
    /// which is the case that matters, since a foreign namespace is exactly when the distinction decides
    /// between a repair the reader must make and one that is none of their business.
    /// </remarks>
    private static bool IsThisPlugin(string source, string pluginRoot)
    {
        if (PathNamespace.Localize(source) is not { } local)
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(local)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginRoot)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Reports which SDKs are installed and which one the workspace would load with.</summary>
    /// <param name="repo">The repository whose <c>global.json</c> governs the choice.</param>
    /// <returns>The SDK section's checks.</returns>
    private static IEnumerable<DoctorCheck> CheckDotnet(string repo)
    {
        const string Group = "dotnet";

        var installed = MSBuildRegistration.InstalledSdks();
        if (installed.Count == 0)
        {
            yield return DoctorCheck.Fail(Group, "installed sdks", "none discoverable",
                "install a .NET 10 SDK, or set DOTNET_TOOLKIT_DOTNET_ROOT to an install root");
            yield break;
        }

        yield return DoctorCheck.Ok(Group, "installed sdks",
            string.Join(", ", installed.Select(sdk => $"{sdk.Version} ({sdk.Path})")));

        var globalJson = FindGlobalJson(repo);
        yield return globalJson is null
            ? DoctorCheck.Ok(Group, "global.json", "none governing this repository; the newest SDK wins")
            : DoctorCheck.Ok(Group, "global.json", globalJson);

        // Registering here is safe because this verb never loads a workspace afterwards, and it is the
        // only way to report the choice rather than a re-derivation of it that could disagree.
        var selection = MSBuildRegistration.Register(repo);
        yield return selection.StartsWith("no MSBuild", StringComparison.Ordinal)
            ? DoctorCheck.Fail(Group, "selected sdk", selection)
            : selection.Contains("no installed SDK satisfies", StringComparison.Ordinal)
                ? DoctorCheck.Warn(Group, "selected sdk", selection,
                    "install an SDK matching this repository's global.json, or relax its rollForward")
                : DoctorCheck.Ok(Group, "selected sdk", selection);
    }

    /// <summary>Checks the sandbox a Codex session on this executor depends on.</summary>
    /// <param name="host">The observed environment.</param>
    /// <returns>The sandbox section's checks.</returns>
    /// <remarks>
    /// Not this plugin's dependency, and deliberately reported anyway: without it a Codex session's
    /// filesystem-backed operations fail before any hook or tool result is visible, which reads as
    /// "dotnet-toolkit's hooks do not fire" and sends the user to debug the wrong component.
    /// </remarks>
    private static IEnumerable<DoctorCheck> CheckSandbox(HostProfile host)
    {
        const string Group = "sandbox";

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            yield return DoctorCheck.Skip(Group, "bubblewrap", "not used on this executor");
            yield break;
        }

        var found = OnPath("bwrap");
        yield return found is not null
            ? DoctorCheck.Ok(Group, "bubblewrap", found)
            : DoctorCheck.Warn(
                Group,
                "bubblewrap",
                "no bwrap on PATH; a Codex session here cannot sandbox filesystem operations, so hook and "
                + "tool probes may fail before producing a result"
                + (host.IsWsl ? string.Empty : " (only relevant when running Codex on this machine)"),
                "sudo apt-get install bubblewrap");
    }

    /// <summary>Launches a server and reports what a real session would get from it.</summary>
    /// <param name="options">The run's options.</param>
    /// <param name="codex">The profile in effect, whose registration is preferred as the probe target.</param>
    /// <param name="pluginRoot">The plugin's installation directory.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The live-session section's checks.</returns>
    private static async Task<IEnumerable<DoctorCheck>> CheckLiveSessionAsync(
        DoctorOptions options,
        CodexProfile codex,
        string pluginRoot,
        CancellationToken cancellationToken)
    {
        const string Group = "live session";

        if (options.NoProbe)
        {
            return [DoctorCheck.Skip(Group, "probe", "--no-probe was passed, so nothing was launched")];
        }

        // Prefer what is actually configured: probing our own dist/ would prove the binary healthy while
        // saying nothing about the registration a session will really use. Falling back to dist/ when the
        // registration is unusable is what separates "the server is broken" from "the path is wrong".
        var configured = codex.Server(ServerName);
        var useConfigured = configured?.Command is { } command
            && PathNamespace.IsUsableHere(command)
            && configured.Args.All(PathNamespace.IsUsableHere);

        var (probeCommand, probeArgs, probeEnv, target) = useConfigured
            ? (configured!.Command!, configured.Args, configured.Env, $"the configured mcp_servers.{ServerName}")
            : ("dotnet",
               (IReadOnlyList<string>)[Path.Combine(pluginRoot, Path.Combine(ServerDllPath))],
               (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal),
               "the plugin's own dist/ (the configured entry is not launchable here)");

        var environment = new Dictionary<string, string>(probeEnv, StringComparer.Ordinal)
        {
            ["DOTNET_TOOLKIT_PROJECT_DIR"] = options.Repo,
        };

        var result = await McpHandshakeProbe.RunAsync(
            probeCommand, probeArgs, environment, options.Repo, options.ProbeTimeout, cancellationToken);

        return [.. ReadProbe(Group, target, result, options.Repo)];
    }

    /// <summary>Turns one probe's outcome into the stage-by-stage report.</summary>
    /// <param name="group">The report section.</param>
    /// <param name="target">What was launched, in words.</param>
    /// <param name="result">The probe's outcome.</param>
    /// <param name="repo">The repository the probe pointed the server at.</param>
    /// <returns>One check per stage the probe distinguishes.</returns>
    private static IEnumerable<DoctorCheck> ReadProbe(
        string group,
        string target,
        McpProbeResult result,
        string repo)
    {
        yield return DoctorCheck.Ok(group, "probe target", target);

        if (!result.Started)
        {
            yield return DoctorCheck.Fail(group, "process started", result.Failure ?? "unknown failure",
                "check the command and its path against the codex profile section above");
            yield break;
        }

        yield return DoctorCheck.Ok(group, "process started", "yes");

        if (result.ProtocolVersion is null)
        {
            yield return DoctorCheck.Fail(group, "protocol initialized",
                result.Failure ?? "no initialize response",
                FirstStderrLine(result.Stderr) is { } line ? $"the server logged: {line}" : null);
            yield break;
        }

        yield return DoctorCheck.Ok(group, "protocol initialized",
            $"{result.ProtocolVersion}, {result.ServerName ?? "?"} {result.ServerVersion ?? "?"}");

        yield return result.Tools.Count == 0
            ? DoctorCheck.Fail(group, "tools discovered", "none", "the server started but registered no tools")
            : DoctorCheck.Ok(group, "tools discovered", $"{result.Tools.Count}: {string.Join(", ", result.Tools)}");

        yield return result.Ping is { Length: > 0 } ping
            ? DoctorCheck.Ok(group, "ping", ping)
            : DoctorCheck.Fail(group, "ping", result.Failure ?? "no reply");

        foreach (var check in ReadWorkspaceStatus(group, result, repo))
        {
            yield return check;
        }

        if (MSBuildLine(result.Stderr) is { } msbuild)
        {
            yield return DoctorCheck.Ok(group, "server sdk choice", msbuild);
        }
    }

    /// <summary>Judges the workspace the probed server actually loaded.</summary>
    /// <param name="group">The report section.</param>
    /// <param name="result">The probe's outcome.</param>
    /// <param name="repo">The repository the server was pointed at.</param>
    /// <returns>The workspace checks.</returns>
    /// <remarks>
    /// A server that answers <c>ping</c> is alive but not necessarily useful: a degraded workspace still
    /// serves every tool, silently and with thinner results. This is the check that keeps a green report
    /// honest about that.
    /// </remarks>
    private static IEnumerable<DoctorCheck> ReadWorkspaceStatus(string group, McpProbeResult result, string repo)
    {
        if (result.WorkspaceStatus is not { Length: > 0 } status)
        {
            yield return DoctorCheck.Fail(group, "workspace", result.Failure ?? "no reply");
            yield break;
        }

        var reportedRoot = Line(status, "root:");
        yield return reportedRoot is null
            ? DoctorCheck.Warn(group, "workspace root", "not reported")
            : string.Equals(
                Path.GetFullPath(reportedRoot).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
                ? DoctorCheck.Ok(group, "workspace root", reportedRoot)
                : DoctorCheck.Fail(group, "workspace root", $"{reportedRoot}, but --repo names {repo}",
                    "the server started in the wrong directory; check the registration's cwd");

        var index = Line(status, "index:");
        yield return index is null
            ? DoctorCheck.Warn(group, "index", "not reported")
            : index.StartsWith("ready", StringComparison.Ordinal)
                ? DoctorCheck.Ok(group, "index", index)
                : DoctorCheck.Warn(group, "index", index, "re-run once indexing has finished");

        var workspace = Line(status, "workspace:");
        if (workspace is null)
        {
            yield return DoctorCheck.Warn(group, "projects", "not reported");
        }
        else if (status.Contains("failed:", StringComparison.Ordinal))
        {
            yield return DoctorCheck.Fail(group, "projects", workspace,
                "run `dotnet restore` in this repository, in this same OS, then re-run doctor");
        }
        else if (workspace.StartsWith("loading", StringComparison.Ordinal))
        {
            // The probe already waited for this to settle, so still-loading here means it never did.
            // Passing it would report an unfinished load as a verified one.
            yield return DoctorCheck.Warn(group, "projects", $"{workspace} — still loading when the probe gave up",
                "re-run with a longer --timeout, or run `dotnet restore` first so the load is not doing it");
        }
        else
        {
            yield return DoctorCheck.Ok(group, "projects", workspace);
        }
    }

    /// <summary>Renders the report as text.</summary>
    /// <param name="checks">Every check's verdict.</param>
    /// <param name="options">The run's options, echoed in the header.</param>
    /// <returns>The full report.</returns>
    private static string RenderText(IReadOnlyList<DoctorCheck> checks, DoctorOptions options)
    {
        var lines = new List<string>
        {
            $"dotnet-toolkit doctor {BuildIdentity.Full}",
            $"repository: {options.Repo}",
            string.Empty,
        };

        foreach (var group in checks.Select(check => check.Group).Distinct(StringComparer.Ordinal))
        {
            lines.Add($"{group}:");
            foreach (var check in checks.Where(check => check.Group == group))
            {
                lines.Add($"  {Marker(check.Status)} {check.Name}: {check.Detail}");
                if (check.Fix is { } fix)
                {
                    lines.Add($"         fix: {fix}");
                }
            }

            lines.Add(string.Empty);
        }

        var failed = checks.Count(check => check.Status == CheckStatus.Fail);
        var warned = checks.Count(check => check.Status == CheckStatus.Warn);
        var skipped = checks.Count(check => check.Status == CheckStatus.Skip);

        lines.Add(failed == 0
            ? $"PASS — {checks.Count - warned - skipped} ok, {warned} warning(s), {skipped} not checked"
            : $"FAIL — {failed} failure(s), {warned} warning(s), {skipped} not checked");

        return string.Join('\n', lines);
    }

    /// <summary>Renders the report as JSON, for a caller that wants to assert on it.</summary>
    /// <param name="checks">Every check's verdict.</param>
    /// <returns>The report as a JSON document.</returns>
    private static string RenderJson(IReadOnlyList<DoctorCheck> checks) =>
        JsonSerializer.Serialize(
            new
            {
                build = BuildIdentity.Full,
                passed = checks.All(check => check.Status != CheckStatus.Fail),
                checks = checks.Select(check => new
                {
                    group = check.Group,
                    name = check.Name,
                    status = check.Status.ToString().ToLowerInvariant(),
                    detail = check.Detail,
                    fix = check.Fix,
                }),
            },
            new JsonSerializerOptions { WriteIndented = true });

    /// <summary>The one-character marker a status shows as in the text report.</summary>
    /// <param name="status">The verdict.</param>
    /// <returns>A fixed-width marker, so the report's columns line up.</returns>
    private static string Marker(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "[ ok ]",
        CheckStatus.Warn => "[warn]",
        CheckStatus.Fail => "[FAIL]",
        _ => "[skip]",
    };

    /// <summary>Reads a labelled line out of a <c>workspace_status</c> reply.</summary>
    /// <param name="text">The reply.</param>
    /// <param name="prefix">The label, including its colon.</param>
    /// <returns>The text after the label, or null when the reply has no such line.</returns>
    private static string? Line(string text, string prefix) =>
        text.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))
            ?[prefix.Length..]
            .Trim();

    /// <summary>Finds the startup line naming the SDK the server registered.</summary>
    /// <param name="stderr">Everything the server logged.</param>
    /// <returns>The line, or null when the server logged none.</returns>
    private static string? MSBuildLine(string stderr) =>
        stderr.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("MSBuild", StringComparison.Ordinal));

    /// <summary>Finds the first non-empty line the server logged, for a failure message.</summary>
    /// <param name="stderr">Everything the server logged.</param>
    /// <returns>The line, or null when it logged nothing.</returns>
    private static string? FirstStderrLine(string stderr) =>
        stderr.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);

    /// <summary>Reads the <c>version</c> field out of a plugin manifest.</summary>
    /// <param name="path">The manifest's path.</param>
    /// <returns>The declared version, or null when the file is absent or unreadable.</returns>
    private static string? ManifestVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Finds the <c>global.json</c> governing a directory.</summary>
    /// <param name="start">The directory to search upward from.</param>
    /// <returns>The nearest <c>global.json</c> at or above it, or null when there is none.</returns>
    private static string? FindGlobalJson(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "global.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Finds an executable on <c>PATH</c>.</summary>
    /// <param name="name">The executable's name, without an extension.</param>
    /// <returns>Its full path, or null when it is not on <c>PATH</c>.</returns>
    private static string? OnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return path.Split(Path.PathSeparator)
            .Where(directory => directory.Length > 0)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>What one <c>doctor</c> run was asked to verify.</summary>
    /// <param name="Repo">The repository to verify against.</param>
    /// <param name="Json">Whether to render the report as JSON.</param>
    /// <param name="NoProbe">Whether to skip launching a server.</param>
    /// <param name="ProbeTimeout">How long the live probe may take.</param>
    private sealed record DoctorOptions(string Repo, bool Json, bool NoProbe, TimeSpan ProbeTimeout)
    {
        /// <summary>Reads the verb's arguments.</summary>
        /// <param name="args">Everything after <c>doctor</c>.</param>
        /// <returns>The options, with the current directory as the repository unless one was named.</returns>
        /// <remarks>
        /// An unrecognized argument is ignored rather than rejected: a verifier that refuses to run
        /// because of a typo in one of its own flags withholds the report the user is trying to obtain.
        /// </remarks>
        public static DoctorOptions Parse(string[] args)
        {
            var repo = Directory.GetCurrentDirectory();
            var json = false;
            var noProbe = false;
            var timeout = DefaultProbeTimeout;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--repo" when i + 1 < args.Length:
                        repo = args[++i];
                        break;
                    case "--json":
                        json = true;
                        break;
                    case "--no-probe":
                        noProbe = true;
                        break;
                    case "--timeout" when i + 1 < args.Length
                        && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var seconds):
                        timeout = TimeSpan.FromSeconds(seconds);
                        i++;
                        break;
                }
            }

            return new DoctorOptions(Path.GetFullPath(repo), json, noProbe, timeout);
        }
    }
}
