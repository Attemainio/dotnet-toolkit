using DotnetToolkit.McpServer.Control;
using DotnetToolkit.McpServer.Diagnostics;
using DotnetToolkit.McpServer.Hooks;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Tools;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// A `hook` invocation runs one Claude Code guard against stdin and exits, and is handled before
// anything else: hooks run once per tool call, so MSBuild discovery and host startup would be pure
// latency there, and no guard needs either.
if (await HookCli.TryRunAsync(args) is { } hookExitCode)
{
    return hookExitCode;
}

// A `doctor` invocation verifies an installation and exits. Handled before host startup for the same
// reason `hook` is, plus one of its own: it launches the CONFIGURED server command as a child to prove
// that command actually runs, and a server already started here would prove nothing about it.
if (await Doctor.TryRunAsync(args) is { } doctorExitCode)
{
    return doctorExitCode;
}

// A `stamp` invocation records which sources this binary was published from and exits. Run by the
// publish scripts against the binary they just produced, so the stamp is written by the build it
// describes rather than from a source tree that may already have moved past it.
if (args is ["stamp", ..])
{
    return SourceStamp.Write(args.Length > 1 ? args[1] : Directory.GetCurrentDirectory());
}

// MSBuild registration is NOT done here. Which SDK to load a repository with depends on that
// repository's global.json, and the root is not known until SolutionLocator has resolved it -- so
// WorkspaceHost registers at its first load, before anything touches an MSBuild type, and logs which
// SDK won. Registering at startup meant picking the machine's newest SDK, which is a machine-global
// answer to a per-repository question: it could load a workspace with MSBuild from a feature band the
// repository never builds with, and it made this server's restore disagree with the developer's own
// `dotnet build` over one shared NuGet assets cache.
var builder = Host.CreateEmptyApplicationBuilder(settings: null);

// stdout carries the MCP JSON-RPC protocol; every log line must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton<SolutionLocator>();
builder.Services.AddSingleton<ProjectIndex>();
builder.Services.AddSingleton<WorkspaceHost>();

// v2 knowledge store + telemetry (spec Part IV). The store is rebuildable; if it fails to
// open, KnowledgeStore.Available stays false and telemetry degrades to no-ops.
builder.Services.AddSingleton<KnowledgeStore>();
builder.Services.AddSingleton<IKnowledgeStore>(sp => sp.GetRequiredService<KnowledgeStore>());
builder.Services.AddSingleton<TelemetryRecorder>();
builder.Services.AddSingleton<MetricsReader>();

builder.Services.AddSingleton<SymbolStore>();
builder.Services.AddSingleton<SymbolIndexBuilder>();
builder.Services.AddSingleton<CallSlice>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ControlServer>();
builder.Services.AddSingleton<DotnetToolkit.McpServer.Git.GitAnalyzer>();
builder.Services.AddSingleton<DotnetToolkit.McpServer.Git.SemanticDiff>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    // Without this, anything a tool throws reaches the caller as the SDK's bare "An error occurred
    // invoking '<tool>'", which names neither the subsystem nor the cause.
    .WithFailureDetail();

var app = builder.Build();

// Start both knowledge tiers in the background so the MCP handshake completes
// well inside Claude Code's ~5s startup timeout; tools await readiness themselves.
app.Services.GetRequiredService<ProjectIndex>().StartInitialization();
app.Services.GetRequiredService<WorkspaceHost>().StartLoading();
// Seeds the runtime-mutable Formats.Current once; set_output_format (ServerTools) is the only
// other writer, so a session can change the active format later without a restart.
Formats.Current = Formats.Parse(app.Services.GetRequiredService<SolutionLocator>().Config.DefaultFormat);
// Populate the SQLite symbol index + edge cache once the workspace is ready (it self-awaits).
app.Services.GetRequiredService<SymbolIndexBuilder>().Start();

// Loopback control channel for hooks that need to trigger a rescan/reload outside the MCP session.
app.Services.GetRequiredService<ControlServer>().Start();

// Telemetry covers this process and no other, so the raw tables are cleared at both ends of its life.
// The startup purge inside KnowledgeStore is the load-bearing one - an MCP server is usually killed
// rather than stopped, and a killed process never reaches this callback - but clearing on a graceful
// stop too keeps the database from holding a finished session's rows until the next start.
app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(
    app.Services.GetRequiredService<KnowledgeStore>().PurgeTelemetry);

await app.RunAsync();

// Explicit because the hook branch above returns a value, which makes the entry point int-returning.
return 0;
