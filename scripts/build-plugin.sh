#!/usr/bin/env bash
# Publishes the MCP server into dist/, which .mcp.json and hooks/hooks.json both point at.
#
# A convenience wrapper, not a dependency: this is a developer script the harness never executes, so
# the canonical command is the one it runs, and `scripts\build-plugin.cmd` is its Windows twin. Nothing
# the plugin ships at runtime needs a shell any more - the MCP server and every hook are invoked as
# `dotnet <dll>`.
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet publish src/DotnetToolkit.McpServer -c Release -o dist
echo "Plugin server published to dist/. Install with: claude --plugin-dir $(pwd)"
