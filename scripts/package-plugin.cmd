@echo off
setlocal enabledelayedexpansion
rem Builds a clean plugin distribution directory to use as a local marketplace source.
rem
rem WHY THIS EXISTS: a host's plugin installer snapshots the marketplace root verbatim. Pointed at
rem this working tree it copies the whole repository -- src\, tests\, .git\ and build output -- and
rem then fails outright, because Visual Studio holds .vs\<solution>\FileContentIndex\*.vsidx open
rem with a deny-read lock and the copy aborts with a permission error. No packaging step should ever
rem snapshot a live development tree.
rem
rem A fresh `git clone` has none of those directories, so installing from a remote marketplace is
rem unaffected. This script is what makes a LOCAL install from a working repo behave the same way.

set "root=%~dp0.."
pushd "%root%" || exit /b 1
set "root=%CD%"
popd

if "%~1"=="" (set "out=%root%\package") else (set "out=%~1")

if not exist "%root%\dist\DotnetToolkit.McpServer.dll" (
    echo package-plugin: dist\ has no published server - run scripts\build-plugin.cmd first 1>&2
    exit /b 1
)

rem Everything a consuming session actually reaches for. Deliberately an allow-list: an exclude-list
rem silently ships whatever a later commit adds at the repository root.
set "dirs=.claude-plugin .codex-plugin skills agents hooks standards docs dist"
set "files=AGENTS.md README.md LICENSE.txt"

if exist "%out%" rmdir /s /q "%out%"
mkdir "%out%" || exit /b 1

for %%D in (%dirs%) do (
    if not exist "%root%\%%D" (
        echo package-plugin: missing %%D 1>&2
        exit /b 1
    )
    robocopy "%root%\%%D" "%out%\%%D" /e /njh /njs /ndl /nfl /np >nul
    if errorlevel 8 (
        echo package-plugin: failed to copy %%D 1>&2
        exit /b 1
    )
)

for %%F in (%files%) do (
    if not exist "%root%\%%F" (
        echo package-plugin: missing %%F 1>&2
        exit /b 1
    )
    copy /y "%root%\%%F" "%out%\%%F" >nul || exit /b 1
)

rem The always-loaded rule only -- not the rest of .claude\, which is this repo's own session state.
robocopy "%root%\.claude\rules" "%out%\.claude\rules" /e /njh /njs /ndl /nfl /np >nul
if errorlevel 8 (
    echo package-plugin: failed to copy .claude\rules 1>&2
    exit /b 1
)

rem A packaging step that quietly ships a development tree is the bug this script exists to prevent,
rem so verify rather than trust the allow-list above.
for %%X in (.git .vs src tests obj bin scripts) do (
    if exist "%out%\%%X" (
        echo package-plugin: %%X leaked into the package 1>&2
        exit /b 1
    )
)

echo Plugin packaged to %out%
echo Use it as the marketplace source:
echo   codex plugin marketplace add %out%
echo   claude --plugin-dir %out%
endlocal
