#!/bin/bash
# record-spec-baseline.sh — WS3.5: a deliberate, explicit "I've confirmed
# this specification content matches the current code" checkpoint for one
# slice. NEVER called automatically by check-spec-coverage.sh or any other
# script — recording a baseline is a decision a human makes on purpose, not
# a side effect of a passing check. If a baseline were auto-updated on every
# passing check, drift could never be detected between two passing runs:
# the very act of checking would overwrite the baseline with the drifted
# content before anyone saw the difference.
#
# Usage: ./record-spec-baseline.sh <sliceFolder>

set -euo pipefail

KIT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
EVAL_DIR="$KIT_DIR/eval"
MANIFEST="$EVAL_DIR/slices-manifest.json"
BASELINE_DIR="$EVAL_DIR/.spec-baseline"

SLICE_FOLDER="${1:?Usage: record-spec-baseline.sh <sliceFolder> — see eval/slices-manifest.json for valid folders}"

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
if [[ -z "$SPECIFICATIONS_FILE" ]]; then
  echo "[record-spec-baseline] $SLICE_FOLDER: no specificationsFile wired — nothing to baseline" >&2
  exit 1
fi

SPEC_FILE_PATH="$EVAL_DIR/$SPECIFICATIONS_FILE"
if [[ ! -f "$SPEC_FILE_PATH" ]]; then
  echo "[record-spec-baseline] $SLICE_FOLDER: specificationsFile listed but not found at $SPEC_FILE_PATH" >&2
  exit 1
fi

mkdir -p "$BASELINE_DIR"
cp "$SPEC_FILE_PATH" "$BASELINE_DIR/$SLICE_FOLDER.json"
echo "[record-spec-baseline] $SLICE_FOLDER: baseline recorded from $SPEC_FILE_PATH -> $BASELINE_DIR/$SLICE_FOLDER.json"
