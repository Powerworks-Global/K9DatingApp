#!/bin/bash
# generate-runbook.sh — WS4.3: a runbook SKELETON generator from a slice's
# Rules/Examples (specifications/<folder>.json) and its operability analysis
# (operability/<folder>.json, mechanizing WS4.1's Board Vocabulary ->
# Operability Mapping instead of leaving it stranded as prose).
#
# "Skeleton" per the task's own wording: populates what real data exists
# (Preconditions from Rules, Steps from Examples, Rollback/Escalation where
# operability.json actually has an answer) and leaves an explicit, visible
# TODO wherever it doesn't -- never invents a rollback path or an escalation
# contact that isn't real.
#
# Usage: ./generate-runbook.sh <sliceFolder>

set -euo pipefail

KIT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
EVAL_DIR="$KIT_DIR/eval"
MANIFEST="$EVAL_DIR/slices-manifest.json"

SLICE_FOLDER="${1:?Usage: generate-runbook.sh <sliceFolder> — see eval/slices-manifest.json for valid folders}"

manifest_field() {
  node -e "
    const m = require('$MANIFEST');
    const s = m.slices.find(x => x.folder === process.argv[1]);
    if (!s) { console.error('Unknown slice folder: ' + process.argv[1]); process.exit(1); }
    const v = s[process.argv[2]];
    if (v === undefined) { process.stdout.write(''); process.exit(0); }
    process.stdout.write(typeof v === 'string' ? v : JSON.stringify(v));
  " "$SLICE_FOLDER" "$1"
}

SPECIFICATIONS_FILE="$(manifest_field specificationsFile)"
SLICE_TITLE="$(manifest_field title)"

if [[ -z "$SPECIFICATIONS_FILE" ]]; then
  echo "[generate-runbook] $SLICE_FOLDER: no specificationsFile wired — nothing to generate a runbook from" >&2
  exit 1
fi

SPEC_FILE_PATH="$EVAL_DIR/$SPECIFICATIONS_FILE"
OPERABILITY_FILE_PATH="$EVAL_DIR/operability/$SLICE_FOLDER.json"
OUT_DIR="$EVAL_DIR/runbooks"
mkdir -p "$OUT_DIR"

node -e "
const fs = require('fs');

const specData = JSON.parse(fs.readFileSync('$SPEC_FILE_PATH', 'utf8'));
const specs = specData.specifications;
const operability = fs.existsSync('$OPERABILITY_FILE_PATH')
  ? JSON.parse(fs.readFileSync('$OPERABILITY_FILE_PATH', 'utf8')).outcomes
  : [];
const opBySpecId = new Map(operability.filter(o => o.sourceSpecId).map(o => [o.sourceSpecId, o]));

// Whether a given spec has a real matching test is NOT reliably determinable
// here — there is no formal spec-id-to-test-method mapping anywhere in this
// codebase (that's real, harder semantic-matching work WS3.5 explicitly
// deferred). A text-similarity heuristic was tried and produced false
// negatives against real, existing tests — worse than saying nothing, since
// it asserts a gap that isn't real. So this generator only claims a spec is
// a 'known gap' when operability.json's own curated isFailureMode flag says
// so (a deliberate, human-verified signal), never from an automated guess.

const rules = new Map();
for (const s of specs) {
  const rule = s.rule || '(no rule attached)';
  if (!rules.has(rule)) rules.set(rule, []);
  rules.get(rule).push(s);
}

const lines = [];
lines.push('# Runbook — $SLICE_TITLE');
lines.push('');
lines.push('_Generated skeleton (WS4.3) from specifications/$SLICE_FOLDER.json and operability/$SLICE_FOLDER.json — TODO markers are real gaps, not omissions._');
lines.push('');

for (const [rule, ruleSpecs] of rules) {
  lines.push('## Precondition: ' + rule);
  lines.push('');
  for (const s of ruleSpecs) {
    const op = opBySpecId.get(s.id);
    lines.push('### Step: ' + s.title);
    lines.push('**Trigger:** ' + s.given + ' → ' + s.when);
    lines.push('**Expected outcome:** ' + s.then);
    if (op && op.rollback) {
      const r = op.rollback;
      lines.push('**Rollback:** ' + (r.possible ? (r.compensatingAction || 'possible') : (r.reason || 'not possible')) + (r.compensatingAction && !r.possible ? ' (compensating action: ' + r.compensatingAction + ')' : ''));
    } else {
      lines.push('**Rollback:** TODO: not yet specified');
    }
    if (op && op.escalation && op.escalation.contact) {
      lines.push('**Escalation:** ' + op.escalation.contact);
    } else {
      lines.push('**Escalation:** TODO: not yet specified');
    }
    if (op && op.isFailureMode) {
      lines.push('**Known gap:** yes — see \"Known gaps\" section below');
    }
    lines.push('');
  }
}

const unverifiedFailureModes = operability.filter(o => o.isFailureMode);
if (unverifiedFailureModes.length > 0) {
  lines.push('## Known gaps — failure modes without a verified path today');
  lines.push('');
  for (const o of unverifiedFailureModes) {
    lines.push('- **' + o.name + '**' + (o.constitutionRule ? ' — CONSTITUTION.md Rule ' + o.constitutionRule : ''));
    if (o.rollback && o.rollback.reason) lines.push('  - Rollback: ' + o.rollback.reason);
    if (o.escalation && o.escalation.contact) lines.push('  - Escalation: ' + o.escalation.contact);
  }
  lines.push('');
}

fs.writeFileSync('$OUT_DIR/$SLICE_FOLDER.md', lines.join('\n'));
console.error('[generate-runbook] wrote $OUT_DIR/$SLICE_FOLDER.md');
"
