#!/usr/bin/env bash
# PreToolUse guard: keep C# edits on the validate_patch write path.
#
# CLAUDE.md has told Claude to use validate_patch for .cs files since this plugin existed, and it is
# still the rule that gets broken most often — CLAUDE.md is context, not enforcement, and adherence
# decays over a long session. This hook is the enforcement half. The compile check is the cheap part
# of what validate_patch does; the development-log entry is the part that cannot be recovered later,
# so an Edit that slips through is a permanent hole in the log, not a slower path to the same place.
#
# Denial is exit-2-plus-stderr rather than a permissionDecision JSON object. Both are supported, but
# this way the script never has to emit JSON, so it needs a JSON *reader* and not also a correct
# JSON *writer* — no escaping bugs in the one path that has to be reliable.
#
# Fails OPEN by design. This is a workflow guard, not a security boundary: a missing interpreter or
# an unparseable payload must not wedge the user's editing, so every uncertain path exits 0 (allow).

set -uo pipefail

payload=$(cat)

# Read .tool_name and .tool_input.file_path as two lines. No single JSON tool is guaranteed to exist
# on a consumer machine — jq is frequently absent (it is absent on this plugin's own dev box), and
# Claude Code's native installer means node cannot be assumed either — so try each in turn and give
# up quietly if none is present.
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

[ -n "$file" ] || exit 0

# Only C# source. Everything else this plugin has no opinion about — csproj, json, md, sh are
# explicitly the plain-tool path, and .csx/.cshtml/.razor are not what validate_patch operates on.
case "$file" in
    *.cs) ;;
    *) exit 0 ;;
esac

# Creating a new file is not a validate_patch case: it requires baseVersions keyed by a symbolId,
# and a symbol that does not exist yet has no version to lease against. Write the file, then make
# subsequent changes to it through the tool.
if [ "$tool" = "Write" ] && [ ! -e "$file" ]; then
    exit 0
fi

cat >&2 <<EOF
Blocked ${tool} on ${file}: C# edits go through validate_patch, not ${tool}.

validate_patch is the write path for .cs files, not a faster dotnet build. It is also the ONLY thing
that appends to the development log — an edit made with ${tool} is a change whose reasoning is gone
the moment this conversation ends, and search_log can never recover it.

Do this instead:
  1. get_symbol on the target symbol; keep its contentVersion and the declarationSites line span.
  2. validate_patch with baseVersions {symbolId: contentVersion} and line-span edits, applyOnSuccess
     false, to see the ladder verdict without touching disk.
  3. Re-send with applyOnSuccess true and an intent in user terms once it reports isSufficient true.

A change that feels too large or too interleaved to decompose is still not a reason to fall back to
${tool} — split it into more validate_patch calls, one per touched symbol, sharing one intent.

If this genuinely is not a validate_patch case (the workspace failed to load, or you are reverting a
partial write), say so and ask the user to allow it explicitly rather than retrying the same call.
EOF
exit 2
