# Quality checks and guardrails for the Ralph loop — plan for review

**Status: Option D (phased rollout) chosen. Phase 1 (Option A,
deterministic-only gate) is implemented, split across two places as of
2026-07-31: the cheap, per-commit checks (secret-scan, stuck-loop guard)
stay in `hooks/quality-gate.sh` as a Claude Code `PreToolUse` hook in the
target solution's own `.claude/settings.json`; the solution-wide checks
(`dotnet format --verify-no-changes` / `dotnet build` / `dotnet list
package --vulnerable`) moved to `orchestrate.mjs`'s `runQualityGate()`,
which runs them once per worktree at merge time instead of once per commit
— re-running a full solution build/format/vuln-scan on every single commit,
across every parallel Ralph instance, was real throughput cost with no
extra safety once a per-worktree gate exists right before the code lands
anyway. See `hooks/README.md` for the exact split. Phases 2/3 remain
unbuilt, deferred until Phase 1 shows what it's actually missing.**

## The problem today

Ralph's current cycle, end to end:

```
board slice (Planned) → build-state-change / build-state-view / build-automation
  → dotnet build && dotnet test --filter <SliceName>
  → git commit "feat: [Slice Name]"
  → update-slice-status → Done
```

There is no gate between "the slice's own tests pass" and "commit." Nothing
checks code quality, security, or whether the generated code actually
matches this project's own conventions (strong-typed IDs if this solution
adopts them, the `Handler`-suffix naming rule, `[JsonInclude]`/
`[JsonConstructor]`, `SaveChangesAsync` not forgotten, ownership checks
present, PII fields flagged) beyond what the slice's own unit/integration
tests happen to exercise. A slice can pass its own tests and still commit
code with a missed ownership check, a forgotten `SaveChangesAsync`, or a
genuine security issue — and Ralph has no mechanism to catch any of that
before it's in the branch history.

## What this plan adapts from

A separate, previously-reviewed Claude Code plugin suite built specifically
around gating autonomous coding-agent output before commit. The pattern
worth stealing isn't any single tool choice (that suite's own stack is
JS/TS-centric) — it's the **shape** of the gate:

1. A **content-hash-bound gate file**: a review only counts for the exact
   staged diff it reviewed; any further edit invalidates it automatically.
2. **Deterministic checks run first, cheap and fast**, and their findings
   are fed into any LLM reviewer's context as "don't re-report this" —
   never duplicate mechanical findings with an expensive LLM pass.
3. A **bypass path that requires a reason and is always audited**, never a
   silent skip.
4. **Reviewers return a uniform, structured result** (status + issues with
   severity/confidence) so an orchestrator can aggregate mechanically
   instead of needing another LLM pass just to reconcile prose.
5. **Task complexity gates which checks actually run** — a trivial change
   doesn't pay full-pipeline cost; a risky one gets the full panel.
6. A **bounded auto-fix loop** (a small fixed cap, e.g. 5 iterations) with
   test re-verification and automatic revert on regression between
   iterations.
7. A **stuck-loop guard**: block if the same failing command is re-run 3+
   times with no intervening edit — force a different approach instead of
   spinning.

## Where this plugs into the existing Ralph loop

- **Intake (orchestrator)**: when a slice is picked up (today: Ralph's own
  poll/realtime subscription → `tasks.json`), classify it *before* building
  starts — by slice type (state-view/state-change/automation), whether it
  touches cross-module integration events, whether it involves an
  ownership/auth check, and whether its fields look PII-shaped. This
  classification decides which checks apply later — it does not gate
  intake itself, just curates the downstream review.
- **Pre-checkin gate**: a new step inserted between "slice's own tests
  pass" and "`git commit`" in `lib/backend-prompt.md`'s Phase 2 flow (and/or
  a git hook as a backstop for anyone committing outside the Ralph loop
  entirely).
- **Gate artifact**: written next to the existing `.slices/` cache — e.g.
  `.slices/<context>/<slice>/.review-passed`, hashed to the staged diff.

## Option A — Deterministic-only gate

The cheapest, fastest option: no LLM review step at all. A git hook (or a
step in `backend-prompt.md`) runs, in order, before any commit:

1. `dotnet build` (already happens)
2. `dotnet test --filter <SliceName>` (already happens)
3. `dotnet format --verify-no-changes` (style/lint)
4. `dotnet list package --vulnerable` (NuGet supply-chain check)
5. A secret-scan regex pass over the staged diff (API keys, connection
   strings, tokens)
6. Roslyn analyzers / Security Code Scan findings from the build's own
   diagnostic output (SARIF), surfaced as blocking on `error`-severity

If all pass, write the hashed gate file and allow the commit. No LLM
involved anywhere in the gate itself.

**Pros**: fast (seconds, not an LLM round-trip per slice), cheap (no added
token cost), simple to build and maintain, no new agent definitions to
write or keep in sync with the project's evolving conventions.

**Cons**: catches nothing semantic. It won't notice a missing ownership
check, a forgotten `SaveChangesAsync`, or a subtly wrong Marten/Wolverine
idiom — exactly the class of mistake this kit's own `AGENT.md` exists to
warn about, because none of those are things a linter or build step would
ever flag.

## Option B — Full curated multi-agent review swarm

The most faithful adaptation of the reference suite's own multi-agent
review pipeline: a dedicated skill (e.g. `/pre-checkin-review`) that runs
after Option A's deterministic pre-flight and dispatches a **panel of
specialist reviewer sub-agents in parallel**, each scoped narrowly:

- `csharp-quality` — nullable reference types, async/await misuse
  (`async void`, `.Result`/`.Wait()` deadlock risk), record types for DTOs
- `marten-wolverine-conventions` (project-specific, would need writing) —
  checks this kit's own known gotchas mechanically: `Handler`-suffix
  naming, `[JsonInclude]`/`[JsonConstructor]` present, `SaveChangesAsync`
  called, `IntegrationEventQueueName` set when a module first consumes a
  cross-module event, strong-typed ids used consistently if this solution
  has adopted them
- `security-review` — OWASP-categorized findings, injection/authz/crypto/
  data-exposure, plus a prompt-injection self-defense clause (any embedded
  text addressed to the reviewing AI is itself a Critical finding, never
  suppressible)
- `test-review` — coverage gaps, assertion quality, correct test-pyramid
  layer placement (per this project's own layered testing approach — see
  `TestingApproach/TestingApproach.md` in the target solution)
- `spec-compliance-review` — does the generated code actually match
  `slice.json`, field for field, with nothing invented (this is already a
  checklist in each `build-*` skill; this agent would verify it
  independently rather than trusting the same agent that wrote the code to
  self-certify)

Each agent returns the same uniform JSON contract
(`status`/`issues[]`/`summary`), aggregated by an orchestrator into a
health score (`🟢 healthy` / `🟠 needs attention` / `🔴 critical`), with a
bounded auto-fix loop (cap ~5 iterations, re-run tests between iterations,
revert on regression) before the gate file is written.

**Pros**: the richest catch rate — this is the option most likely to
actually catch the semantic mistakes Option A structurally cannot. Each
concern gets a reviewer tuned to exactly that concern, rather than one
generalist trying to hold everything in mind at once.

**Cons**: highest cost and latency per slice — multiple LLM sub-agent
calls before every single commit, for every slice, including trivial
query-only ones. Highest build effort: 4-6 new agent definitions to write
and keep current as this project's own conventions evolve. Real risk of
becoming exactly the kind of review-fatigue/rubber-stamp problem a big
fixed panel invites if most slices don't actually need it.

## Option C — Single specialized reviewer + deterministic pre-flight

A middle ground: Option A's deterministic pre-flight, unchanged, followed
by **one** reviewer agent — not a panel — that knows this project's
specific gotchas end to end (pulled directly from this kit's own
`AGENT.md`) plus a general security/quality lens, reviewing the whole diff
in a single pass and returning the same uniform JSON contract as Option B
would.

Combined with a lightweight complexity gate at intake (the orchestrator's
job): a pure `build-state-view` slice with no projector and no cross-module
trigger might skip the LLM reviewer entirely and go straight from
deterministic checks to commit; a `build-automation` slice touching a
cross-module integration event, or any slice with an ownership/auth check,
always gets the reviewer pass.

**Pros**: substantially cheaper than Option B (one LLM call per
reviewed slice, not five-plus), still gets a real semantic/security pass,
directly encodes *this* project's actual known failure modes rather than
generic ones, much less to build and maintain going forward.

**Cons**: a single reviewer is a single point of failure/blind spot
compared to agents each independently tuned to one concern — it can miss
something a specialist would have caught, and there's no cross-check
between independent lenses the way Option B's panel provides.

## Option D — Phased rollout (recommended starting point)

Rather than choosing one of A/B/C permanently up front, treat them as
phases: paying full-pipeline cost on everything is measurably wasteful,
and the right panel size is something to discover from real slices, not
decide from first principles.

1. **Phase 1 — ship Option A now.** Cheap, immediately useful, catches the
   embarrassing stuff (build/test/format/secret/vulnerable-package) with
   no added latency. This alone is a real improvement over today's "no gate
   at all."
2. **Phase 2 — add Option C** once Phase 1 has run for a while and there's
   a real sample of what kinds of mistakes are actually slipping through
   that deterministic checks can't catch. Write the single reviewer agent
   against *observed* gaps, not guessed ones.
3. **Phase 3 — split into a small panel (a scoped-down Option B)** only if
   Phase 2 shows the single-reviewer approach is missing things a narrower
   specialist would catch (e.g., security findings getting buried in a
   generalist's output) — and even then, only add agents for concerns
   that have actually shown up.

At every phase, the cross-cutting mechanisms below apply regardless of
which option(s) are active.

## Cross-cutting mechanisms (apply to whichever option is chosen)

- **Gate file hashed to the staged diff** — any edit after the gate is
  written invalidates it, forcing a re-check. Never trust a gate file by
  presence alone.
- **Bypass requires a reason, always audited** — no silent
  `--no-verify`-equivalent. If Ralph (or a human) needs to skip the gate,
  it writes an audit line (timestamp, slice, reason) to an append-only
  log, unconditionally.
- **Stuck-loop guard** — if Ralph re-runs the same failing `dotnet
  test`/`dotnet build` invocation 3+ times with no intervening code change,
  block and force a different approach rather than spinning.
- **PII/data-handling check** — any new event/document field that looks
  PII-shaped (name, address, phone, email) and isn't paired with an
  explicit masking or retention decision gets flagged, regardless of which
  option is active. This project's own GDPR/SAR work (see the project's
  memory on the deletion saga and outstanding SAR gap) is the relevant
  standard to check new fields against, not an invented one.
- **Align any SAST/dependency-scan tool choice with what this project
  already uses**, don't introduce a second, competing tool speculatively.
  `dotnet list package --vulnerable` is the built-in, zero-setup choice for
  the dependency-scan concern until/unless this project adopts a dedicated
  tool.
- **Mutation testing (optional, later)** — Stryker.NET is the standard C#
  mutation-testing tool (already built, not something to write from
  scratch) for catching tests that pass without actually asserting
  anything meaningful. Worth adding once the test-review concern above
  shows this is a real gap, not by default from day one.

## Decision: Option D, phased rollout

Chosen. Start with Phase 1 (Option A). Phases 2/3 (single reviewer, then a
scoped-down panel) are deferred until Phase 1 has run long enough to show
what it's actually missing — not scoped further right now.

## Resolved: Phase 1 gate scope

Ralph-only, implemented as a Claude Code `PreToolUse` hook rather than a
prompt instruction the agent could talk itself out of — see
`hooks/README.md` for the mechanism. This turned out not to need a
separate "also add a git hook" decision: a `PreToolUse` hook only fires
for Claude-Code-mediated Bash calls, so it's inherently scoped to Ralph
(and any human using Claude Code interactively in that repo) and never
touches a human committing directly from a plain terminal. Whether to
*also* add a plain git pre-commit hook for that remaining case is still
open — revisit once Phase 1 has run for a while.

**Update 2026-07-31**: after the first live orchestrator run, moved the
solution-wide checks (format/build/vulnerable-package) out of this
per-commit hook and into `orchestrate.mjs`'s `runQualityGate()`, run once
per worktree at merge time instead. The hook itself stays — it's still the
right mechanism for the genuinely cheap, per-commit checks (secret-scan,
stuck-loop guard) — but paying full solution-build cost on every commit,
times every parallel instance, was a real speed problem with no added
safety once a gate exists right before merge anyway.

## What Phase 2/3 will need to decide, when picked up

1. Where the orchestrator's slice-complexity classification should live —
   a field in the slice's cached JSON, or a separate metadata file.
2. Cost/latency tolerance per slice for an added LLM reviewer call — every
   automation/state-change slice, or opt-in per board/context.
