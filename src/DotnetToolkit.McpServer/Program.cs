using DotnetToolkit.McpServer.Devlog;
using DotnetToolkit.McpServer.Indexing;
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

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Start both knowledge tiers in the background so the MCP handshake completes
// well inside Claude Code's ~5s startup timeout; tools await readiness themselves.
app.Services.GetRequiredService<ProjectIndex>().StartInitialization();
app.Services.GetRequiredService<WorkspaceHost>().StartLoading();

await app.RunAsync();
