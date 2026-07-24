#!/usr/bin/env bash
# PreToolUse guard: block a raw Read on a .cs file that is actually compiled by a project, pointing
# at search_index/get_symbol instead.
#
# Why this can't be left to CLAUDE.md/skills alone: they are context, not enforcement, and adherence
# decays over a long session — the same reasoning behind guard-cs-edit.sh's Edit/Write block. Read
# has no equivalent today, so a Read on a large multi-symbol file quietly pulls in every method in it
# whether or not the task needs them, at a cost get_symbol (one symbol, or a type's member list) and
# search_index (ranked hits with file/line, no truncation) do not pay.
#
# The membership check itself (nearest .csproj, nested-solution exception, <Compile Remove> exclusion)
# lives in lib-cs-membership.sh, shared with guard-cs-bash-read.sh — the Bash-tool counterpart that
# closes the gap this script alone leaves open: `cat`/`sed`/`grep`/etc. via Bash read the same bytes
# but are a different tool name, so a matcher scoped to "Read" alone never sees them. Two scripts
# asking the same membership question from two tool surfaces must never drift on the answer.
#
# A gap this cannot close: a file that IS governed by a project while the server's own workspace is
# still index_only or degraded. That is runtime state of the running process, invisible to a static,
# filesystem-only check for the same reason WorkspaceHost itself is invisible to this script.
#
# Denial is exit-2-plus-stderr, same as guard-cs-edit.sh, so this script only ever needs a JSON
# *reader*, never a JSON *writer*. Fails OPEN by design: a missing interpreter, an unparseable
# payload, or an unresolvable project root must not block a legitimate read, so every uncertain path
# exits 0 (allow) silently - no stderr, no output, nothing surfaced to the agent or user.

set -uo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/lib-cs-membership.sh"

payload=$(cat)

# Same two-line tool_name/file_path extraction as guard-cs-edit.sh, and the same reasoning for the
# node/python3/jq fallback chain: no single JSON tool is guaranteed present on a consumer machine.
extract() {
    if command -v node >/dev/null 2>&1; then
        node -e '
            let s = "";
            process.stdin.on("data", d => s += d);
            process.stdin.on("end", () => {
                try {
                    const j = JSON.parse(s);
                    console.log(j.tool_name ?? "");
                    console.log(j.tool_input?.file_path ?? "");
                } catch { process.exit(1); }
            });
        ' 2>/dev/null
    elif command -v python3 >/dev/null 2>&1; then
        python3 -c '
import json, sys
try:
    j = json.load(sys.stdin)
except Exception:
    sys.exit(1)
print(j.get("tool_name") or "")
print((j.get("tool_input") or {}).get("file_path") or "")
' 2>/dev/null
    elif command -v jq >/dev/null 2>&1; then
        jq -r '.tool_name // "", (.tool_input.file_path // "")' 2>/dev/null
    else
        return 1
    fi
}

parsed=$(printf '%s' "$payload" | extract) || exit 0
tool=$(printf '%s\n' "$parsed" | sed -n '1p')
file=$(printf '%s\n' "$parsed" | sed -n '2p')

[ "$tool" = "Read" ] || exit 0
[ -n "$file" ] || exit 0
case "$file" in
    *.cs) ;;
    *) exit 0 ;;
esac
[ -f "$file" ] || exit 0

# Same root precedence as Workspace/SolutionLocator.cs, so the walk below bounds itself to the same
# repo the server itself would resolve.
root="${DOTNET_TOOLKIT_PROJECT_DIR:-${CLAUDE_PROJECT_DIR:-$PWD}}"
root="$(cd "$root" 2>/dev/null && pwd -P)" || exit 0
abs_dir="$(cd "$(dirname "$file")" 2>/dev/null && pwd -P)" || exit 0
abs_file="$abs_dir/$(basename "$file")"

is_governed_cs_file "$abs_file" "$root" || exit 0

cat >&2 <<EOF
Blocked Read on ${file}: it is compiled by ${REL_CSPROJ}, so search_index/get_symbol answer this more
cheaply and completely than a raw file read - no truncation risk, and no irrelevant methods pulled in
alongside the one you want.

Do this instead:
  - Don't know the exact symbol name: search_index(query: "term1 term2 ...") - ranked hits with
    symbolId, file, and line, all in one call.
  - Know the type/member name: get_symbol(symbol: "...", include: "members") to enumerate a type, or
    the default include for one member's declaration, xmlDoc, and reference counts. Its source
    component includes the symbol's own leading /// doc comment now, not just the signature/body.
  - Orienting in a folder you already know: search_index(query: "...", pathPrefix: "path/to/folder")
    scopes ranked results to one file or directory.

If this genuinely needs a raw read (the workspace failed to load, or the file's exact formatting/byte
layout is itself what you need to see), say so and ask the user to allow it explicitly rather than
retrying Read on this file.
EOF
exit 2
