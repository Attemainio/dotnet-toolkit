using DotnetToolkit.McpServer.Control;
using DotnetToolkit.McpServer.Devlog;
using DotnetToolkit.McpServer.Hooks;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Output;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
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

// Locate the installed .NET SDK's MSBuild before any Microsoft.CodeAnalysis.Workspaces.MSBuild
// code runs; MSBuildWorkspace resolves MSBuild assemblies through this registration.
var msbuildRegistration = MSBuildRegistration.Register();

var builder = Host.CreateEmptyApplicationBuilder(settings: null);

// stdout carries the MCP JSON-RPC protocol; every log line must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton<SolutionLocator>();
builder.Services.AddSingleton<ProjectIndex>();
builder.Services.AddSingleton<WorkspaceHost>();
builder.Services.AddSingleton<DevlogStore>();

// v2 knowledge store + telemetry (spec Part IV). The store is rebuildable; if it fails to
// open, KnowledgeStore.Available stays false and telemetry degrades to no-ops.
builder.Services.AddSingleton<KnowledgeStore>();
builder.Services.AddSingleton<IKnowledgeStore>(sp => sp.GetRequiredService<KnowledgeStore>());
builder.Services.AddSingleton<TelemetryRecorder>();
builder.Services.AddSingleton<MetricsReader>();

builder.Services.AddSingleton<SymbolStore>();
builder.Services.AddSingleton<FeatureLogStore>();
builder.Services.AddSingleton<SymbolIndexBuilder>();
builder.Services.AddSingleton<CallSlice>();
builder.Services.AddSingleton<DotnetToolkit.McpServer.Validation.TargetedTests>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DotnetToolkit.McpServer.Validation.PatchDraftStore>();
builder.Services.AddSingleton<ControlServer>();
builder.Services.AddSingleton<DotnetToolkit.McpServer.Git.GitAnalyzer>();
builder.Services.AddSingleton<DotnetToolkit.McpServer.Git.SemanticDiff>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Reported at startup because a workspace loaded by the wrong SDK degrades quietly - projects load
// with missing references instead of failing - and this is the only line that says which one won.
app.Services.GetRequiredService<ILogger<Program>>().LogInformation(
    "MSBuild: {Registration}", msbuildRegistration);

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

// One-time import of the legacy markdown devlog into feature_log (no-op once the log has entries).
DevlogMigration.Run(
    app.Services.GetRequiredService<DevlogStore>(),
    app.Services.GetRequiredService<FeatureLogStore>(),
    app.Services.GetRequiredService<ILogger<Program>>());

// Telemetry covers this process and no other, so the raw tables are cleared at both ends of its life.
// The startup purge inside KnowledgeStore is the load-bearing one - an MCP server is usually killed
// rather than stopped, and a killed process never reaches this callback - but clearing on a graceful
// stop too keeps the database from holding a finished session's rows until the next start.
app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(
    app.Services.GetRequiredService<KnowledgeStore>().PurgeTelemetry);

await app.RunAsync();

// Explicit because the hook branch above returns a value, which makes the entry point int-returning.
return 0;
