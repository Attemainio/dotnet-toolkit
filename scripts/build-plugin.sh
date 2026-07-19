#!/usr/bin/env bash
# Publishes the MCP server into dist/, which .mcp.json points at.
set -euo pipefail
cd "$(dirname "$0")/.."

# Prefer a user-local .NET install, as run-server.sh does: the system-wide dotnet may predate net10.0.
if [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  DOTNET="$HOME/.dotnet/dotnet"
else
  DOTNET="dotnet"
fi

"$DOTNET" publish src/DotnetToolkit.McpServer -c Release -o dist
echo "Plugin server published to dist/. Install with: claude --plugin-dir $(pwd)"
