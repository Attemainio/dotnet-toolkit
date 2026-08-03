@echo off
rem Publishes the MCP server into dist/, which .mcp.json and hooks/hooks.json both point at.
rem Windows twin of build-plugin.sh; both just run the canonical dotnet publish below.
setlocal
cd /d "%~dp0.."

dotnet publish src\DotnetToolkit.McpServer -c Release -o dist || exit /b 1
echo Plugin server published to dist\. Install with: claude --plugin-dir %CD%
