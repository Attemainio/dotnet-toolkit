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

# Trim dist/runtimes to what a framework-dependent publish actually loads.
#
# Microsoft.Data.Sqlite pulls SQLitePCLRaw, which ships native SQLite for all 31 RIDs - Android, iOS,
# iOS simulator, wasm, riscv64, mips64, s390x, ppc64le - plus .a static archives that only a NativeAOT
# or static link would use. Nothing here is loaded at runtime except the .so/.dll/.dylib for the RID
# we are running on. Left alone it is 85 MB of an ~116 MB dist/, and dist/ is COMMITTED, so every
# `git clone` and `git pull` of this repo pays for platforms Claude Code does not run on.
#
# Keep every desktop/server RID a .NET 10 host plausibly starts on, including musl for Alpine
# containers, and `win` for the managed System.Diagnostics.EventLog assembly. Deleting a RID only
# costs a rebuild, so err toward keeping one.
KEEP_RIDS="win win-x64 win-arm64 win-x86 linux-x64 linux-arm64 linux-arm linux-musl-x64 linux-musl-arm64 osx-x64 osx-arm64"
if [ -d dist/runtimes ]; then
    find dist/runtimes -name '*.a' -delete
    for rid_dir in dist/runtimes/*/; do
        rid=$(basename "$rid_dir")
        case " $KEEP_RIDS " in
            *" $rid "*) ;;
            *) rm -rf "$rid_dir" ;;
        esac
    done
fi

echo "Plugin server published to dist/. Install with: claude --plugin-dir $(pwd)"
