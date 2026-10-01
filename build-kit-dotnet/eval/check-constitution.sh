#!/bin/bash
# check-constitution.sh — WS4.2: runs the mechanically-checkable subset of
# CONSTITUTION.md against one slice — B1 (coverage), B2 (drift), and C1
# (no duplicate rejection, checked mechanically like B1). Tier A and C2 have
# no automated check yet (see CONSTITUTION.md's own status notes) and are
# not run here — this script only reports what it can actually verify.
#
# Reports each rule separately, does not collapse to one pass/fail bit —
# an honest multi-rule report of real current state.
#
# Usage: ./check-constitution.sh <sliceFolder>
# Exit 0: every checked rule passed (or was n/a).
# Exit 1: at least one checked rule failed or flagged drift.

set -uo pipefail

KIT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
EVAL_DIR="$KIT_DIR/eval"
MANIFEST="$EVAL_DIR/slices-manifest.json"

SLICE_FOLDER="${1:?Usage: check-constitution.sh <sliceFolder> — see eval/slices-manifest.json for valid folders}"

echo "=== K9Crush Constitution check: $SLICE_FOLDER ==="
echo

OVERALL=0

echo "--- Rule B1: Specification coverage ---"
"$EVAL_DIR/check-spec-coverage.sh" "$SLICE_FOLDER"
[[ $? -ne 0 ]] && OVERALL=1
echo

echo "--- Rule B2: Specification drift ---"
"$EVAL_DIR/check-spec-drift.sh" "$SLICE_FOLDER"
[[ $? -ne 0 ]] && OVERALL=1
echo

echo "--- Rule C1: No duplicate rejection ---"
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
TEST_FILES_JSON="$(manifest_field testFiles)"
REPO_ROOT="$(cd "$KIT_DIR/.." && pwd)"
PROJECT_DIR="$REPO_ROOT/code/K9Crush-scaffold/K9Crush"

if [[ "$SLICE_FOLDER" != "RejectApplication" ]]; then
  echo "[check-constitution] Rule C1 is RejectApplication-specific — n/a for $SLICE_FOLDER"
elif [[ -z "$SPECIFICATIONS_FILE" ]]; then
  echo "[check-constitution] Rule C1: no specificationsFile wired — n/a"
else
  SPEC_FILE_PATH="$EVAL_DIR/$SPECIFICATIONS_FILE"
  HAS_DUP_SPEC=$(node -e "
    const specs = JSON.parse(require('fs').readFileSync('$SPEC_FILE_PATH','utf8')).specifications;
    const hit = specs.some(s => /already.*reject|duplicate.*reject|reject.*again/i.test(s.title + ' ' + s.given + ' ' + s.then));
    console.log(hit ? 'yes' : 'no');
  ")
  if [[ "$HAS_DUP_SPEC" != "yes" ]]; then
    echo "[check-constitution] Rule C1: no specification names the duplicate-rejection guard — n/a"
  else
    HAS_DUP_TEST=no
    for f in $(node -e "console.log($TEST_FILES_JSON.join(' '))"); do
      full_path="$PROJECT_DIR/$f"
      if [[ -f "$full_path" ]] && grep -qiE 'already.*reject|duplicate.*reject|reject.*again|conflict' "$full_path"; then
        HAS_DUP_TEST=yes
      fi
    done
    if [[ "$HAS_DUP_TEST" == "yes" ]]; then
      echo "[check-constitution] Rule C1: PASS — a test covering the duplicate-rejection guard was found"
    else
      echo "[check-constitution] Rule C1: FAIL — specification names the duplicate-rejection guard, no matching test found"
      OVERALL=1
    fi
  fi
fi
echo

echo "=== Summary: $([[ $OVERALL -eq 0 ]] && echo 'all checked rules pass' || echo 'one or more rules FAILED/flagged') ==="
exit $OVERALL
