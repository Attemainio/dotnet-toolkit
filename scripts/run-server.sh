#!/usr/bin/env bash
# MCP server launcher. Prefers a user-local .NET install (~/.dotnet) so the plugin
# works even when the system-wide dotnet is older than net10.0.
set -euo pipefail
PLUGIN_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  DOTNET="$HOME/.dotnet/dotnet"
else
  DOTNET="dotnet"
fi
exec "$DOTNET" "$PLUGIN_ROOT/dist/DotnetToolkit.McpServer.dll"
