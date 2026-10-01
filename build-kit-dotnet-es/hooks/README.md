# Quality gate hook (Phase 1)

Implements Phase 1 (Option A, deterministic-only) of
[`../quality-checks.md`](../quality-checks.md): a Claude Code `PreToolUse`
hook on the `Bash` matcher that gates `git commit` before it happens, plus
a stuck-loop guard on repeated `dotnet build`/`dotnet test` calls.

**2026-07-31 update**: the solution-wide checks (`dotnet format
--verify-no-changes`, `dotnet build`, `dotnet list package --vulnerable`)
no longer live here — they moved to `../orchestrate.mjs`'s
`runQualityGate()`, which runs them **once per worktree, at merge time**,
instead of once per `git commit` inside every Ralph instance's Claude Code
session. Re-running a full solution build/format/vuln-scan on every commit
attempt, times every parallel instance, was measured throughput cost with
no extra safety once a per-worktree gate exists right before the code
lands on the target branch anyway. This hook now only runs the checks that
are legitimately cheap no matter how often they fire: the secret-scan
(a grep over the staged diff) and the stuck-loop guard (a hash compare).
See `../orchestrate.mjs`'s `mergeWorktrees()` for the moved checks — on
failure it leaves that worktree/branch unmerged and in place (same
convention as a merge conflict), rather than losing or silently dropping
the work.

## Why this lives here, but installs elsewhere

Ralph invokes `claude` with `cwd` set to the **target .NET solution's own
directory** (see `../ralph-claude.js`), not this kit's directory. Claude
Code only discovers hook configuration (`.claude/settings.json`) by
walking up from `cwd` — so the hook registration has to live in the target
solution's own `.claude/` folder, even though the hook *script* itself
stays here in the kit (so it's still version-controlled and updated
alongside the skills it complements, not duplicated per project).

This has a useful side effect: because the hook only fires for
Claude-Code-mediated Bash calls, it is **inherently Ralph-only** — a human
running `git commit` directly in a terminal never goes through Claude
Code's tool-call pipeline, so this hook never sees or blocks that commit.
No extra scoping was needed to achieve "Ralph-only"; it falls out of how
Claude Code hooks work.

## Installing into a new target solution

1. Copy `quality-gate.sh` in place (it already lives in this kit — nothing
   to copy if the kit is already checked out as a sibling of the .NET
   solution).
2. Create `<solution-root>/.claude/settings.json`:
   ```json
   {
     "hooks": {
       "PreToolUse": [
         {
           "matcher": "Bash",
           "hooks": [
             {
               "type": "command",
               "command": "bash <relative-path-to-this-kit>/hooks/quality-gate.sh"
             }
           ]
         }
       ]
     }
   }
   ```
   Adjust the relative path to wherever this kit actually sits relative to
   the solution root. In this repo, that's `../../../build-kit-dotnet-es/hooks/quality-gate.sh`
   from `code/K9Crush-scaffold/K9Crush/.claude/settings.json`.
3. Add to the solution's own `.gitignore`:
   ```
   .claude/state/
   ```
   **Do not** ignore `.claude/metrics/` — `gate-bypass-audit.jsonl` and
   `gate-history.jsonl` are audit trails, meant to be committed and
   readable in git history, not ephemeral.
4. Make sure `dotnet format`, `dotnet build`, and `dotnet list package
   --vulnerable` all work from the solution root before relying on the
   gate — the hook assumes these succeed as commands, not that their
   *output* is clean (a failing `dotnet format` invocation itself, as
   opposed to it reporting unformatted files, would currently read as a
   pass — this is a known Phase 1 gap, not a design intent; harden this if
   it turns out to matter in practice).

## What it checks (every `git commit` attempt)

1. **Secret scan** over the staged diff — a regex match on
   `api_key`/`secret`/`password`/`token` followed by a quoted value ≥8
   chars. Blocks on match.

If it passes, an audit line is appended to `.claude/metrics/gate-history.jsonl`
(timestamp + a hash of the staged diff) and the commit proceeds. This is
an audit trail, not a skip-cache — the scan re-runs on every commit
attempt; nothing is cached to avoid re-running it, since it's cheap.
Caching becomes relevant once Phase 2/3 add an LLM reviewer pass (see
`../quality-checks.md`), which is not free to re-run speculatively.

## What moved to `orchestrate.mjs` (once per worktree, at merge time)

1. **`dotnet format --verify-no-changes`** — blocks the merge if any file
   isn't already formatted.
2. **`dotnet build`** — blocks the merge on build failure. (Ralph's own
   prompt already runs this before attempting to commit each slice — this
   is a second, independent check at merge time, not a trust of what the
   agent already claimed.)
3. **`dotnet list package --vulnerable`** — blocks the merge if any
   referenced NuGet package has a known vulnerability.

See `runQualityGate()` / `mergeWorktrees()` in `../orchestrate.mjs`.

## The bypass path

`git commit --no-verify` (or `-n`) is not blocked outright, but requires a
non-empty `GATE_BYPASS_REASON` environment variable:

```
GATE_BYPASS_REASON="hotfix, gate to follow" git commit --no-verify -m "..."
```

Every bypass — whether the reason was accepted or the attempt was blocked
for lacking one — is either logged (accepted) or refused (missing), never
silently allowed through unlogged. The audit line goes to
`.claude/metrics/gate-bypass-audit.jsonl`: timestamp, branch, reason,
staged file count.

## The stuck-loop guard

Separately from the commit gate, the same hook also watches for
`dotnet build`/`dotnet test` being re-run with the **exact same command
string** and **no change to the working tree** (excluding `.claude/`
itself) three times in a row. On the third identical attempt, it blocks
with a message telling the agent to stop and diagnose rather than keep
retrying — a real, specific failure mode for an autonomous loop, not a
hypothetical one. The counter resets on any real code change, or once
triggered.

## Known Phase 1 limitations (by design, not oversight)

- No LLM-driven review of any kind — see `../quality-checks.md`'s Option
  C/B for what that adds and when to consider it.
- No hash-bound "review passed" gate file that's checked *before*
  re-running expensive work — not needed yet, since every Phase 1 check is
  cheap enough to just re-run on every attempt.
- The secret-scan regex is intentionally simple and will have both false
  positives and false negatives — it is not a replacement for a real
  secret-scanning tool (e.g. gitleaks), just a cheap first line of defense
  consistent with Phase 1's "deterministic and cheap" scope.
- SAST/dependency-scan tooling deliberately does **not** introduce Semgrep
  or any other third-party static-analysis tool by default — `dotnet list
  package --vulnerable` (already in the gate above) covers the
  supply-chain concern with zero added tooling. Add a real SAST tool later
  if this project settles on one; nothing here assumes a specific choice.
