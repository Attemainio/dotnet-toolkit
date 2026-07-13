---
name: dotnet-devlog
description: Use at the start of a coding task to check prior work and decisions, and at the end of any completed, partial, or abandoned change to record it. The development log stores WHAT/WHY/OBSERVATIONS/FIX entries with affected classes and ensembles, searchable via MCP tools.
---

# Development log discipline

This repo keeps a structured development log in weekly markdown files
(`docs/devlog/<year>-W<week>.md`) managed by the dotnet-toolkit MCP server.

**Hard rule: never Read, Grep, or edit `docs/devlog/*.md` directly.** Always go
through the MCP tools — search returns only the relevant entries, which is the point.

## Before starting work on an area

Search for prior attempts and decisions so you don't repeat rejected approaches:

- `mcp__plugin_dotnet-toolkit_dotnet__devlog_search` with a free-text `query`
  (e.g. "evolutionary solver mutation rate") and/or filters: `affected_class`,
  `ensemble`, `tag`, `status`, `from`/`to` dates.
- Results are compact hit rows (id, date, title, status, classes, ensemble, score).
  Fetch the one or two entries that matter with
  `mcp__plugin_dotnet-toolkit_dotnet__devlog_get` — do not fetch every hit.
- An empty `query` lists the most recent entries (useful for "what happened lately").

## After finishing (or abandoning) a change

Record it with `mcp__plugin_dotnet-toolkit_dotnet__devlog_add`:

- `title` — short and specific ("Fixed decimal rounding in price calculation").
- `what` — what was done or attempted.
- `why` — why it was done, or why it was NOT completed.
- `observations` — what was tried, what worked, what didn't, and why alternatives
  were rejected. This is the field future sessions search for — include rejected
  approaches and their reasons.
- `fix` — the concrete change and how it was validated (tests, build, manual check).
- `status` — `done`, `not-done`, or `partial`. Record abandoned work too: a
  `not-done` entry explaining why saves the next attempt from the same dead end.
- `affected_classes` — class names touched (used for targeted search later).
- `ensemble` — the module/folder/subsystem name.
- `tags` — free-form keywords.

One entry per logical change; don't batch unrelated changes into one entry.
