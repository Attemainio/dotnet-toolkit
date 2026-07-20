#!/usr/bin/env bash
# PostToolUse hint: a Write that creates a brand-new .cs file leaves the syntax index and MSBuild
# workspace unaware of it. Both are mtime-polling, not filesystem watchers (deliberate — WSL /mnt
# drives don't fire inotify), and a new file is invisible even to a poll until a FULL sweep runs
# (debounced to every 30s in Index/ProjectIndex.cs), which then backgrounds a full MSBuildWorkspace
# reload before the file becomes a real Roslyn Document. A validate_patch call against the new file
# before that finishes fails deterministically with invalid_edit: "file is not part of the loaded
# solution" — there is no on-demand fallback that opens an arbitrary path from disk.
#
# This hook cannot fix that itself (a hook has no MCP session to call reload_workspace through — it
# is a separate process with no access to the stdio pipe the server and Claude Code talk over). What
# it can do is remind Claude to call reload_workspace before the next validate_patch/get_symbol on
# the new file, right when the file is created rather than after a confusing invalid_edit failure.
#
# The Write PreToolUse guard (guard-cs-edit.sh) only lets Write through for .cs files that do not
# already exist — an existing file's Write is blocked there, forced through validate_patch instead —
# so any Write this hook sees succeed on a .cs path is, by construction, a new file.
#
# Fails open (exit 0, no output) on any uncertainty: this is a reminder, not an enforcement boundary,
# and a missing interpreter or unparseable payload must not surface a confusing error mid-turn. The
# JSON reply is built by the same interpreter that parsed the input, not hand-interpolated into a
# heredoc — a file_path is caller-controlled text (a Windows path's backslashes, a quote in a
# directory name) and does not belong anywhere near manual JSON string escaping.

set -uo pipefail

payload=$(cat)

MESSAGE_SUFFIX=" is a new .cs file. The syntax index and MSBuild workspace do not know about it yet (mtime-polling, not a filesystem watcher) - call reload_workspace(scope: \"all\") and wait for workspace_status to report loaded before validate_patch or get_symbol on this file, or the call will fail with invalid_edit: file is not part of the loaded solution."

if command -v node >/dev/null 2>&1; then
    printf '%s' "$payload" | MESSAGE_SUFFIX="$MESSAGE_SUFFIX" node -e '
        let s = "";
        process.stdin.on("data", d => s += d);
        process.stdin.on("end", () => {
            try {
                const j = JSON.parse(s);
                const file = j.tool_input?.file_path ?? "";
                if (j.tool_name !== "Write" || !file.endsWith(".cs")) process.exit(0);
                console.log(JSON.stringify({
                    hookSpecificOutput: {
                        hookEventName: "PostToolUse",
                        additionalContext: file + process.env.MESSAGE_SUFFIX,
                    },
                }));
            } catch { process.exit(0); }
        });
    ' 2>/dev/null
    exit 0
fi

if command -v python3 >/dev/null 2>&1; then
    printf '%s' "$payload" | MESSAGE_SUFFIX="$MESSAGE_SUFFIX" python3 -c '
import json, os, sys
try:
    j = json.load(sys.stdin)
except Exception:
    sys.exit(0)
file = (j.get("tool_input") or {}).get("file_path") or ""
if j.get("tool_name") != "Write" or not file.endswith(".cs"):
    sys.exit(0)
context = file + os.environ["MESSAGE_SUFFIX"]
print(json.dumps({"hookSpecificOutput": {"hookEventName": "PostToolUse", "additionalContext": context}}))
' 2>/dev/null
    exit 0
fi

if command -v jq >/dev/null 2>&1; then
    printf '%s' "$payload" | jq -r --arg suffix "$MESSAGE_SUFFIX" '
        if .tool_name == "Write" and (.tool_input.file_path // "" | endswith(".cs")) then
            {hookSpecificOutput: {hookEventName: "PostToolUse",
                additionalContext: (.tool_input.file_path + $suffix)}}
        else empty end' 2>/dev/null
    exit 0
fi

exit 0
