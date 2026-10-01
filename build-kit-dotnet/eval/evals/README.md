# evals.json — WS1.5 (Corrective Project Plan)

This directory adopts the `evals/evals.json` convention from the ACES (Agentic Continuous Evaluation of Skills) paper's schema, per the plan's WS1.5 task: "Adopt the `evals/evals.json` convention per the Agent Skills spec so per-skill evals compose into this suite."

`evals.json` holds one entry per slice in `../slices-manifest.json` (12 total), each with the ACES-standard shape: `id`, `user_question`, `expected_skill`, `expected_script`, `ground_truth`, `expected_behavior`. `id` matches both the manifest's `folder` field and `results.jsonl`'s `slice_folder`, so a run result and its eval case join directly on that key.

## What this is, and isn't

**Is**: a schema-compliant eval dataset, so this suite's cases are shaped the same way any other skill's `evals/evals.json` would be — the composability WS1.5 asked for.

**Isn't**: a working ACES grader. `run-slice.sh` doesn't read this file or score `expected_behavior`/`expected_skill` against a saved trajectory — it only ever recorded `passed_mechanical`/`iterations_to_green`/`agent_final_status`/`failure_class` in `results.jsonl`, and still does. Wiring real ACES-style behavior-check scoring (which needs a saved trajectory in something like ATIF format, plus an LLM-judge pass) is a separate, larger piece of work, not scoped into WS1.5.

## Where the content came from

Not invented — `expected_skill` and `expected_behavior` are transcribed from `code/K9Crush-scaffold/K9Crush/AGENTS.md`'s "Building a Slice" section (the actual instructions the agent works from), cross-checked against `slices-manifest.json`'s `sliceType` field. Two slices (`MarkApplicationStale`, `CloseStaleApplication`) are tagged `STATE_CHANGE` on the board but are real `Automation`s in code (manifest's own `sliceTypeNoteRealCode`) — `expected_skill` follows the real code, not the board label, same principle the manifest itself already applies.

`expected_skill` deliberately omits `load-slice` even though AGENTS.md lists it as step 1: `run-slice.sh` hides `.eventmodelers/config.json` for the run's duration specifically so the agent can't reach the live board, and pre-seeds `.slices/default/` locally instead — `load-slice` isn't actually exercised in eval mode. Documented in `evals.json`'s own `_expected_skill_caveat` field so this doesn't need re-deriving later.
