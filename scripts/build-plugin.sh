#!/usr/bin/env bash
# Publishes the MCP server into dist/, which .mcp.json points at.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet publish src/DotnetToolkit.McpServer -c Release -o dist
echo "Plugin server published to dist/. Install with: claude --plugin-dir $(pwd)"
