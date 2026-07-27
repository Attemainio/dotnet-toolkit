#!/usr/bin/env bash
# PostToolUse hook: a Write that creates a brand-new .cs file leaves the syntax index and MSBuild
# workspace unaware of it until a sweep runs. Previously this hook only reminded Claude to call
# reload_workspace itself - a reminder that depended on being followed, and wasn't reliably. Now it
# talks directly to the running server's loopback control channel (Control/ControlServer.cs), a TCP
# listener on 127.0.0.1 whose port is published at CacheDir/control.port, and triggers the rescan/
# reload itself. A hook is a separate OS process with no access to the MCP stdio pipe Claude Code and
# the server talk over - the control channel exists specifically to give it another way in.
#
# "rescan" is synchronous and cheap (syntax index only, no MSBuild) - this hook waits for it, so the
# syntax index already knows about the new file by the time the hook returns. "reload" only starts the
# MSBuildWorkspace reload in the background and returns immediately, since that can run far longer than
# this hook's timeout; the injected message tells Claude to check workspace_status before
# validate_patch/get_symbol rather than assuming the reload already finished.
#
# Falls back to the old reminder-only text if the control channel is unreachable for any reason (a
# server built before this feature existed, a missing port file, connection refused, a timeout) - fails
# open, same as every other hook here.

set -uo pipefail

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

[ "$tool" = "Write" ] || exit 0
case "$file" in
    *.cs) ;;
    *) exit 0 ;;
esac

root="${DOTNET_TOOLKIT_PROJECT_DIR:-${CLAUDE_PROJECT_DIR:-$PWD}}"
root="$(cd "$root" 2>/dev/null && pwd -P)" || exit 0
port_file="$root/.claude/dotnet-toolkit/cache/control.port"

# $1: "rescan" | "reload". Prints the server's one-line response, or nothing on any failure - a missing
# port file, an old server with no control channel, a refused connection, or no response within the
# timeout all look the same here and all mean "fall back to the reminder text".
control_request() {
    [ -f "$port_file" ] || return 1
    local port response=""
    port=$(cat "$port_file" 2>/dev/null) || return 1
    [ -n "$port" ] || return 1
    if exec 3<>"/dev/tcp/127.0.0.1/$port" 2>/dev/null; then
        printf '%s\n' "$1" >&3
        read -r -t 8 response <&3
        exec 3<&- 3>&-
    else
        return 1
    fi
    printf '%s' "$response"
}

rescan_result=$(control_request rescan)
reload_result=""
if [ -n "$rescan_result" ]; then
    reload_result=$(control_request reload)
fi

if [ -n "$rescan_result" ] && [ -n "$reload_result" ]; then
    MESSAGE_SUFFIX=" is a new .cs file. Control channel: ${rescan_result}; ${reload_result} - call workspace_status before validate_patch/get_symbol on this file to confirm the workspace reload has finished."
else
    MESSAGE_SUFFIX=" is a new .cs file. The syntax index and MSBuild workspace do not know about it yet (mtime-polling, not a filesystem watcher) - call reload_workspace(scope: \"all\") and wait for workspace_status to report loaded before validate_patch or get_symbol on this file, or the call will fail with invalid_edit: file is not part of the loaded solution."
fi
export MESSAGE_SUFFIX

if command -v node >/dev/null 2>&1; then
    node -e '
        const file = process.argv[1];
        console.log(JSON.stringify({
            hookSpecificOutput: {
                hookEventName: "PostToolUse",
                additionalContext: file + process.env.MESSAGE_SUFFIX,
            },
        }));
    ' "$file" 2>/dev/null
    exit 0
fi

if command -v python3 >/dev/null 2>&1; then
    python3 -c '
import json, os, sys
file = sys.argv[1]
context = file + os.environ["MESSAGE_SUFFIX"]
print(json.dumps({"hookSpecificOutput": {"hookEventName": "PostToolUse", "additionalContext": context}}))
' "$file" 2>/dev/null
    exit 0
fi

if command -v jq >/dev/null 2>&1; then
    jq -n -r --arg suffix "$MESSAGE_SUFFIX" --arg file "$file" \
        '{hookSpecificOutput: {hookEventName: "PostToolUse", additionalContext: ($file + $suffix)}}' 2>/dev/null
    exit 0
fi

exit 0
