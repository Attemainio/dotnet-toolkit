# Internal document drift

Read by `SKILL.md` Step 7. §A–§G of `harness-compliance.md` ask whether the files are *right*;
Steps 2–6 ask whether they *match the code*. This file asks a different question: **what changed
since the last time anyone checked, and which of the files that describe it were not updated with
it?**

Drift is not found by reading files — every one of them reads plausibly on its own. It is found by
starting from the change and asking what should have moved with it.

## Establish the baseline first

Everything below is relative to a baseline. Take it, in order of preference, from:

1. A commit range the user stated.
2. The last commit that touched `skills/dotnet-toolkit-consistency/` — the previous audit.
3. `main~N` for a stated N, or the last release tag.

**Say which baseline you used in the report.** A drift finding without a baseline is unfalsifiable,
and the next run cannot tell what it still needs to cover.

## Evidence, cheapest and most semantic first

### 1. `get_semantic_diff` — what symbols changed

```
get_semantic_diff(<baseline>)
```

Symbol-level, not line-level: added, removed, and signature-changed members. A moved method with an
unchanged signature is correctly silent here, and that is the point — it costs nothing to describe
and needs no doc update.

For each **added, removed, or signature-changed** symbol, ask: is it a tool method, a hook message, a
contract type? Then go to Step 5's file table in `SKILL.md` and name every row that should have
moved with it.

### 2. `search_log` — the recorded intent

```
search_log(<terms from step 1>)
```

**The development log is the only source that records *why*.** `validate_patch` is the sole writer,
so every C# change made through the sanctioned path left an `intent` here — and nothing else in the
repo has it. Git shows what changed; the log shows what it was *for*, which is what the describing
files are supposed to say.

Three things to look for:

- An intent describing a **behaviour** change whose `docs/tools/<tool>.md` still describes the old
  behaviour.
- An intent mentioning a file, flag, or argument name that no longer exists.
- A symbol changed in step 1 with **no log entry at all** — it was edited outside `validate_patch`.
  That is itself a finding: the reasoning is gone, and the next session will re-derive or contradict
  it. Report it; do not try to reconstruct the intent.

### 3. `git log --stat` / `git diff --name-status` — everything the log cannot see

The development log covers `.cs` changes made through `validate_patch`. It covers **none** of the
files this skill mostly audits. So sweep the non-`.cs` tree directly:

```bash
git diff --name-status <baseline>..HEAD -- \
    docs/ skills/ agents/ hooks/ standards/ .claude/rules/ scripts/ \
    README.md CLAUDE.md .mcp.json
```

For every added file: **does anything reference it?** An orphan is the terminal form of scatter
(`harness-compliance.md` §D). For every modified file: does its counterpart in §A's owner/pointer
table still agree?

### 4. `git status` — uncommitted work

Cheap, and catches the case where the audit is being run *during* a change rather than after one.
Report uncommitted files separately; they are in flight, not drifted.

## Drift-sensitivity ranking

Work top-down. The ranking sets **order, not scope** — a partial run should have covered what matters
most, and a full run covers everything regardless of tier.

### Tier 1 — always loaded, or read at a blocking moment

Wrong text here is acted on immediately, by every session, with no opportunity to notice.

- `.claude/rules/index.md` — always loaded here *and* copied into every consuming repo. Drift ships.
- `hooks/hooks.json` and the messages in `src/DotnetToolkit.McpServer/Hooks/*` — read at the exact
  moment a caller is denied. A stale one teaches the wrong fix at the worst moment.
- `Tools/*.cs` `[Description]` attributes — the searchable index (`harness-compliance.md` §C). A
  drifted description makes a tool unfindable, which reads as the tool not existing.
- `docs/tools/<tool>.md` **filenames** — the reachability contract, since `dotnet-read`/`dotnet-write`
  derive the path rather than tabulating it. A manual named anything else is unreachable no matter how
  good it is.
- Skill and agent `description:` frontmatter — always resident and the entire basis for selection. A
  description that stops matching how a user phrases the task silently strands the whole skill body
  behind it, which is exactly the failure `.claude/rules/index.md` now depends on not happening.

### Tier 2 — read on demand, on a path that matters

Wrong text here is acted on by whoever took that path, which is usually someone about to write code.

- `skills/dotnet-read/SKILL.md` — read before every first `.cs` read, and it is now the only
  always-reachable statement of which tool answers what.
- `skills/dotnet-write/SKILL.md` — read before every first `.cs` edit.
- `standards/index.md` — the shared load rule for both the writer and the reviewer.
- `agents/*.md` `tools:` frontmatter — a stale grant silently removes a capability the body still
  instructs the agent to use.
- `standards/` cross-references and the MCP tools they name.
- `skills/dotnet-toolkit-init/SKILL.md` and `docs/install/*` — drift here ships a broken install.

### Tier 3 — read once, by a human

- `docs/design/*`
- `README.md`
- `CLAUDE.md`

Real, but the reader is a person who can notice a contradiction. Ranked last for *order*, not
excluded.

## Act on all relevant files, not just the ranked ones

The ranking is a floor. If step 1–4 evidence points at a file no tier names, act on it **and** report
that the ranking missed it — a gap in this list is a finding about this list, and the fix is a new
row here, not a one-off correction.

`get_scope`, `get_call_slice` and `get_semantic_diff` are the standing example: three tools that
shipped in the code and were named in no doc at all. Nothing in a per-file check would ever have
found them, because the files that should have mentioned them were internally consistent. Only
starting from the change finds that class of drift.
