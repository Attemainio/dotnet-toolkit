@echo off
rem Publishes the MCP server into dist/, which .mcp.json and hooks/hooks.json both point at.
rem Windows twin of build-plugin.sh; both just run the canonical dotnet publish below.
setlocal
cd /d "%~dp0.."

dotnet publish src\DotnetToolkit.McpServer -c Release -o dist || exit /b 1

rem Trim dist\runtimes to what a framework-dependent publish actually loads. Microsoft.Data.Sqlite
rem pulls SQLitePCLRaw, which ships native SQLite for all 31 RIDs (Android, iOS, wasm, riscv64,
rem s390x, ...) plus .a static archives only a NativeAOT or static link would use. That is 85 MB of
rem an ~116 MB dist\, and dist\ is COMMITTED, so every clone pays for platforms we never run on.
rem Keep every desktop/server RID a .NET 10 host plausibly starts on. Mirror any change in
rem build-plugin.sh.
set "KEEP= win win-x64 win-arm64 win-x86 linux-x64 linux-arm64 linux-arm linux-musl-x64 linux-musl-arm64 osx-x64 osx-arm64 "
if exist dist\runtimes (
    for /r "dist\runtimes" %%F in (*.a) do del /q "%%F"
    for /d %%D in ("dist\runtimes\*") do (
        echo %KEEP% | findstr /i /c:" %%~nxD " >nul || rd /s /q "%%D"
    )
)

echo Plugin server published to dist\. Install with: claude --plugin-dir %CD%
