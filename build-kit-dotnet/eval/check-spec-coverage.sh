#!/bin/bash
# check-spec-coverage.sh — WS3.4: a standalone, gate-only version of the
# spec-coverage check run-slice.sh's Step 5 performs (WS3.3), for checking
# the CURRENT working-tree implementation against a slice's wired
# specifications WITHOUT run-slice.sh's strip-baseline-and-rebuild flow
# (which spends a real agent iteration). Same manifest fields
# (specificationsFile, testFiles), same coverage-COUNT heuristic, same
# spec-coverage semantics — a fast "did this board change break coverage"
# check, usable any time, not just inside a full eval run.
#
# Usage: ./check-spec-coverage.sh <sliceFolder>
# Exit 0: coverage OK, or no specifications wired for this slice ("n/a").
# Exit 1: real coverage shortfall (test method count < specification count).

set -euo pipefail

KIT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
REPO_ROOT="$(cd "$KIT_DIR/.." && pwd)"
PROJECT_DIR="$REPO_ROOT/code/K9Crush-scaffold/K9Crush"
EVAL_DIR="$KIT_DIR/eval"
MANIFEST="$EVAL_DIR/slices-manifest.json"

SLICE_FOLDER="${1:?Usage: check-spec-coverage.sh <sliceFolder> — see eval/slices-manifest.json for valid folders}"

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

if [[ -z "$SPECIFICATIONS_FILE" ]]; then
  echo "[check-spec-coverage] $SLICE_FOLDER: no specificationsFile wired — n/a"
  exit 0
fi

SPEC_FILE_PATH="$EVAL_DIR/$SPECIFICATIONS_FILE"
if [[ ! -f "$SPEC_FILE_PATH" ]]; then
  echo "[check-spec-coverage] $SLICE_FOLDER: specificationsFile listed but not found at $SPEC_FILE_PATH" >&2
  exit 1
fi

SPEC_COUNT=$(node -e "console.log(JSON.parse(require('fs').readFileSync('$SPEC_FILE_PATH','utf8')).specifications.length)")

TEST_METHOD_COUNT=0
for f in $(node -e "console.log($TEST_FILES_JSON.join(' '))"); do
  full_path="$PROJECT_DIR/$f"
  if [[ -f "$full_path" ]]; then
    count=$(grep -cE '^\s*\[(Fact|Theory)' "$full_path" || true)
    TEST_METHOD_COUNT=$((TEST_METHOD_COUNT + count))
  fi
done

echo "[check-spec-coverage] $SLICE_FOLDER: $TEST_METHOD_COUNT test method(s) vs $SPEC_COUNT specification(s)"

if [[ "$TEST_METHOD_COUNT" -lt "$SPEC_COUNT" ]]; then
  echo "[check-spec-coverage] FAIL — spec-coverage shortfall"
  exit 1
fi

echo "[check-spec-coverage] PASS"
exit 0
