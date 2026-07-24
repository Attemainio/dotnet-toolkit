#!/usr/bin/env bash
# PreToolUse guard: block a Bash command that reads a compiled .cs file's raw bytes the same way Read
# would (cat/sed/head/tail/grep/awk/etc.), in favor of search_index/get_symbol.
#
# guard-cs-read.sh only ever sees the Read tool by name — its matcher is "Read", so a shell command
# that dumps the same file's content into the transcript via Bash is invisible to it. That is not a
# sanctioned escape hatch; it is a gap in what tool name the enforcement watches, and this script closes
# it the same way guard-cs-edit.sh/guard-cs-read.sh close theirs: by matching on the tool ("Bash") and
# inspecting what it is about to do, not by trusting that CLAUDE.md's guidance holds under a long
# session. The membership question ("does this repo's own solution compile this file") is identical to
# guard-cs-read.sh's, so both scripts share lib-cs-membership.sh rather than answering it twice.
#
# What this does NOT try to do: parse the command line like a real shell would. It splits on pipeline/
# statement separators (| ; && ||), takes each segment's first word as the invoked command, and — for a
# segment whose command is a known read utility — looks for a bare .cs-suffixed argument token. Quoted
# paths containing spaces, variable-expanded paths, and heredocs are not recognized; that under-detection
# is deliberate, matching this file's fail-OPEN posture (see below) rather than a security boundary that
# needs to be airtight. git/dotnet/find and any command not in the blocklist are never touched, so
# `git diff -- Foo.cs`, `git log Foo.cs`, and `find . -name '*.cs'` are all unaffected.
#
# Fails OPEN by design, same as guard-cs-read.sh: a missing interpreter, an unparseable payload, or an
# unresolvable project root must not block a legitimate command, so every uncertain path exits 0 silently.

set -uo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/lib-cs-membership.sh"

payload=$(cat)

extract() {
    if command -v node >/dev/null 2>&1; then
        node -e '
            let s = "";
            process.stdin.on("data", d => s += d);
            process.stdin.on("end", () => {
                try {
                    const j = JSON.parse(s);
                    console.log(j.tool_name ?? "");
                    console.log(j.tool_input?.command ?? "");
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
print((j.get("tool_input") or {}).get("command") or "")
' 2>/dev/null
    elif command -v jq >/dev/null 2>&1; then
        jq -r '.tool_name // "", (.tool_input.command // "")' 2>/dev/null
    else
        return 1
    fi
}

parsed=$(printf '%s' "$payload" | extract) || exit 0
tool=$(printf '%s\n' "$parsed" | sed -n '1p')
command=$(printf '%s\n' "$parsed" | sed -n '2,$p')

[ "$tool" = "Bash" ] || exit 0
[ -n "$command" ] || exit 0

root="${DOTNET_TOOLKIT_PROJECT_DIR:-${CLAUDE_PROJECT_DIR:-$PWD}}"
root="$(cd "$root" 2>/dev/null && pwd -P)" || exit 0

blocklist=" ${DOTNET_TOOLKIT_READ_BLOCKLIST:-cat sed head tail less more awk gawk grep egrep fgrep rg ag nl tac bat} "

# Split on pipeline/statement separators into one candidate command per line.
segments=$(printf '%s' "$command" | sed -E 's/(\|\||&&|[|;])/\n/g')

while IFS= read -r seg; do
    [ -n "${seg// /}" ] || continue
    read -r first _rest <<< "$seg"
    [ -n "$first" ] || continue
    cmdname=$(basename -- "$first")
    case "$blocklist" in
        *" $cmdname "*) ;;
        *) continue ;;
    esac

    candidate=""
    for tok in $seg; do
        case "$tok" in
            -*) continue ;;
            *.cs) candidate="$tok" ;;
        esac
    done
    [ -n "$candidate" ] || continue

    case "$candidate" in
        /*) abs_file="$candidate" ;;
        *) abs_file="$PWD/$candidate" ;;
    esac
    abs_dir="$(cd "$(dirname "$abs_file")" 2>/dev/null && pwd -P)" || continue
    abs_file="$abs_dir/$(basename "$abs_file")"
    [ -f "$abs_file" ] || continue

    if is_governed_cs_file "$abs_file" "$root"; then
        cat >&2 <<EOF
Blocked Bash command '${cmdname}' reading ${candidate}: it is compiled by ${REL_CSPROJ}, so
search_index/get_symbol answer this more cheaply and completely than raw shell text tools - no
truncation risk, and no irrelevant methods pulled in alongside the one you want.

This is the same rule Read is blocked under (see guard-cs-read.sh) - running the same read through
Bash instead of the Read tool is not a sanctioned way around it.

Do this instead:
  - Don't know the exact symbol name: search_index(query: "term1 term2 ...") - ranked hits with
    symbolId, file, and line, all in one call.
  - Know the type/member name: get_symbol(symbol: "...", include: "members") to enumerate a type, or
    the default include for one member's declaration, xmlDoc, and reference counts.
  - Looking for arbitrary text (a string literal, an API name not declared in this repo) rather than a
    declared symbol: search_index only indexes declared symbols, so a genuine text search has no MCP
    equivalent yet - say so and ask the user to allow the Bash command explicitly.

If this genuinely needs raw shell access (the workspace failed to load, or the file's exact
formatting/byte layout is itself what you need to see), say so and ask the user to allow it explicitly
rather than retrying the same command.
EOF
        exit 2
    fi
done <<< "$segments"

exit 0
