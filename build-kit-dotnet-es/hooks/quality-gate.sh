#!/usr/bin/env bash
# Quality gate — Claude Code PreToolUse hook for the Bash tool.
#
# This is Phase 1 (Option A: deterministic-only) of quality-checks.md.
# Install instructions: see hooks/README.md — this must be registered in
# the TARGET .NET solution's own .claude/settings.json, not this kit's,
# because Ralph invokes `claude` with cwd set to the solution's own
# directory (see ralph-claude.js). That also means this hook only ever
# fires for Claude-Code-mediated Bash calls — a human running `git commit`
# directly in a terminal is untouched by this, by construction, not by
# extra configuration.
#
# Scope, as of 2026-07-31: this hook only runs the checks that are cheap
# regardless of how many times they fire — secret-scan (a grep) and the
# stuck-loop guard (a hash compare). The solution-wide checks (dotnet
# format --verify-no-changes / dotnet build / dotnet list package
# --vulnerable) moved to orchestrate.mjs's runQualityGate(), which runs
# them once per worktree at merge time instead of once per commit here.
# Re-running a full solution build/format/vuln-scan on every single commit
# attempt — across every parallel Ralph instance — was real, measured
# throughput cost for no extra safety once a per-worktree gate exists
# right before the code lands anyway.
#
# Contract: reads the PreToolUse JSON payload on stdin
# ({"tool_name": "Bash", "tool_input": {"command": "..."}, ...}),
# exits 0 to allow the tool call, exits 2 to block it (stderr is shown
# back to the model as the reason).

set -uo pipefail

INPUT=$(cat)
COMMAND=$(printf '%s' "$INPUT" | python3 -c "import json,sys
try:
    d = json.load(sys.stdin)
    print(d.get('tool_input', {}).get('command', ''))
except Exception:
    print('')" 2>/dev/null || echo "")

# Not a Bash tool call, or no command — nothing to do.
if [ -z "$COMMAND" ]; then
  exit 0
fi

# Ephemeral, gitignore-able state (loop-guard counters) vs. tracked audit
# trails (bypass log, passed-gate log) are deliberately separate
# directories — see hooks/README.md.
STATE_DIR=".claude/state"
METRICS_DIR=".claude/metrics"
AUDIT_FILE="$METRICS_DIR/gate-bypass-audit.jsonl"
GATE_HISTORY_FILE="$METRICS_DIR/gate-history.jsonl"
mkdir -p "$STATE_DIR" "$METRICS_DIR" 2>/dev/null

hash_stdin() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | cut -d' ' -f1
  else
    shasum -a 256 | cut -d' ' -f1
  fi
}

block() {
  echo "$1" >&2
  exit 2
}

# ============================================================
# git commit path
# ============================================================
if printf '%s' "$COMMAND" | grep -qE '(^|[;&|]| )git[[:space:]]+commit([[:space:]]|$)'; then

  # --- Bypass path: --no-verify / -n ---
  if printf '%s' "$COMMAND" | grep -qE -- '--no-verify|(^|[[:space:]])-n([[:space:]]|$)'; then
    if [ -z "${GATE_BYPASS_REASON:-}" ]; then
      block "BLOCKED: git commit --no-verify (or -n) requires a reason.

Set GATE_BYPASS_REASON to a non-empty explanation and retry, e.g.:
  GATE_BYPASS_REASON=\"hotfix, gate to follow\" git commit --no-verify -m \"...\"

Every bypass is audited to .claude/metrics/gate-bypass-audit.jsonl — this
is not a silent skip."
    fi

    TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
    BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "unknown")
    STAGED_COUNT=$(git diff --cached --name-only 2>/dev/null | wc -l | tr -d ' ')
    ESCAPED_REASON=$(printf '%s' "$GATE_BYPASS_REASON" | sed 's/\\/\\\\/g; s/"/\\"/g')
    printf '{"timestamp":"%s","branch":"%s","reason":"%s","stagedFiles":%s}\n' \
      "$TIMESTAMP" "$BRANCH" "$ESCAPED_REASON" "$STAGED_COUNT" >> "$AUDIT_FILE"
    exit 0
  fi

  # --- Normal commit: run the deterministic gate ---
  # Just the secret scan here — dotnet format/build/vulnerable-package
  # checks moved to orchestrate.mjs's per-worktree gate (see header comment
  # above).
  FAILURES=""

  # 1. Secret scan over the staged diff
  if git diff --cached 2>/dev/null | grep -qEi "(api[_-]?key|secret|password|token)[[:space:]]*[:=][[:space:]]*['\"][^'\"]{8,}"; then
    FAILURES="${FAILURES}- Possible secret found in the staged diff (matched an api_key/secret/password/token pattern). Remove it and use a proper secrets manager (e.g. dotnet user-secrets locally, a real vault in any deployed environment) instead.\n"
  fi

  if [ -n "$FAILURES" ]; then
    block "BLOCKED: quality gate failed before commit.

$(printf '%b' "$FAILURES")
Fix these and try committing again — do not bypass unless there is a
genuine reason (bypasses are audited, see above).

To bypass: GATE_BYPASS_REASON=\"...\" git commit --no-verify -m \"...\""
  fi

  # Passed — append an audit-trail entry (evidence, not a skip-cache; the
  # secret-scan re-runs on every commit attempt, it's cheap enough to).
  TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
  DIFF_HASH=$(git diff --cached 2>/dev/null | hash_stdin)
  printf '{"timestamp":"%s","diffHash":"%s","status":"passed"}\n' "$TIMESTAMP" "$DIFF_HASH" >> "$GATE_HISTORY_FILE"
  exit 0
fi

# ============================================================
# Stuck-loop guard: the same dotnet build/test command re-run 3+ times
# with no code change in between.
# ============================================================
if printf '%s' "$COMMAND" | grep -qE '(^|[;&|]| )dotnet[[:space:]]+(build|test)([[:space:]]|$)'; then
  NORMALIZED=$(printf '%s' "$COMMAND" | tr -s '[:space:]' ' ')
  # Exclude .claude/ itself — this hook's own state/audit files are
  # untracked and would otherwise show up as a "change" on every
  # invocation, permanently defeating this exact comparison.
  TREE_HASH=$(git status --porcelain -- . ':(exclude).claude' 2>/dev/null | hash_stdin)
  STATE_FILE="$STATE_DIR/verify-guard.json"

  if [ -f "$STATE_FILE" ]; then
    PREV_CMD=$(python3 -c "import json; print(json.load(open('$STATE_FILE')).get('command',''))" 2>/dev/null || echo "")
    PREV_TREE=$(python3 -c "import json; print(json.load(open('$STATE_FILE')).get('treeHash',''))" 2>/dev/null || echo "")
    PREV_COUNT=$(python3 -c "import json; print(json.load(open('$STATE_FILE')).get('count',0))" 2>/dev/null || echo "0")
  else
    PREV_CMD=""
    PREV_TREE=""
    PREV_COUNT="0"
  fi

  if [ "$NORMALIZED" = "$PREV_CMD" ] && [ "$TREE_HASH" = "$PREV_TREE" ]; then
    COUNT=$((PREV_COUNT + 1))
  else
    COUNT=1
  fi

  printf '{"command":"%s","treeHash":"%s","count":%s}\n' \
    "$(printf '%s' "$NORMALIZED" | sed 's/"/\\"/g')" "$TREE_HASH" "$COUNT" > "$STATE_FILE"

  if [ "$COUNT" -ge 3 ]; then
    rm -f "$STATE_FILE"
    block "BLOCKED: the same command has been re-run 3 times with no code
change in between:
  $NORMALIZED

Re-running it again is very unlikely to produce a different result. Stop
and diagnose instead: read the actual failure output, form a specific
hypothesis about the cause, and make a targeted code change before
re-running — don't just retry."
  fi
fi

exit 0
