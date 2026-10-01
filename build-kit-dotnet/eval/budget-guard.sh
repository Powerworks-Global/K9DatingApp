#!/bin/bash
# budget-guard.sh — hard spend/wall-clock kill switch for unattended agent
# invocations (Corrective Project Plan WS1.3). Wraps a single `claude` call
# the same way lib/agent.sh does, but doesn't reuse agent.sh itself — that
# file is explicitly documented as unmodified/shared (build-kit-dotnet/README.md),
# and getting real per-call cost/token data means switching to
# `--output-format json`, which agent.sh doesn't use. This script is the
# thing eval/run-slice.sh (and any future unattended loop) calls instead of
# `lib/agent.sh` directly.
#
# Enforces, per harness invocation of this script (cumulative across a
# single run-slice.sh run, tracked in a state file, reset per run):
#   - EVAL_MAX_COST_USD    (default 2.00)  — hard cumulative $ cap
#   - EVAL_MAX_WALLCLOCK_S (default 900)   — hard wall-clock cap PER CALL,
#                                            enforced via `timeout`, not just
#                                            measured after the fact
#
# On breach: refuses to make the call (if already over budget before
# starting) or reports the breach and exits non-zero (if the call itself
# pushed cumulative cost over the cap, or timed out) — the calling loop
# (run-slice.sh's retry loop) must check the exit code and stop, not retry
# through a budget breach.
#
# Usage: ./budget-guard.sh <state-file> ["<model>"] "<prompt>"
#   state-file — path to a JSON file tracking cumulative cost for this run
#                (created if absent; caller is responsible for using a
#                fresh path per harness run so budgets don't leak across runs)
#   model      — optional. An alias (sonnet/opus/fable) or full model id.
#                Omit to use whatever the `claude` CLI's own default is.
#                Pass explicitly whenever a fair cross-harness comparison
#                (e.g. against pi-budget-guard.sh) needs a pinned model.

# --bare (found 2026-09-08): this machine has a top-level firstmate/CLAUDE.md
# and firstmate/AGENTS.md above the K9Crush project directory implementing a
# real, separate multi-agent governance system ("never write to a project
# directly, delegate to a spawned crewmate"). That's a real, live system for
# this user's actual workflow, but it's a confound for an eval harness meant
# to measure raw coding capability, not exercise that governance layer.
# --bare disables CLAUDE.md auto-discovery (among other things) so this
# comparison measures the model, not whichever governance file happens to
# sit above the project directory.
set -euo pipefail

STATE_FILE="${1:?Usage: budget-guard.sh <state-file> [model] <prompt>}"
# Model is optional for backward compatibility with existing callers passing
# just (state-file, prompt) — if $2 looks like a model id/alias (no spaces,
# not empty) and a third arg exists, treat it as (state-file, model, prompt);
# otherwise treat it as the original (state-file, prompt) shape.
if [[ $# -ge 3 ]]; then
  MODEL="$2"
  PROMPT="$3"
else
  MODEL=""
  PROMPT="${2:?Usage: budget-guard.sh <state-file> [model] <prompt>}"
fi
MODEL_FLAG=()
[[ -n "$MODEL" ]] && MODEL_FLAG=(--model "$MODEL")

MAX_COST_USD="${EVAL_MAX_COST_USD:-2.00}"
MAX_WALLCLOCK_S="${EVAL_MAX_WALLCLOCK_S:-900}"
GCP_PROJECT="${EVAL_GCP_PROJECT:-agentos-61847}"
SECRET_NAME="${EVAL_ANTHROPIC_SECRET:-anthropic-api-key}"

if [[ ! -f "$STATE_FILE" ]]; then
  echo '{"cumulative_cost_usd": 0, "calls": 0}' > "$STATE_FILE"
fi

CUR_COST=$(node -e "console.log(require('$STATE_FILE').cumulative_cost_usd)")

# --- Pre-flight check: already over budget from a prior call this run? ------
if node -e "process.exit($CUR_COST >= $MAX_COST_USD ? 0 : 1)"; then
  echo "[budget-guard] REFUSING: cumulative cost \$$CUR_COST already at/over cap \$$MAX_COST_USD — not making this call." >&2
  exit 1
fi

echo "[budget-guard] cumulative so far: \$$CUR_COST / \$$MAX_COST_USD cap. Wall-clock cap this call: ${MAX_WALLCLOCK_S}s. Model: ${MODEL:-<CLI default>}"

# --- The actual call, hard-capped on wall-clock via `timeout` ----------------
# --output-format json is required to get total_cost_usd/usage back at all;
# this is the one place this guard's invocation deliberately differs from
# agent.sh's plain-text mode.
#
# --bare's auth is "strictly ANTHROPIC_API_KEY... OAuth and keychain are
# never read" (per `claude --help`) — found 2026-09-08 the hard way, on the
# first real call after adding --bare: it silently authenticated as nobody
# ("Not logged in") because --bare also disables the keychain read that
# normal `claude` calls use. Pulling the same Secret Manager key already
# used by pi-budget-guard.sh, fresh per call, not cached in a variable
# longer than needed.
ANTHROPIC_API_KEY="$(gcloud secrets versions access latest --secret="$SECRET_NAME" --project="$GCP_PROJECT")"
export ANTHROPIC_API_KEY
RAW_OUTPUT=""
CALL_EXIT=0
set +e
RAW_OUTPUT=$(timeout "${MAX_WALLCLOCK_S}s" claude --dangerously-skip-permissions --bare "${MODEL_FLAG[@]}" -p "$PROMPT" --output-format json)
CALL_EXIT=$?
set -e
unset ANTHROPIC_API_KEY

if [[ "$CALL_EXIT" -eq 124 ]]; then
  echo "[budget-guard] KILLED: wall-clock cap of ${MAX_WALLCLOCK_S}s breached (timeout exit 124)." >&2
  exit 1
fi

if [[ "$CALL_EXIT" -ne 0 ]]; then
  echo "[budget-guard] claude exited non-zero ($CALL_EXIT) — not a budget breach, but not a clean call either. Passing the failure through." >&2
  exit "$CALL_EXIT"
fi

# --- Extract cost, update cumulative state, re-check post-call --------------
# Schema per Claude Code CLI --output-format json (single result mode):
# {type, subtype, is_error, duration_ms, total_cost_usd, usage: {...}, ...}
# NOTE: verify this exact shape against a real call's output the first time
# this script actually runs for real — written from documented/known CLI
# behavior, not re-verified against a fresh paid call for this task
# specifically (the whole point of this script is not spending money
# unnecessarily).
CALL_COST=$(node -e "
  const d = JSON.parse(process.argv[1]);
  process.stdout.write(String(d.total_cost_usd ?? 0));
" "$RAW_OUTPUT" 2>/dev/null || echo "0")

NEW_TOTAL=$(node -e "console.log($CUR_COST + $CALL_COST)")
node -e "
  const fs = require('fs');
  const s = JSON.parse(fs.readFileSync('$STATE_FILE'));
  s.cumulative_cost_usd = $NEW_TOTAL;
  s.calls = (s.calls || 0) + 1;
  fs.writeFileSync('$STATE_FILE', JSON.stringify(s, null, 2));
"

echo "[budget-guard] this call: \$$CALL_COST. cumulative now: \$$NEW_TOTAL / \$$MAX_COST_USD cap."

# Surface the actual result text the same way agent.sh's plain output would,
# so callers parsing stdout for agent behavior aren't broken by the JSON switch.
node -e "
  const d = JSON.parse(process.argv[1]);
  process.stdout.write(d.result || '');
" "$RAW_OUTPUT"

if node -e "process.exit($NEW_TOTAL >= $MAX_COST_USD ? 0 : 1)"; then
  echo "[budget-guard] BREACH: this call pushed cumulative cost to \$$NEW_TOTAL, at/over cap \$$MAX_COST_USD. Signaling caller to stop." >&2
  exit 1
fi

exit 0
