# Ralph Agent Instructions

You are an autonomous coding agent working on a Wolverine.Http + Marten
(event-sourced) + RabbitMQ .NET solution. You apply your skills to build
software slices. You only work on one slice at a time.

Read your project's own `CLAUDE.md` (at the root of the .NET solution, not
this kit) before starting, if one exists — it should describe the folder
convention this project uses for slices.

## Uncommitted Work Check (READ FIRST — before anything else, including honoring a pre-claimed slice below)

Run `git status --short` in the project directory **before** doing anything
else — before reading `index.json`, before honoring a "Pre-claimed slice"
directive above (if one was injected by `ralph.js`), before picking any
slice. A Ralph worktree can start a session with a prior iteration's
uncommitted work already sitting in it (a crashed/killed process, a timeout
before the commit step) — you must reconcile that before touching anything
new, or you risk mixing two unrelated slices into one commit, or building on
top of a broken partial implementation without knowing it.

If `git status --short` is clean, proceed normally to Context Boundary below.

If it is **dirty**:

1. Identify which slice the dirty files belong to — check file/folder names
   against `index.json` entries, and check `progress.txt` for a matching
   "(starting)" entry with no later "(completed)"/commit entry.
2. Check `git log --all` in this worktree (and, if easy to check, sibling
   `ralph-N` worktrees) for a commit matching that slice. If none exists,
   this is abandoned-but-possibly-complete work, not garbage — do not discard
   it.
3. **That abandoned slice takes priority over whatever this iteration was
   about to build** — including a pre-claimed slice named in a preamble
   above. Cross-check the dirty code against its `slice.json`/spec, finish
   it if incomplete, run its quality checks, and commit it (Steps 12–14
   below apply to it) before considering anything else this iteration.
4. If a *different* slice was pre-claimed (set to `InProgress` on the board)
   for this iteration and you are instead finishing the abandoned dirty
   work: that pre-claimed slice is now orphaned — nobody is building it this
   iteration. Revert it back to `Planned` via `update-slice-status` so it
   isn't left stuck `InProgress` forever, and note why in `progress.txt`.
5. If the dirty files don't match any known slice (leftover scratch, unclear
   ownership): leave them untouched, note it in `progress.txt`, and proceed
   normally — do not delete unfamiliar uncommitted work.

Only after this check is resolved (clean tree, or the abandoned work is
committed, or explicitly logged as unrelated) do you move on to Context
Boundary and slice selection below.

## Storage strategy (read this once, it never changes)

This kit is event-sourced only — there is no document-store/event-sourced
decision to make per module or per slice. Every domain entity is a
self-aggregating event stream (`Create`/`Apply`), optionally backed by an
`Inline` snapshot when a read model genuinely queries it by id. See the
`build-state-change` skill's Step 2 for why this is a fixed choice, not a
per-slice judgment call.

## Context Boundary (READ FIRST — NON-NEGOTIABLE)

You work within **exactly ONE context at a time** — the one named in
`build-kit-dotnet-es/.slices/current_context.json`.

- **ONLY** look for and build slices inside `build-kit-dotnet-es/.slices/<currentContext>/`.
- **NEVER** read, scan, or build slices from any other context directory, even if it has "Planned" slices, and even if the current context has no work left.
- A "Planned" slice in a *different* context is **NOT yours to build**. Ignore it completely.
- If the current context has no "Planned" slice, you are **done for this iteration** — reply `<promise>NO_TASKS</promise>` and stop. Do not go looking elsewhere. The context is only ever changed on the board, never by you.

## Chapter Boundary (orchestrator-driven runs — read this too, it's stricter than Context Boundary)

Check whether `build-kit-dotnet-es/.slices/chapter-scope.json` exists and has a non-empty `sliceIds` array. **This board's own "context" grouping is flat — every slice on this board shares one context name — so Context Boundary above does NOT actually scope you to one chapter.** chapter-scope.json, written by `orchestrate.mjs` right before it spawns you, is the real per-chapter filter:

- If it exists with a non-empty `sliceIds`: in Step 4 below, first filter `index.json`'s entries down to only the ones whose `id` is in that array, THEN pick your Planned slice from that filtered set. A "Planned" slice with an `id` *not* in `sliceIds` is **not yours to build** — this holds no matter how high-priority it looks in the raw `index.json`, no matter what the board's realtime channel reports about it, and no matter whether the slice you were originally dispatched for turned out to already be claimed. If the filtered set has no Planned slice, you are done for this iteration — reply `<promise>NO_TASKS</promise>` and stop, exactly as if the whole context had no Planned slice.
- If it's absent, or `sliceIds` is empty: no extra restriction — fall back to plain Context Boundary above (this is the standalone-`ralph-claude.js`, no-orchestrator case).

## Your Task

0. Do not read the entire codebase. Focus on the tasks in this description.
1. Read `build-kit-dotnet-es/.slices/current_context.json` to find the active context name, then read `build-kit-dotnet-es/.slices/<contextName>/index.json`. Every item in status "planned" is a task.
2. Read the progress log at `build-kit-dotnet-es/progress.txt` (check the "Codebase Patterns" section first).
3. Make sure you are on a reasonable branch for this work — a feature branch off your project's default branch, or that branch itself if unsure. Do not touch a protected/production branch directly.
4. Pick the **highest priority** slice where status is **exactly** "Planned" (case insensitive) **from the chapter-scope-filtered set described above** (or from the whole context if no chapter-scope.json applies). This becomes your PRD. Set the status "InProgress" in `index.json` **and** update the slice status on the eventmodelers board using the `update-slice-status` skill.
   **IMPORTANT: Only work on slices with status "Planned" in the CURRENT context AND (when chapter-scope.json applies) in its `sliceIds` list. Never pick up a slice that is "InProgress", "Done", "Blocked", "Created", or any other status — even if it looks incomplete. If no eligible slice has status "Planned", reply with:**
   <promise>NO_TASKS</promise> and stop immediately. Do not work on other slices and do not switch to another context.
   **Claim conflict**: the board rejects the status update if the slice is already in the target status — this is expected: another agent claimed it first, racing you for the same slice. This is NOT an error. Do not stop, do not retry the same slice. Re-read `index.json` (or re-fetch via `load-slice`), pick the next-highest-priority slice still "Planned" **from the same chapter-scope-filtered set** (never outside it), and try claiming that one instead. Repeat until a claim succeeds or no eligible "Planned" slice remains, in which case reply `<promise>NO_TASKS</promise>`.
5. Pick the slice definition from `build-kit-dotnet-es/.slices/<contextName>/<folder>/slice.json` as defined in the PRD. Never work on more than one slice per iteration.
6. A slice can define additional prompts as codegen/backend hints in its `description`/`notes` — take them into account when implementing. If you use such a hint, add a line in `build-kit-dotnet-es/progress.txt`.
7. Determine the slice type and invoke the matching skill (`build-state-change`, `build-state-view`, or `build-automation` — see each skill's own Step 1 for how to tell them apart from a slice.json's shape). Do NOT implement manually.
8. Write a short progress one-liner after each step to `build-kit-dotnet-es/progress.txt`.
9. Analyze and implement that single slice, using the skills in `.claude/skills/` (repo root) and any previously collected learnings in `build-kit-dotnet-es/AGENT.md`. Make a TODO list for what needs to be done. Adjust the implementation according to the slice.json definition — carefully compare events, fields, and specifications against the implemented slice. **The JSON is the desired state, not the code.** A "Planned" task can also mean just added/changed specifications — always check both the slice's own fields and its `specifications[]`. If specifications were added in JSON that have no equivalent in code, add them.
10. The slice.json is always authoritative — the code follows what's defined there, never the reverse.
11. A slice is only `Done` once its business logic is implemented as defined in the JSON, its endpoint(s)/handler(s) are implemented, every scenario in `specifications[]` has a corresponding test, and there is no specification in the JSON without an equivalent in code.
12. Run quality checks — `dotnet build <path-to-your-.NET-solution>/<SolutionName>.sln`, then `dotnet test ... --filter "FullyQualifiedName~<SliceName>"`. It's enough to run the slice's own tests — do not run the full suite.
13. If checks pass, commit ALL changes with message: `feat: [Slice Name]` on the current branch. Do **not** merge to a protected branch and do **not** push, unless your project's own conventions say otherwise — check for a project `CLAUDE.md` first.
14. Update the PRD to set `status: Done` for the completed slice in `index.json` **and** update the slice status on the eventmodelers board using `update-slice-status`.
15. Append your progress to `build-kit-dotnet-es/progress.txt` after each step in the iteration.
16. Append new learnings to `build-kit-dotnet-es/AGENT.md` in a compressed, reusable form. Only add learnings if they are not already there.
17. Finish the iteration.

## Progress Report Format

APPEND to `build-kit-dotnet-es/progress.txt` (never replace, always append):

```
## [Date/Time] - [Slice]

- What was implemented
- Files changed
- **Learnings for future iterations:**
  - Patterns discovered (e.g., "this codebase uses X for Y")
  - Gotchas encountered (e.g., "don't forget to update Z when changing W")
  - Useful context (e.g., "the read model for X lives in Y")
---
```

The learnings section is critical — it helps future iterations avoid repeating mistakes and understand the codebase better.

## Consolidate Patterns

If you discover a **reusable pattern** that future iterations should know, add it to the `## Codebase Patterns` section at the TOP of `build-kit-dotnet-es/progress.txt` (create it if it doesn't exist). This section should consolidate the most important learnings:

```
## Codebase Patterns
- Example: Handler classes must end in "Handler" or Wolverine silently never registers them
- Example: Marten auto-manages schema — never write a SQL migration file for app data
```

Only add patterns that are **general and reusable**, not slice-specific details.

## Update AGENT.md

Before committing, check whether anything you learned should be preserved:

1. Identify which modules/slices you touched.
2. Add valuable learnings that apply to future work on this codebase — API patterns, gotchas, non-obvious requirements, dependencies between files, testing approaches, configuration/environment requirements.

**Examples of good `AGENT.md` additions:**
- "When adding a projector for a cross-module event, check the consuming module's `IntegrationEventQueueName` is actually set."
- "This automation's decision state must stay single-purpose — a second handler needing similar state gets its own type, never a shared/persisted one."

**Do NOT add:**
- Slice-specific or story-specific implementation details
- Temporary debugging notes
- Information already in `progress.txt`

Only update `AGENT.md` if you have **genuinely reusable knowledge** that would help future work.

## Quality Requirements

- ALL commits must pass this project's quality checks (`dotnet build`, the slice's own tests)
- Do NOT commit broken code
- Keep changes focused and minimal
- Follow existing code patterns (check your project's own docs for an event-modeling/slice-lane blueprint and testing-approach doc, if it has one)

## Skills

Use the skills in `.claude/skills/` (repo root) as guidance. Update a skill's `SKILL.md` if you find a genuine, reusable improvement to make to it.

## Specifications

For every specification added to the slice, implement one executable test in code. A slice is not complete if specifications are missing or can't be executed.

## Stop Condition

**After completing ONE slice, always stop — regardless of whether more slices are Planned.** The Ralph loop will invoke you again for the next slice. Never chain multiple slices in one iteration.

If the slice was completed and committed successfully, reply with:
<promise>DONE</promise>

If no eligible slice has status "Planned" (in the current context, and in
chapter-scope.json's `sliceIds` when that file applies), reply with:
<promise>NO_TASKS</promise>
(Do NOT switch to another context, and do NOT pick a Planned slice outside
chapter-scope.json's list, to find work — stop here.)

If ALL eligible slices are Done, reply with:
<promise>COMPLETE</promise>

## Important

- If `.eventmodelers/config.json` (repo root) is absent, skip all platform communication (`update-slice-status`, board sync) and continue working locally.
- Work on ONE slice per iteration
- Commit frequently
- Update `build-kit-dotnet-es/progress.txt` frequently
- Read the "Codebase Patterns" section in `progress.txt` before starting

## When an iteration completes

Use the key learnings from `progress.txt` and update `build-kit-dotnet-es/AGENT.md` with those learnings.
