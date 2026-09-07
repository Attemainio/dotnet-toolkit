namespace DotnetToolkit.McpServer.Diagnostics;

/// <summary>One MCP server as a Codex profile stores it.</summary>
/// <param name="Name">The server name Codex registers the tools under.</param>
/// <param name="Command">The executable to launch, or null when the entry names none.</param>
/// <param name="Args">The arguments passed to it, in order.</param>
/// <param name="Env">Environment variables the entry sets for the launched process.</param>
/// <param name="Enabled">
/// Whether the entry is switched on. Absent means enabled: Codex's own default, and the reason a
/// diagnostic must not treat a missing key as "off".
/// </param>
internal sealed record CodexMcpServer(
    string Name,
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    bool Enabled);

/// <summary>One plugin marketplace as a Codex profile stores it.</summary>
/// <param name="Name">The marketplace name.</param>
/// <param name="SourceType">The source kind, e.g. <c>local</c>.</param>
/// <param name="Source">The path or URL the marketplace is loaded from.</param>
internal sealed record CodexMarketplace(string Name, string? SourceType, string? Source);

/// <summary>
/// The Codex profile in effect for this process: which <c>config.toml</c> was read, and what MCP servers
/// and marketplaces it registers.
/// </summary>
/// <param name="Home">The profile directory, from <c>CODEX_HOME</c> or the default location.</param>
/// <param name="ConfigPath">The configuration file inside it, whether or not it exists.</param>
/// <param name="Exists">Whether that file was found and read.</param>
/// <param name="Servers">Every entry under <c>[mcp_servers.*]</c>.</param>
/// <param name="Marketplaces">Every entry under <c>[marketplaces.*]</c>.</param>
/// <param name="ReadError">Why the file could not be read, when it exists but could not be opened.</param>
/// <remarks>
/// Read-only by construction. The plugin has no business rewriting a profile it does not own, and a
/// diagnostic that repaired configuration silently would remove the one signal telling the user their
/// two installations disagree.
/// </remarks>
/// <seealso cref="HostProfile"/>
internal sealed record CodexProfile(
    string Home,
    string ConfigPath,
    bool Exists,
    IReadOnlyList<CodexMcpServer> Servers,
    IReadOnlyList<CodexMarketplace> Marketplaces,
    string? ReadError)
{
    /// <summary>The table prefix Codex stores MCP server registrations under.</summary>
    private const string ServersTable = "mcp_servers";

    /// <summary>The table prefix Codex stores plugin marketplaces under.</summary>
    private const string MarketplacesTable = "marketplaces";

    /// <summary>Reads the profile at a directory.</summary>
    /// <param name="home">The profile directory to read.</param>
    /// <returns>
    /// The profile's contents, or an empty one with <see cref="Exists"/> false when the directory holds
    /// no <c>config.toml</c> — an absent profile is an ordinary state, not a failure.
    /// </returns>
    public static CodexProfile Load(string home)
    {
        var configPath = Path.Combine(home, "config.toml");
        if (!File.Exists(configPath))
        {
            return new CodexProfile(home, configPath, Exists: false, [], [], ReadError: null);
        }

        string text;
        try
        {
            text = File.ReadAllText(configPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A profile on a Windows volume read from WSL can fail on permissions alone. Reporting why
            // beats reporting nothing, since that failure is itself one of the cross-namespace symptoms
            // this diagnostic exists to name.
            return new CodexProfile(home, configPath, Exists: true, [], [], ex.Message);
        }

        var document = TomlDocument.Parse(text);

        return new CodexProfile(
            home,
            configPath,
            Exists: true,
            Servers: [.. document.ChildrenOf(ServersTable).Select(name => ReadServer(document, name))],
            Marketplaces: [.. document.ChildrenOf(MarketplacesTable).Select(name => new CodexMarketplace(
                name,
                document.Scalar($"{MarketplacesTable}.{name}", "source_type"),
                document.Scalar($"{MarketplacesTable}.{name}", "source")))],
            ReadError: null);
    }

    /// <summary>Finds the registration for one MCP server by name.</summary>
    /// <param name="name">The server name, e.g. <c>dotnet</c>.</param>
    /// <returns>The entry, or null when the profile registers no server under that name.</returns>
    public CodexMcpServer? Server(string name) =>
        Servers.FirstOrDefault(server => string.Equals(server.Name, name, StringComparison.Ordinal));

    /// <summary>Reads one <c>[mcp_servers.&lt;name&gt;]</c> table.</summary>
    /// <param name="document">The parsed profile.</param>
    /// <param name="name">The server's name.</param>
    /// <returns>The entry, with environment variables gathered from both spellings Codex accepts.</returns>
    private static CodexMcpServer ReadServer(TomlDocument document, string name)
    {
        var table = $"{ServersTable}.{name}";
        var entries = document.Table(table);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        // env is writable two ways -- an inline table on the entry, or its own [mcp_servers.x.env]
        // section -- and a user following one worked example while a diagnostic reads only the other
        // gets told their override is missing when it is present. Both are gathered.
        if (entries is not null)
        {
            foreach (var (key, value) in entries)
            {
                if (key.StartsWith("env.", StringComparison.Ordinal) && value.Scalar is { } scalar)
                {
                    env[key["env.".Length..]] = scalar;
                }
            }
        }

        if (document.Table($"{table}.env") is { } envTable)
        {
            foreach (var (key, value) in envTable)
            {
                if (value.Scalar is { } scalar)
                {
                    env[key] = scalar;
                }
            }
        }

        var enabled = document.Scalar(table, "enabled");

        return new CodexMcpServer(
            name,
            document.Scalar(table, "command"),
            entries?.TryGetValue("args", out var args) == true ? args.AsList : [],
            env,
            Enabled: !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase));
    }
}
