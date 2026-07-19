using DotnetToolkit.McpServer.Devlog;
using DotnetToolkit.McpServer.Indexing;
using DotnetToolkit.McpServer.Store;
using DotnetToolkit.McpServer.Telemetry;
using DotnetToolkit.McpServer.Workspace;
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Locate the installed .NET SDK's MSBuild before any Microsoft.CodeAnalysis.Workspaces.MSBuild
// code runs; MSBuildWorkspace resolves MSBuild assemblies through this registration.
if (!MSBuildLocator.IsRegistered)
    MSBuildLocator.RegisterDefaults();

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
builder.Services.AddSingleton<TelemetryRecorder>();
builder.Services.AddSingleton<MetricsReader>();
builder.Services.AddSingleton<AttributionJob>();
builder.Services.AddSingleton<SymbolStore>();
builder.Services.AddSingleton<FeatureLogStore>();
builder.Services.AddSingleton<SymbolIndexBuilder>();
builder.Services.AddSingleton<DotnetToolkit.McpServer.Validation.TargetedTests>();
builder.Services.AddSingleton<DotnetToolkit.McpServer.Git.GitAnalyzer>();
builder.Services.AddSingleton<DotnetToolkit.McpServer.Git.SemanticDiff>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Start both knowledge tiers in the background so the MCP handshake completes
// well inside Claude Code's ~5s startup timeout; tools await readiness themselves.
app.Services.GetRequiredService<ProjectIndex>().StartInitialization();
app.Services.GetRequiredService<WorkspaceHost>().StartLoading();
// Populate the SQLite symbol index + edge cache once the workspace is ready (it self-awaits).
app.Services.GetRequiredService<SymbolIndexBuilder>().Start();

// One-time import of the legacy markdown devlog into feature_log (no-op once the log has entries).
DevlogMigration.Run(
    app.Services.GetRequiredService<DevlogStore>(),
    app.Services.GetRequiredService<FeatureLogStore>(),
    app.Services.GetRequiredService<ILogger<Program>>());

await app.RunAsync();
