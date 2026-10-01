#!/usr/bin/env node
// orchestrate.mjs — pick a chapter from the board, make sure every column
// in it has a slice (retrofitting a SLICE_BORDER node if the chapter
// predates that structure), flip each slice's status to "Planned" so
// Ralph will pick it up, then start N Ralph loops in the background to
// build them.
//
// Usage:
//   node orchestrate.mjs <project_dir> [chapterName] [--parallel N] [--watch] [--timeout-minutes N]
//   node orchestrate.mjs <project_dir> --merge [--parallel N]
//
// If chapterName is omitted, every chapter on the board is listed and you
// pick one interactively. --parallel defaults to 2 (also controls how many
// instance worktrees/branches --merge looks for).
//
// Each instance gets its own git worktree (sibling directory, on branch
// ralph/instance-N) instead of sharing projectDir's working tree — running
// two instances against one shared tree caused repeated lost-update races
// (concurrent full-file rewrites of the same source files), costing several
// extra corrective commits per slice. Once the chapter's tracked slices all
// reach a terminal state, each instance's branch is merged back into the
// branch orchestrate.mjs started on, and the worktree is removed. Run with
// --merge (no chapter needed) to merge+clean up existing instance branches
// after killing instances early, before a chapter finished.

import { spawn, execFileSync } from 'child_process';
import { readFileSync, existsSync, openSync, appendFileSync, writeFileSync, mkdirSync } from 'fs';
import { dirname, resolve, join, basename } from 'path';
import { fileURLToPath } from 'url';
import { randomUUID } from 'crypto';
import { createInterface } from 'readline';

const kitDir = dirname(fileURLToPath(import.meta.url));

// ── Args ─────────────────────────────────────────────────────────────────
const rawArgs = process.argv.slice(2);
function takeFlag(name, hasValue = true) {
  const idx = rawArgs.indexOf(name);
  if (idx < 0) return { present: false, value: undefined, idx: -1, valueIdx: -1 };
  return { present: true, value: hasValue ? rawArgs[idx + 1] : true, idx, valueIdx: hasValue ? idx + 1 : idx };
}
const parallelFlag = takeFlag('--parallel');
const timeoutFlag = takeFlag('--timeout-minutes');
const watchFlag = takeFlag('--watch', false);
const mergeFlag = takeFlag('--merge', false);

const parallel = parallelFlag.present ? parseInt(parallelFlag.value, 10) : 2;
const timeoutMinutes = timeoutFlag.present ? parseInt(timeoutFlag.value, 10) : 60;
const watchOnly = watchFlag.present;
const mergeOnly = mergeFlag.present;

const consumedIdx = new Set([parallelFlag.idx, parallelFlag.valueIdx, timeoutFlag.idx, timeoutFlag.valueIdx, watchFlag.idx, mergeFlag.idx].filter((i) => i >= 0));
const positional = rawArgs.filter((_, i) => !consumedIdx.has(i));

const projectDirArg = positional[0] || process.env.DOTNET_PROJECT_DIR;
if (!projectDirArg) {
  console.error('[orchestrate] Missing project_dir: node orchestrate.mjs /path/to/solution [chapterName] [--parallel N] [--watch] [--timeout-minutes N]');
  process.exit(1);
}
const projectDir = resolve(projectDirArg);
const chapterArg = positional[1];
const kitLogDir = kitDir;
const TIMING_LOG = join(kitLogDir, 'slice-timings.jsonl');

// ── Config (read directly — same file connect/ralph-claude use) ────────
function findConfig(startDir) {
  let dir = startDir;
  while (true) {
    const candidate = join(dir, '.eventmodelers', 'config.json');
    if (existsSync(candidate)) return candidate;
    const parent = dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
}

const configPath = findConfig(kitDir);
if (!configPath) {
  console.error('[orchestrate] No .eventmodelers/config.json found in any ancestor directory. Run the connect skill first.');
  process.exit(1);
}
const cfg = JSON.parse(readFileSync(configPath, 'utf-8'));
const { token, boardId, orgId, baseUrl } = cfg;
if (!token || !boardId || !orgId || !baseUrl) {
  console.error(`[orchestrate] ${configPath} is missing one of token/boardId/orgId/baseUrl.`);
  process.exit(1);
}

// ── API helper ───────────────────────────────────────────────────────────
async function api(path, opts = {}) {
  const res = await fetch(`${baseUrl}/api/org/${orgId}/boards/${boardId}${path}`, {
    ...opts,
    headers: {
      'Content-Type': 'application/json',
      'x-token': token,
      'x-board-id': boardId,
      'x-user-id': 'orchestrate',
      ...(opts.headers || {}),
    },
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`${opts.method || 'GET'} ${path} -> ${res.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}

function prompt(question) {
  const rl = createInterface({ input: process.stdin, output: process.stdout });
  return new Promise((resolve) => rl.question(question, (answer) => { rl.close(); resolve(answer); }));
}

// ── Chapter selection ───────────────────────────────────────────────────
async function pickChapter() {
  const chapters = await api('/nodes?type=CHAPTER');
  if (chapters.length === 0) {
    console.error('[orchestrate] No chapters on this board.');
    process.exit(1);
  }

  if (chapterArg) {
    const match = chapters.find((c) => (c.meta.title || '').toLowerCase() === chapterArg.toLowerCase());
    if (!match) {
      console.error(`[orchestrate] No chapter named "${chapterArg}". Available:`);
      chapters.forEach((c) => console.error(`  - ${c.meta.title}`));
      process.exit(1);
    }
    return match;
  }

  console.log('Chapters on this board:');
  chapters.forEach((c, i) => console.log(`  ${i + 1}. ${c.meta.title || '(untitled)'}`));
  const answer = await prompt('Pick a chapter number: ');
  const idx = parseInt(answer, 10);
  if (!idx || idx < 1 || idx > chapters.length) {
    console.error('[orchestrate] Invalid selection.');
    process.exit(1);
  }
  return chapters[idx - 1];
}

// ── Retrofit: make sure every column has a slice ────────────────────────
// SLICE_BORDER nodes are hidden nodes keyed by meta.colId — no visible
// "feedback" row required (that was an older convention; some existing
// chapters still carry a legacy feedback row/lane, but the platform now
// rejects placing a SLICE_BORDER in a lane typed "feedback" — it only
// accepts MARKDOWN there). Missing borders are created via the dedicated
// slice-definitions endpoint instead, which needs only an existing column.

// Pick a slice's display title from whatever's actually in its column,
// preferring the interaction lane (COMMAND/READMODEL — what a slice is
// usually named after), then the swimlane (EVENT), then the actor lane
// (SCREEN/AUTOMATION) as a last resort for a screen-only column.
function pickSliceTitle(cellsByRow, rows, nodeMap) {
  for (const type of ['interaction', 'swimlane', 'actor']) {
    const row = rows.find((r) => r.type === type);
    const cell = row && cellsByRow[row.id];
    if (cell?.nodeId && nodeMap[cell.nodeId]) return nodeMap[cell.nodeId].meta.title || 'Untitled Slice';
  }
  return 'Untitled Slice';
}

async function ensureSlicesForChapter(chapter) {
  const fresh = await api(`/nodes/${chapter.id}`);
  const td = fresh.meta.timelineData;

  const allNodes = await api('/nodes');
  const nodeMap = Object.fromEntries(allNodes.map((n) => [n.id, n]));

  const byCol = {};
  for (const cell of td.cells) {
    byCol[cell.colId] = byCol[cell.colId] || {};
    byCol[cell.colId][cell.rowId] = cell;
  }

  const sliceBorderByCol = Object.fromEntries(
    allNodes.filter((n) => n.meta?.type === 'SLICE_BORDER' && n.meta?.colId).map((n) => [n.meta.colId, n]),
  );

  const sliceIds = [];
  for (const col of td.columns) {
    const existing = sliceBorderByCol[col.id];
    if (existing) {
      sliceIds.push(existing.id);
      continue;
    }

    const title = pickSliceTitle(byCol[col.id] || {}, td.rows, nodeMap);
    const created = await api(`/timelines/${chapter.id}/slice-definitions`, {
      method: 'POST',
      body: JSON.stringify({ columnId: col.id, title }),
    });
    console.log(`  + created slice "${title}"`);
    sliceIds.push(created.nodeId);
  }
  return sliceIds;
}

// ── Status flip ──────────────────────────────────────────────────────────
const SKIP_STATUSES = new Set(['Done', 'InProgress', 'Blocked']);

async function flipToPlanned(sliceIds) {
  const allNodes = await api('/nodes?type=SLICE_BORDER');
  const byId = Object.fromEntries(allNodes.map((n) => [n.id, n]));

  let flipped = 0, skipped = 0, planned = 0;
  for (const id of sliceIds) {
    const node = byId[id];
    const current = node?.meta?.sliceStatus || 'Created';
    if (current === 'Planned') { skipped++; planned++; continue; }
    if (SKIP_STATUSES.has(current)) {
      console.log(`  - skipping "${node?.meta?.title}" (currently ${current})`);
      skipped++;
      continue;
    }
    await api('/nodes/events', {
      method: 'POST',
      body: JSON.stringify([{
        id: randomUUID(),
        eventType: 'node:changed',
        nodeId: id,
        boardId,
        timestamp: Date.now(),
        changedAttributes: ['sliceStatus'],
        meta: { sliceStatus: 'Planned' },
      }]),
    });
    console.log(`  - "${node?.meta?.title}": ${current} -> Planned`);
    flipped++;
    planned++;
  }
  return { flipped, skipped, planned };
}

// ── Chapter scope (read by lib/ralph.js's getFirstPlannedSlice) ─────────
// The board's own "context" grouping (slicedata's contextName) puts every
// slice on this board into one flat "default" bucket — it isn't per-chapter.
// That means a Ralph loop's "first Planned slice in the current context"
// search can (and did, in practice) surface a stray Planned slice left over
// from a completely unrelated, already-shipped chapter instead of one of
// THIS chapter's slices. chapter-scope.json is the actual chapter filter:
// written before spawning instances, read by every instance (they share one
// kitDir), cleared once this chapter's run completes normally.
const CHAPTER_SCOPE_FILE = join(kitDir, '.slices', 'chapter-scope.json');

function writeChapterScope(chapterTitle, sliceIds) {
  mkdirSync(dirname(CHAPTER_SCOPE_FILE), { recursive: true });
  writeFileSync(CHAPTER_SCOPE_FILE, JSON.stringify({ chapterTitle, sliceIds }, null, 2), 'utf-8');
}

function clearChapterScope() {
  try { writeFileSync(CHAPTER_SCOPE_FILE, JSON.stringify({ sliceIds: [] }, null, 2), 'utf-8'); } catch {}
}

// ── Per-instance git worktrees ──────────────────────────────────────────
function git(args, cwd = projectDir) {
  return execFileSync('git', args, { cwd, encoding: 'utf-8' }).trim();
}

function branchExists(branch) {
  try {
    git(['rev-parse', '--verify', '--quiet', branch]);
    return true;
  } catch {
    return false;
  }
}

function worktreeDir(n) {
  return join(dirname(projectDir), `${basename(projectDir)}-ralph-${n}`);
}

// A ralph loop runs detached and can pick up a brand-new Planned slice (from
// any chapter, not just the one this orchestrate.mjs run is watching — see
// getFirstPlannedSlice's "current context" scoping in lib/ralph.js) the
// instant its previously-tracked slice goes terminal, racing against
// monitorChapter deciding the chapter is done and starting cleanup. Dropping
// this file tells the loop in that specific worktree to stop grabbing new
// planned-slice work (checked once per loop iteration — it won't interrupt
// an already-running `claude -p` call). It's a best-effort narrowing of the
// race window, not a substitute for worktreeHasUncommittedChanges() below,
// which is the actual backstop against data loss.
const RALPH_STOP_FILE = '.ralph-stop';

function signalStop(dir, reason) {
  try {
    writeFileSync(join(dir, RALPH_STOP_FILE), `${reason}\n`, 'utf-8');
  } catch (err) {
    console.error(`  ! failed to write stop signal to ${dir}: ${err.message}`);
  }
}

// git worktree remove (without --force) already refuses to touch a worktree
// with uncommitted or untracked changes — that's git protecting the caller.
// Check status ourselves first (rather than blindly retrying with --force on
// ANY failure) so we can tell an in-flight-work refusal apart from some other
// removal failure, and never destroy real work just because a Ralph instance
// happened to pick up new work in the gap between its tracked slice going
// terminal and this cleanup step running.
function worktreeHasUncommittedChanges(dir) {
  const status = execFileSync('git', ['status', '--porcelain'], { cwd: dir, encoding: 'utf-8' });
  return status.trim().length > 0;
}

// ── Static quality gate ──────────────────────────────────────────────────
// Runs the deterministic, non-LLM checks from quality-checks.md's Phase 1
// (dotnet format/build/vulnerable-package scan) here, once per worktree
// right before it merges — not per commit inside the Ralph loop's own
// PreToolUse hook (hooks/quality-gate.sh), which used to re-run all three
// on every single `git commit` attempt. That was the real throughput cost:
// a full solution build + format-check + vuln-scan, repeated per commit,
// times every parallel instance. Running it once per worktree here catches
// the same class of issue for a fraction of the wall-clock cost. The hook
// still runs on every commit for the genuinely cheap checks (secret-scan,
// stuck-loop guard) that don't touch the whole solution.
function findSln(dir) {
  try {
    const out = execFileSync('find', ['.', '-maxdepth', '2', '(', '-name', '*.sln', '-o', '-name', '*.slnx', ')'], { cwd: dir, encoding: 'utf-8' });
    const first = out.split('\n').map((s) => s.trim()).find(Boolean);
    return first || null;
  } catch {
    return null;
  }
}

function runDotnet(args, dir) {
  try {
    const out = execFileSync('dotnet', args, { cwd: dir, encoding: 'utf-8', stdio: 'pipe' });
    return { ok: true, output: out };
  } catch (err) {
    return { ok: false, output: (err.stdout || '') + (err.stderr || '') || err.message };
  }
}

function runQualityGate(dir) {
  const sln = findSln(dir);
  if (!sln) return { passed: true, failures: [] }; // nothing to check against

  const failures = [];

  const fmt = runDotnet(['format', sln, '--verify-no-changes'], dir);
  if (!fmt.ok) failures.push(`'dotnet format --verify-no-changes' found unformatted code:\n${fmt.output}`);

  const build = runDotnet(['build', sln], dir);
  if (!build.ok) failures.push(`'dotnet build' failed:\n${build.output}`);

  // dotnet list package --vulnerable exits 0 even when it finds vulnerable
  // packages — the finding is in the text output, not the exit code.
  const vuln = runDotnet(['list', sln, 'package', '--vulnerable'], dir);
  if (!vuln.ok) {
    failures.push(`'dotnet list package --vulnerable' errored:\n${vuln.output}`);
  } else if (/has the following vulnerable packages/i.test(vuln.output)) {
    failures.push(`'dotnet list package --vulnerable' found known-vulnerable packages:\n${vuln.output}`);
  }

  return { passed: failures.length === 0, failures };
}

function ensureWorktree(n, startPoint) {
  const dir = worktreeDir(n);
  const branch = `ralph/instance-${n}`;
  if (existsSync(dir)) {
    console.log(`  - worktree for instance ${n} already exists at ${dir} (reusing)`);
    return dir;
  }
  if (branchExists(branch)) {
    git(['worktree', 'add', dir, branch]);
  } else {
    git(['worktree', 'add', '-b', branch, dir, startPoint]);
  }
  console.log(`  - created worktree for instance ${n}: ${dir} (branch ${branch})`);
  return dir;
}

async function mergeWorktrees(n, targetBranch) {
  console.log(`\nMerging up to ${n} Ralph worktree branch(es) into ${targetBranch}...`);
  for (let i = 1; i <= n; i++) {
    const branch = `ralph/instance-${i}`;
    const dir = worktreeDir(i);
    if (!branchExists(branch)) {
      console.log(`  - ${branch}: no such branch, skipping`);
      continue;
    }

    console.log(`  - running static quality gate on ${dir}...`);
    const gate = runQualityGate(dir);
    if (!gate.passed) {
      console.error(`  ! quality gate failed for ${branch} — NOT merging. Fix in ${dir} (or re-run this chapter), commit, then re-run:\n      node orchestrate.mjs ${projectDir} --merge --parallel ${n}\n    (leaving this worktree/branch in place; not touching later instances)`);
      gate.failures.forEach((f) => console.error(`      - ${f.split('\n')[0]}`));
      continue;
    }
    console.log(`  - quality gate passed for ${branch}`);

    try {
      git(['merge', '--no-edit', branch]);
      console.log(`  - merged ${branch} into ${targetBranch}`);
    } catch (err) {
      console.error(`  ! merge of ${branch} failed or conflicted — resolve manually in ${projectDir}, then run:\n      git worktree remove --force "${dir}"\n      git branch -d "${branch}"\n    (leaving this worktree/branch in place for now; not touching later instances)`);
      continue;
    }
    if (existsSync(dir)) {
      if (worktreeHasUncommittedChanges(dir)) {
        console.error(`  ! ${dir} still has uncommitted/untracked changes after merging ${branch} — NOT removing it. This usually means the Ralph loop in this worktree picked up a new slice after ${branch} was merged. Inspect and clean up manually once you've confirmed nothing is lost:\n      cd "${dir}" && git status\n      git add -A && git commit   # if the new work should be kept\n      git worktree remove --force "${dir}"\n      git branch -d "${branch}"\n    (leaving this worktree/branch in place; not touching later instances)`);
        continue;
      }
      try {
        git(['worktree', 'remove', dir]);
      } catch (err) {
        console.error(`  ! ${dir} reported clean but "git worktree remove" still failed (${err.message}) — leaving it in place rather than force-removing blindly. Investigate manually.`);
        continue;
      }
    }
    try {
      git(['branch', '-d', branch]);
    } catch {}
    console.log(`  - cleaned up worktree ${dir} and branch ${branch}`);
  }
}

// ── Spawn Ralph instances ───────────────────────────────────────────────
function spawnRalph(n, instanceProjectDir) {
  const logPath = join(kitDir, `ralph-${n}.log`);
  const out = openSync(logPath, 'a');
  const child = spawn('node', [join(kitDir, 'ralph-claude.js'), instanceProjectDir], {
    detached: true,
    stdio: ['ignore', out, out],
  });
  child.unref();
  console.log(`  - Ralph instance ${n}: pid ${child.pid}, worktree ${instanceProjectDir}, logging to ${logPath}`);
  return child.pid;
}

// ── Timing tracker ───────────────────────────────────────────────────────
// Tracks each slice's InProgress -> terminal-status wall-clock time by
// polling the board (same cadence Ralph itself polls at, per AGENT.md —
// no separate instrumentation needed inside ralph.js/ralph-claude.js).
// Writes one JSONL line per slice that reaches a terminal state to
// slice-timings.jsonl, and prints a running log plus a final summary
// table. Exits once every tracked slice is terminal (Done/Blocked), or
// after `timeoutMinutes` if some are still stuck.
const POLL_INTERVAL_MS = 15000;
const TERMINAL_STATUSES = new Set(['Done', 'Blocked']);

function fmtDuration(ms) {
  const s = Math.round(ms / 1000);
  const m = Math.floor(s / 60);
  const rem = s % 60;
  return m > 0 ? `${m}m ${rem}s` : `${rem}s`;
}

async function monitorChapter(chapter, sliceIds) {
  const tracked = new Map(sliceIds.map((id) => [id, { status: null, inProgressAt: null, doneAt: null }]));
  const startedAt = Date.now();
  let completed = false;
  console.log(`\nWatching ${sliceIds.length} slice(s) in "${chapter.meta.title}" (polling every ${POLL_INTERVAL_MS / 1000}s, ${timeoutMinutes}m timeout)...`);

  while (true) {
    const nodes = await api('/nodes?type=SLICE_BORDER');
    const byId = Object.fromEntries(nodes.map((n) => [n.id, n]));

    for (const [id, t] of tracked) {
      const node = byId[id];
      const status = node?.meta?.sliceStatus || 'Created';
      if (status === t.status) continue; // no change since last poll

      const title = node?.meta?.title || '(untitled)';
      if (status === 'InProgress' && !t.inProgressAt) {
        t.inProgressAt = Date.now();
        console.log(`  -> "${title}" started (InProgress)`);
      }
      if (TERMINAL_STATUSES.has(status) && !t.doneAt) {
        t.doneAt = Date.now();
        const durationMs = t.inProgressAt ? t.doneAt - t.inProgressAt : null;
        const durationLabel = durationMs !== null ? fmtDuration(durationMs) : 'unknown (never saw InProgress)';
        console.log(`  <- "${title}" reached ${status} in ${durationLabel}`);
        appendFileSync(TIMING_LOG, JSON.stringify({
          chapter: chapter.meta.title,
          slice: title,
          sliceId: id,
          status,
          inProgressAt: t.inProgressAt ? new Date(t.inProgressAt).toISOString() : null,
          terminalAt: new Date(t.doneAt).toISOString(),
          durationSeconds: durationMs !== null ? Math.round(durationMs / 1000) : null,
        }) + '\n');
      }
      t.status = status;
    }

    const allTerminal = [...tracked.values()].every((t) => t.doneAt);
    if (allTerminal) {
      console.log('\nAll slices in this chapter reached a terminal state.');
      completed = true;
      break;
    }
    if (Date.now() - startedAt > timeoutMinutes * 60 * 1000) {
      const stillGoing = [...tracked.entries()].filter(([, t]) => !t.doneAt).map(([id]) => byId[id]?.meta?.title || id);
      console.log(`\nTimeout (${timeoutMinutes}m) reached with ${stillGoing.length} slice(s) still not terminal: ${stillGoing.join(', ')}`);
      console.log('Ralph instance(s) are still running in the background — this is just the watch loop giving up, not stopping them.');
      completed = false;
      break;
    }
    await new Promise((r) => setTimeout(r, POLL_INTERVAL_MS));
  }

  const summary = [...tracked.values()]
    .filter((t) => t.doneAt && t.inProgressAt)
    .map((t) => t.doneAt - t.inProgressAt);
  if (summary.length > 0) {
    const avg = summary.reduce((a, b) => a + b, 0) / summary.length;
    console.log(`\nSummary: ${summary.length} slice(s) timed, average ${fmtDuration(avg)}. Full history in ${TIMING_LOG}.`);
  }
  return { completed };
}

// ── Main ─────────────────────────────────────────────────────────────────
async function main() {
  if (mergeOnly) {
    const startBranch = git(['rev-parse', '--abbrev-ref', 'HEAD']);
    await mergeWorktrees(parallel, startBranch);
    return;
  }

  const chapter = await pickChapter();
  console.log(`\nChapter: ${chapter.meta.title}`);

  console.log('\nEnsuring every column has a slice...');
  const sliceIds = await ensureSlicesForChapter(chapter);
  console.log(`${sliceIds.length} slice(s) in this chapter.`);

  if (watchOnly) {
    console.log('\n--watch: skipping retrofit/flip/spawn, monitoring only.');
    await monitorChapter(chapter, sliceIds);
    return;
  }

  console.log('\nFlipping status to Planned...');
  const { flipped, skipped, planned } = await flipToPlanned(sliceIds);
  console.log(`${flipped} flipped to Planned, ${skipped} skipped (already Planned, or Done/InProgress/Blocked).`);

  // Spawn Ralph whenever there's anything sitting Planned for this chapter
  // — whether it was just flipped this run or already Planned from an
  // earlier run (e.g. a prior dry run with --parallel 0).
  if (planned === 0) {
    console.log('\nNo Planned slices in this chapter — not starting any Ralph instances.');
    return;
  }

  writeChapterScope(chapter.meta.title, sliceIds);
  console.log(`\nWrote chapter-scope.json (${sliceIds.length} slice id(s)) — Ralph instances will only pick up Planned slices from this chapter.`);

  const startBranch = git(['rev-parse', '--abbrev-ref', 'HEAD']);
  console.log(`\nStarting ${parallel} Ralph instance(s), each in its own worktree off "${startBranch}"...`);
  // Stagger spawns — starting two instances at the exact same instant
  // races their realtime-channel token exchange against each other and
  // one of them 500s ("failed to exchange agent token for session").
  // Empirically confirmed: a few seconds' gap avoids it entirely.
  for (let i = 1; i <= parallel; i++) {
    const wtDir = ensureWorktree(i, startBranch);
    spawnRalph(i, wtDir);
    if (i < parallel) await new Promise((r) => setTimeout(r, 5000));
  }

  const { completed } = await monitorChapter(chapter, sliceIds);
  if (completed) {
    for (let i = 1; i <= parallel; i++) {
      const dir = worktreeDir(i);
      if (existsSync(dir)) signalStop(dir, `chapter "${chapter.meta.title}" completed at ${new Date().toISOString()} — orchestrator is merging this worktree, not picking up new work`);
    }
    await mergeWorktrees(parallel, startBranch);
    clearChapterScope();
  } else {
    console.log(`\nNot merging worktree branches yet — the Ralph instance(s) are still running (this is just the watch loop giving up after ${timeoutMinutes}m). Re-run with --watch to resume monitoring, or with --merge once you've stopped them, to merge whatever they finished.`);
  }
  console.log('\norchestrate.mjs exiting — any still-running Ralph instance(s) keep going in the background regardless (they are detached processes, not children of this script). Check ralph-1.log / ralph-2.log directly.');
}

main().catch((err) => {
  console.error('[orchestrate] Fatal:', err.message);
  process.exit(1);
});
