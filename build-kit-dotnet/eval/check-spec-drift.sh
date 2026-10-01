#!/bin/bash
# check-spec-drift.sh — WS3.5: flag when a slice's current specification
# content has diverged from its last recorded baseline (record-spec-baseline.sh).
#
# Distinct from check-spec-coverage.sh (WS3.3/3.4), which only compares
# COUNTS (test methods vs. specifications) at the moment it's run. This
# catches a different, equally real failure mode: an EXISTING specification's
# given/when/then text changes with no change in count at all -- coverage
# stays green (same number of specs, same number of tests), but the code's
# behavior no longer matches what's actually specified. That silent case is
# what this script exists to catch.
#
# Flag only, no auto-reconcile -- this script never modifies code, never
# touches the baseline file, never regenerates anything. It reports.
#
# Usage: ./check-spec-drift.sh <sliceFolder>
# Exit 0: no baseline yet (nothing to compare), or baseline matches current.
# Exit 1: real drift -- current specifications differ from the baseline.

set -euo pipefail

KIT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
EVAL_DIR="$KIT_DIR/eval"
MANIFEST="$EVAL_DIR/slices-manifest.json"
BASELINE_DIR="$EVAL_DIR/.spec-baseline"

SLICE_FOLDER="${1:?Usage: check-spec-drift.sh <sliceFolder> — see eval/slices-manifest.json for valid folders}"

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
  echo "[check-spec-drift] $SLICE_FOLDER: no specificationsFile wired — n/a"
  exit 0
fi

SPEC_FILE_PATH="$EVAL_DIR/$SPECIFICATIONS_FILE"
BASELINE_FILE_PATH="$BASELINE_DIR/$SLICE_FOLDER.json"

if [[ ! -f "$SPEC_FILE_PATH" ]]; then
  echo "[check-spec-drift] $SLICE_FOLDER: specificationsFile listed but not found at $SPEC_FILE_PATH" >&2
  exit 1
fi

if [[ ! -f "$BASELINE_FILE_PATH" ]]; then
  echo "[check-spec-drift] $SLICE_FOLDER: no baseline recorded — nothing to compare, run record-spec-baseline.sh first"
  exit 0
fi

node -e "
const fs = require('fs');
const current = JSON.parse(fs.readFileSync('$SPEC_FILE_PATH', 'utf8')).specifications;
const baseline = JSON.parse(fs.readFileSync('$BASELINE_FILE_PATH', 'utf8')).specifications;

const byId = (arr) => new Map(arr.map((s) => [s.id, s]));
const curById = byId(current);
const baseById = byId(baseline);

const added = [...curById.keys()].filter((id) => !baseById.has(id));
const removed = [...baseById.keys()].filter((id) => !curById.has(id));
const changed = [...curById.keys()]
  .filter((id) => baseById.has(id))
  .filter((id) => JSON.stringify(curById.get(id)) !== JSON.stringify(baseById.get(id)));

if (added.length === 0 && removed.length === 0 && changed.length === 0) {
  console.log('[check-spec-drift] $SLICE_FOLDER: no drift');
  process.exit(0);
}

console.log('[check-spec-drift] $SLICE_FOLDER: DRIFT DETECTED');
if (added.length) console.log('  added: ' + added.join(', '));
if (removed.length) console.log('  removed: ' + removed.join(', '));
if (changed.length) {
  for (const id of changed) {
    console.log('  changed: ' + id + ' (\"' + baseById.get(id).title + '\")');
    const b = baseById.get(id), c = curById.get(id);
    for (const field of ['title', 'given', 'when', 'then', 'rule']) {
      if (b[field] !== c[field]) {
        console.log('    ' + field + ': \"' + (b[field] ?? '') + '\" -> \"' + (c[field] ?? '') + '\"');
      }
    }
  }
}
process.exit(1);
"
