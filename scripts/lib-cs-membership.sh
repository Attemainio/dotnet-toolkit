#!/usr/bin/env bash
# Shared solution-membership check, used by both guard-cs-read.sh (the Read tool) and
# guard-cs-bash-read.sh (Bash commands that read a .cs file's bytes the same way Read would). Kept in
# one file so the two guards can never drift apart on what counts as "this repo's own compiled .cs" --
# they are the same question asked from two different tool surfaces.
#
# A hook is a separate, short-lived process with no access to the MCP stdio pipe, so it cannot ask the
# running server's WorkspaceHost "is this file part of the loaded solution" -- that question only has an
# answer inside the running server. What this CAN check statically: whether a .csproj governs the file's
# directory at all, whether that project's own <Compile Remove> globs exclude it anyway, and whether a
# nested *.sln/*.slnx between the file and the repo root means it belongs to its own independent solution
# (a test fixture's throwaway sample project) rather than the one this repo's own server loads.
#
# is_governed_cs_file ABS_FILE ROOT
#   Sets REL_CSPROJ on success (relative to ROOT). Returns 0 if the file is governed by a project this
#   repo's own solution would load (block-worthy), 1 otherwise (allow).
#
# The walk below assumes ABS_FILE starts somewhere under ROOT and climbs toward it. A file that is not
# under ROOT at all (a different repo, a scratch directory, anything genuinely external) must never
# reach that walk: without this check it climbs past ROOT to wherever the filesystem's own .sln/.csproj
# happens to sit and can misreport "governed by this repo", incorrectly blocking a read this guard has
# no business touching. Scope is always "this project" -- outside ROOT is unconditionally external.
is_governed_cs_file() {
    local abs_file="$1" root="$2"
    case "$abs_file" in
        "$root"/*) ;;
        *) return 1 ;;
    esac
    local dir csproj match sln
    dir="$(dirname "$abs_file")"
    csproj=""
    while :; do
        if [ -z "$csproj" ]; then
            match=$(find "$dir" -maxdepth 1 -name '*.csproj' 2>/dev/null | head -n1)
            [ -n "$match" ] && csproj="$match"
        fi
        if [ "$dir" != "$root" ]; then
            sln=$(find "$dir" -maxdepth 1 \( -name '*.slnx' -o -name '*.sln' \) 2>/dev/null | head -n1)
            [ -n "$sln" ] && return 1   # nested/independent solution root
        fi
        [ "$dir" = "$root" ] && break
        local parent
        parent="$(dirname "$dir")"
        [ "$parent" = "$dir" ] && break   # filesystem root, defensive
        dir="$parent"
    done

    [ -n "$csproj" ] || return 1

    local proj_dir rel excluded glob
    proj_dir="$(dirname "$csproj")"
    rel="${abs_file#"$proj_dir"/}"
    excluded=0
    while IFS= read -r glob; do
        [ -n "$glob" ] || continue
        case "$rel" in
            $glob) excluded=1; break ;;
        esac
    done < <(grep -o 'Compile[[:space:]]\+Remove="[^"]*"' "$csproj" 2>/dev/null \
        | sed -E 's/.*Remove="([^"]*)"/\1/' | tr ';' '\n')

    [ "$excluded" = "1" ] && return 1

    REL_CSPROJ="${csproj#"$root"/}"
    return 0
}
