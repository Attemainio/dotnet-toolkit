#!/usr/bin/env bash
# Builds a clean plugin distribution directory to use as a local marketplace source.
#
# WHY THIS EXISTS: a host's plugin installer snapshots the marketplace root verbatim. Pointed at this
# working tree it copies 632 MB of `src/`, `tests/`, `.git/` and build output -- and then fails
# outright, because Visual Studio holds `.vs/<solution>/FileContentIndex/*.vsidx` open with a
# deny-read lock and the copy aborts with `Permission denied (os error 13)`. That is not a Codex bug:
# no packaging step should ever snapshot a live development tree.
#
# A fresh `git clone` has none of those directories, so installing from a remote marketplace is
# unaffected. This script is what makes a LOCAL install from a working repo behave the same way.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$root/package}"

if [ ! -f "$root/dist/DotnetToolkit.McpServer.dll" ]; then
    echo "package-plugin: dist/ has no published server - run scripts/build-plugin.sh first" >&2
    exit 1
fi

# Everything a consuming session actually reaches for. Deliberately an allow-list: an exclude-list
# silently ships whatever a later commit adds at the repository root.
payload=(
    .claude-plugin      # marketplace.json + plugin.json + mcp.json (Claude)
    .codex-plugin       # plugin.json + mcp.json (Codex)
    skills
    agents
    hooks
    standards
    docs                # skills join pluginRoot with docs/tools/<tool>.md at runtime
    dist                # the published server and its build-manifest.json
    AGENTS.md
    README.md
    LICENSE.txt
)

rm -rf "$out"
mkdir -p "$out"

for item in "${payload[@]}"; do
    if [ ! -e "$root/$item" ]; then
        echo "package-plugin: missing $item" >&2
        exit 1
    fi
    cp -R "$root/$item" "$out/"
done

# The always-loaded rule only -- not the rest of .claude/, which is this repo's own session state.
mkdir -p "$out/.claude"
cp -R "$root/.claude/rules" "$out/.claude/"

# A packaging step that quietly ships a development tree is the bug this script exists to prevent,
# so verify rather than trust the allow-list above.
for forbidden in .git .vs src tests obj bin scripts; do
    if [ -e "$out/$forbidden" ]; then
        echo "package-plugin: $forbidden leaked into the package" >&2
        exit 1
    fi
done

if find "$out" -type d \( -name bin -o -name obj \) -print -quit | grep -q .; then
    echo "package-plugin: build output leaked into the package" >&2
    exit 1
fi

echo "Plugin packaged to $out ($(du -sh "$out" | cut -f1), $(find "$out" -type f | wc -l | tr -d ' ') files)"
echo "Use it as the marketplace source:"
echo "  codex plugin marketplace add $out"
echo "  claude --plugin-dir $out"
