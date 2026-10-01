// Common runtime for the ralph loop + realtime agent.
// Not meant to be run directly — use ralph-claude.js or ralph-ollama.js.
//
// startRalph({ kitDir, projectDir, onTask, onPlannedSlice })
//   onTask(prompt) — called when tasks.json has entries
//   onPlannedSlice(prompt) — called when .slices/ has a "Planned" entry (omit to skip)

import { createClient } from '@supabase/supabase-js';
import { readFileSync, mkdirSync, writeFileSync, existsSync, readdirSync } from 'fs';
import { join, dirname } from 'path';
import { randomUUID } from 'crypto';

// ── HTTP helpers ──────────────────────────────────────────────────────────────

class HttpError extends Error {
  constructor(status, body) {
    super(`HTTP ${status}: ${body}`);
    this.status = status;
  }
}

async function fetchJSON(url, options) {
  const res = await fetch(url, options);
  if (!res.ok) throw new HttpError(res.status, await res.text());
  return res.json();
}

async function retryOn401(label, fn, maxRetries = 3) {
  for (let attempt = 1; attempt <= maxRetries; attempt++) {
    try {
      return await fn();
    } catch (err) {
      if (err instanceof HttpError && err.status === 401) {
        if (attempt < maxRetries) {
          console.warn(`[agent] ${label} — 401, retrying (${attempt}/${maxRetries})...`);
          continue;
        }
        console.error(`[agent] ${label} — 401 after ${maxRetries} retries, shutting down`);
        process.exit(1);
      }
      throw err;
    }
  }
}

// ── Config ────────────────────────────────────────────────────────────────────

// Config is resolved by walking from the kit dir up through every ancestor
// directory's .eventmodelers/config.json, merging fields as we go — a value
// set by a closer (more specific) directory always wins over a farther one.
// The walk stops as soon as the merged config has full connection credentials
// (see hasCredentials); anthropicBaseUrl/model are picked up opportunistically
// along the way but never force the walk to continue further up.
function* configCandidates(kitDir) {
  yield join(kitDir, '.eventmodelers', 'config.json');
  let dir = dirname(kitDir);
  while (true) {
    yield join(dir, '.eventmodelers', 'config.json');
    const parent = dirname(dir);
    if (parent === dir) return;
    dir = parent;
  }
}

function loadLocalConfig(kitDir) {
  const merged = {};
  const sources = [];

  for (const candidate of configCandidates(kitDir)) {
    if (!existsSync(candidate)) continue;
    let cfg;
    try {
      cfg = JSON.parse(readFileSync(candidate, 'utf-8'));
    } catch {
      console.warn(`[ralph] Skipping invalid config at ${candidate}`);
      continue;
    }
    for (const [key, value] of Object.entries(cfg)) {
      if (merged[key] === undefined) merged[key] = value;
    }
    sources.push(candidate);
    if (hasCredentials(merged)) break;
  }

  if (process.env.BASE_URL) merged.baseUrl = process.env.BASE_URL;

  if (sources.length > 1) {
    console.log(`[ralph] Merged config from: ${sources.join(', ')}`);
  } else if (sources.length === 1 && sources[0] !== join(kitDir, '.eventmodelers', 'config.json')) {
    console.log(`[ralph] Using credentials from ${sources[0]}`);
  } else if (sources.length === 0) {
    console.warn(`[ralph] Note: no .eventmodelers/config.json found — platform sync disabled.`);
    console.warn(`        To enable board sync, follow: https://app.eventmodelers.ai/documentation#build-node`);
    console.warn(`        Code generation from local slice definitions will still run.`);
  }

  return merged;
}

function hasCredentials(cfg) {
  return !!(cfg.token && cfg.organizationId && cfg.boardId && cfg.baseUrl);
}

async function fetchPlatformConfig(local) {
  const remote = await fetchJSON(`${local.baseUrl}/api/config`, {
    headers: { 'x-token': local.token },
  });
  return { ...local, ...remote };
}

// ── Realtime agent ────────────────────────────────────────────────────────────

async function getRealtimeToken(cfg) {
  const { token } = await fetchJSON(
    `${cfg.baseUrl}/api/org/${cfg.organizationId}/prompts/realtime-token`,
    { headers: { 'x-token': cfg.token } },
  );
  return token;
}

function slugify(str) {
  return str.toLowerCase().replace(/\s+/g, '-').replace(/[^a-z0-9-]/g, '');
}

async function fetchAndPersistSlices(cfg, kitDir) {
  const url = `${cfg.baseUrl}/api/org/${cfg.organizationId}/boards/${cfg.boardId}/slicedata/slices`;
  const { slices } = await fetchJSON(url, {
    headers: { 'x-token': cfg.token, 'x-board-id': cfg.boardId },
  });
  const slicesDir = join(kitDir, '.slices');
  mkdirSync(slicesDir, { recursive: true });

  // Group by context slug
  const contexts = {};
  for (const slice of slices) {
    const contextSlug = slice.contextName ? slugify(slice.contextName) : 'default';
    if (!contexts[contextSlug]) contexts[contextSlug] = { name: slice.contextName || 'default', slices: [] };
    contexts[contextSlug].slices.push(slice);
  }

  // current_context.json is STICKY. We work within ONE context at a time and must
  // not auto-jump to another context just because it happens to have planned work.
  // Keep the existing context if it still exists; only seed it when absent or stale.
  const ctxPath = join(slicesDir, 'current_context.json');
  let activeCtx = null;
  if (existsSync(ctxPath)) {
    try { activeCtx = JSON.parse(readFileSync(ctxPath, 'utf-8')).name; } catch {}
  }
  if (!activeCtx || !contexts[activeCtx]) {
    // First run (or the current context disappeared): seed with a context that
    // has planned work, else the first one. This is the ONLY place we choose it.
    const plannedCtx = Object.keys(contexts).find(c => contexts[c].slices.some(s => (s.status || '').toLowerCase() === 'planned'));
    activeCtx = plannedCtx || Object.keys(contexts)[0] || 'default';
    writeFileSync(ctxPath, JSON.stringify({ name: activeCtx }, null, 2), 'utf-8');
  }

  // Write per-context index.json and per-slice slice.json
  for (const [contextSlug, { slices: ctxSlices }] of Object.entries(contexts)) {
    const contextDir = join(slicesDir, contextSlug);
    mkdirSync(contextDir, { recursive: true });

    const indexSlices = ctxSlices.map((s, i) => {
      const folder = (s.title ?? s.id).replaceAll(' ', '').toLowerCase();
      return {
        id: s.id,
        slice: s.title,
        index: i,
        contextName: s.contextName || contextSlug,
        contextSlug,
        folder,
        status: s.status,
        definition: { id: s.id, title: s.title, status: s.status },
      };
    });
    writeFileSync(join(contextDir, 'index.json'), JSON.stringify({ slices: indexSlices }, null, 2), 'utf-8');

    for (const slice of ctxSlices) {
      const folder = (slice.title ?? slice.id).replaceAll(' ', '').toLowerCase();
      const sliceDir = join(contextDir, folder);
      mkdirSync(sliceDir, { recursive: true });
      writeFileSync(join(sliceDir, 'slice.json'), JSON.stringify(slice, null, 2), 'utf-8');
    }
  }

  console.log(`[agent] Persisted ${slices.length} slice(s)`);
}

async function writeTask(payload, kitDir) {
  const tasksPath = join(kitDir, 'tasks.json');
  const existing = existsSync(tasksPath) ? JSON.parse(readFileSync(tasksPath, 'utf-8')) : [];
  const filtered = existing.filter(t => t.payload?.sliceId !== payload.sliceId);
  const task = { id: randomUUID(), createdAt: new Date().toISOString(), payload };
  filtered.push(task);
  writeFileSync(tasksPath, JSON.stringify(filtered, null, 2), 'utf-8');
  console.log(`[agent] Task written — slice="${payload.sliceTitle}" status="${payload.sliceStatus}"`);
}

async function startRealtimeAgent(cfg, kitDir) {
  let realtimeToken = await retryOn401('getRealtimeToken', () => getRealtimeToken(cfg));

  await retryOn401('fetchAndPersistSlices', () => fetchAndPersistSlices(cfg, kitDir)).catch((err) =>
    console.error('[agent] Initial slice fetch error:', err),
  );

  const supabase = createClient(cfg.supabaseUrl, cfg.supabaseAnonKey, {
    realtime: { params: { apikey: cfg.supabaseAnonKey } },
  });
  await supabase.realtime.setAuth(realtimeToken);

  const channelName = `board:${cfg.boardId}-slicechanged`;

  supabase
    .channel(channelName, { config: { private: true } })
    .on('broadcast', { event: 'message' }, (msg) => {
      if (msg.payload === 'Exit') {
        console.log('[agent] Received "Exit" — shutting down');
        process.exit(0);
      }
    })
    .on('broadcast', { event: 'slice:changed' }, async (msg) => {
      const payload = msg.payload;
      console.log(`[agent] slice:changed — slice="${payload.sliceTitle}" status="${payload.sliceStatus}"`);
      await retryOn401('fetchAndPersistSlices', () => fetchAndPersistSlices(cfg, kitDir)).catch((err) =>
        console.error('[agent] Slice persist error:', err),
      );
      // Planned slices are handled by onPlannedSlice directly — no task needed
      if ((payload.sliceStatus || '').toLowerCase() !== 'planned') {
        await writeTask(payload, kitDir).catch((err) => console.error('[agent] writeTask error:', err));
      }
    })
    .subscribe((status) => console.log(`[agent] Channel "${channelName}": ${status}`));

  setInterval(async () => {
    try {
      realtimeToken = await retryOn401('getRealtimeToken (refresh)', () => getRealtimeToken(cfg));
      supabase.realtime.setAuth(realtimeToken);
      console.log('[agent] Token refreshed');
    } catch (err) {
      console.error('[agent] Token refresh failed:', err);
    }
  }, 10 * 60 * 1000);

  const ping = async () => {
    try {
      const res = await fetch(`${cfg.baseUrl}/api/agent-alive`, {
        method: 'POST',
        headers: { Authorization: `Bearer ${realtimeToken}`, 'Content-Type': 'application/json' },
        body: JSON.stringify({ token: cfg.token }),
      });
      if (!res.ok) console.error(`[agent] Ping failed: ${res.status}`);
    } catch (err) {
      console.error('[agent] Ping error:', err);
    }
  };
  await ping();
  setInterval(ping, 30_000);
}

// ── Ralph loop ────────────────────────────────────────────────────────────────

function hasPendingTasks(kitDir) {
  const tasksPath = join(kitDir, 'tasks.json');
  if (!existsSync(tasksPath)) return false;
  try {
    const tasks = JSON.parse(readFileSync(tasksPath, 'utf-8'));
    return Array.isArray(tasks) && tasks.length > 0;
  } catch {
    return false;
  }
}

function readCurrentContext(kitDir) {
  const ctxPath = join(kitDir, '.slices', 'current_context.json');
  if (!existsSync(ctxPath)) return null;
  try { return JSON.parse(readFileSync(ctxPath, 'utf-8')).name || null; } catch { return null; }
}

// Dropped by orchestrate.mjs (in the worktree it passes as projectDir) once
// it decides a chapter is complete and is about to merge+remove that
// worktree — tells this loop to stop grabbing new Planned slices so it can't
// leave fresh uncommitted work sitting in a worktree that's about to be
// cleaned up. Doesn't interrupt a `claude -p` call already in flight; the
// orchestrator's own git-status check before removal is the real backstop.
const RALPH_STOP_FILE = '.ralph-stop';

// Written by orchestrate.mjs before it spawns Ralph instances for a chapter
// (all instances share this kitDir). The board's own "context" grouping
// (slicedata's contextName) is NOT per-chapter — on this board every slice
// comes back with contextName "default", so "current context" alone doesn't
// stop a loop from grabbing a stray Planned slice left over from some other,
// already-shipped chapter. This file is the real per-chapter filter; absent
// (e.g. running ralph-claude.js standalone, with no orchestrate.mjs) means
// no filtering, preserving the old behavior.
function readChapterScope(kitDir) {
  const scopePath = join(kitDir, '.slices', 'chapter-scope.json');
  if (!existsSync(scopePath)) return null;
  try {
    const { sliceIds } = JSON.parse(readFileSync(scopePath, 'utf-8'));
    return Array.isArray(sliceIds) && sliceIds.length ? new Set(sliceIds) : null;
  } catch {
    return null;
  }
}

// Returns the first Planned slice IN THE CURRENT CONTEXT ONLY, further
// restricted to chapter-scope.json when orchestrate.mjs has written one. If
// the current context (post-filter) has no planned work, returns null so the
// loop waits — it must NEVER cross into another context, or outside the
// active chapter scope, to find something to build.
function getFirstPlannedSlice(kitDir) {
  const currentCtx = readCurrentContext(kitDir);
  if (!currentCtx) return null;
  const indexPath = join(kitDir, '.slices', currentCtx, 'index.json');
  if (!existsSync(indexPath)) return null;
  try {
    const { slices } = JSON.parse(readFileSync(indexPath, 'utf-8'));
    const scope = readChapterScope(kitDir);
    const candidates = scope ? (slices || []).filter((s) => scope.has(s.id)) : slices;
    const planned = candidates && candidates.find((s) => (s.status || '').toLowerCase() === 'planned');
    if (planned) return { id: planned.id || null, title: planned.slice || planned.id || null };
  } catch {}
  return null;
}

// Atomically claims a candidate slice by writing InProgress directly,
// instead of just reading its status — closes the window a read-only check
// can't: several Ralph instances restarting at the same instant each get a
// local index.json snapshot that (correctly, at that moment) shows the same
// slice as Planned, and a plain read-check can't tell them apart since none
// has claimed it yet. The board enforces this atomically server-side (see
// the update-slice-status skill): a write to a status the slice is already
// in is rejected — that rejection IS the concurrency guard, not an error.
//
// Returns 'claimed', 'conflict' (someone else got there first — the caller
// must not build it), or 'unknown' (the check itself errored, e.g. a network
// blip — fails open by letting the caller fall back to cached status, so
// backend flakiness can't stall the loop forever; backend-prompt.md's own
// Step 4 claim-and-defer handling remains the backstop in that case).
async function claimPlannedSlice(cfg, sliceId) {
  const url = `${cfg.baseUrl}/api/org/${cfg.organizationId}/boards/${cfg.boardId}/nodes/events`;
  try {
    await fetchJSON(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'x-token': cfg.token, 'x-board-id': cfg.boardId },
      body: JSON.stringify([{
        id: randomUUID(),
        eventType: 'node:changed',
        nodeId: sliceId,
        boardId: cfg.boardId,
        timestamp: Date.now(),
        changedAttributes: ['sliceStatus'],
        meta: { sliceStatus: 'InProgress' },
      }]),
    });
    return 'claimed';
  } catch (err) {
    if (err instanceof HttpError && (err.status === 409 || /already/i.test(err.message))) {
      return 'conflict';
    }
    console.error('[ralph] Claim attempt failed — proceeding on cached status:', err.message);
    return 'unknown';
  }
}

// Marks a SLICE_BORDER node Blocked directly on the board — used when a slice
// exhausts its retry budget below, so it drops out of the Planned queue
// instead of being picked up again on the next loop iteration.
async function markSliceBlocked(cfg, sliceId, reason) {
  // The failing attempt may have completed the actual work and marked the
  // slice Done just before some unrelated, later step (e.g. the CLI process
  // itself) failed non-zero — don't clobber that with Blocked.
  const nodeUrl = `${cfg.baseUrl}/api/org/${cfg.organizationId}/boards/${cfg.boardId}/nodes/${sliceId}`;
  const node = await fetchJSON(nodeUrl, { headers: { 'x-token': cfg.token, 'x-board-id': cfg.boardId } }).catch(() => null);
  if (node?.meta?.sliceStatus === 'Done') {
    console.log(`[ralph] Slice ${sliceId} already Done — not marking Blocked`);
    return;
  }

  const url = `${cfg.baseUrl}/api/org/${cfg.organizationId}/boards/${cfg.boardId}/nodes/events`;
  await fetchJSON(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'x-token': cfg.token, 'x-board-id': cfg.boardId },
    body: JSON.stringify([{
      id: randomUUID(),
      eventType: 'node:changed',
      nodeId: sliceId,
      boardId: cfg.boardId,
      timestamp: Date.now(),
      changedAttributes: ['sliceStatus'],
      meta: { sliceStatus: 'Blocked' },
    }]),
  });
  console.error(`[ralph] Marked slice ${sliceId} Blocked after repeated failures: ${reason}`);
}

// Retries fn up to maxAttempts times, 60s apart. A failure that keeps
// recurring (an underspecified slice, a budget cap that will never be met)
// used to retry forever here — this caps it, matching the give-up-after-3
// convention ralph.sh's bash loop already uses for onTask failures. onGiveUp
// (if provided) runs once, after the final attempt, so the caller can mark
// the underlying board state instead of leaving it stuck in Planned forever.
async function runWithRetry(label, fn, { maxAttempts = 3, onGiveUp } = {}) {
  for (let attempt = 1; attempt <= maxAttempts; attempt++) {
    try {
      console.log(`[ralph] ${label}`);
      await fn();
      return;
    } catch (err) {
      if (attempt >= maxAttempts) {
        console.error(`[ralph] Error — giving up after ${maxAttempts} attempt(s):`, err.message);
        if (onGiveUp) await onGiveUp(err).catch((giveUpErr) => console.error('[ralph] onGiveUp failed:', giveUpErr.message));
        return;
      }
      console.error(`[ralph] Error — retrying in 60s (${attempt}/${maxAttempts}):`, err.message);
      await new Promise((r) => setTimeout(r, 60_000));
    }
  }
}

async function ralphLoop(kitDir, projectDir, cfg, onTask, onPlannedSlice) {
  const promptFile = join(kitDir, 'lib', 'prompt.md');
  const backendPromptFile = join(kitDir, 'lib', 'backend-prompt.md');
  const credentialed = hasCredentials(cfg);
  let lastIdleCtx;
  let stopLogged = false;
  let slicesBuilt = 0;
  const maxSlicesPerRun = cfg.maxSlicesPerRun ? parseInt(cfg.maxSlicesPerRun, 10) : null;

  while (true) {
    let didWork = false;

    if (credentialed && hasPendingTasks(kitDir)) {
      // The prompt file references the kit by the bare relative name
      // "build-kit-dotnet-es/..." — only correct if the kit happens to live
      // inside the executor's cwd (projectDir). It doesn't in general (this
      // kit is meant to sit alongside, not inside, the target solution), so
      // resolve it to the kit's real absolute path before handing the
      // prompt to the executor. Without this, the executor sometimes reads
      // its own cwd literally, concludes the kit "isn't present", and bails
      // with NO_TASKS — a full paid iteration wasted on nothing.
      const prompt = readFileSync(promptFile, 'utf-8').replaceAll('build-kit-dotnet-es', kitDir);
      await runWithRetry('onTask: loading slice from board...', () => onTask(prompt));
      await fetchAndPersistSlices(cfg, kitDir).catch(() => {});
      didWork = true;
    }

    const stopSignaled = existsSync(join(projectDir, RALPH_STOP_FILE));
    if (stopSignaled && !stopLogged) {
      console.log(`[ralph] Stop signal found at ${join(projectDir, RALPH_STOP_FILE)} — orchestrator is cleaning up this worktree, not picking up new planned slices.`);
      stopLogged = true;
    }
    let planned = !stopSignaled && onPlannedSlice && getFirstPlannedSlice(kitDir);
    let preClaimed = false;
    if (planned && planned.id && credentialed) {
      const outcome = await claimPlannedSlice(cfg, planned.id);
      if (outcome === 'conflict') {
        console.log(`[ralph] "${planned.title}" was claimed elsewhere since the last sync — refreshing and re-checking.`);
        await fetchAndPersistSlices(cfg, kitDir).catch(() => {});
        planned = null;
      } else if (outcome === 'claimed') {
        preClaimed = true;
        await fetchAndPersistSlices(cfg, kitDir).catch(() => {});
      }
      // 'unknown' (claim check itself errored) — proceed unclaimed on cached
      // status; backend-prompt.md's own Step 4 claim-and-defer is the backstop.
    }
    if (planned) {
      const basePrompt = readFileSync(backendPromptFile, 'utf-8').replaceAll('build-kit-dotnet-es', kitDir);
      // A pre-claimed slice MUST be named explicitly — backend-prompt.md's
      // Step 4 otherwise re-derives its own "highest priority Planned slice"
      // from scratch, which (now that ours is InProgress) would be a
      // DIFFERENT slice than the one just claimed above, orphaning the
      // claimed one at InProgress forever with nobody actually building it.
      const prompt = preClaimed
        ? `## Pre-claimed slice (added by ralph.js — read this before Step 4)\n\n` +
          `This iteration already claimed **"${planned.title}"** (id \`${planned.id}\`) by setting its board status to InProgress, to prevent two Ralph instances racing to build the same slice.\n\n` +
          `- Build exactly this slice. Skip the Planned-scan-and-claim part of Step 4 below — look this slice up by id/title in \`index.json\` for its \`folder\`, then proceed from Step 5 (load \`slice.json\`, implement, test, commit, mark Done) as normal.\n` +
          `- Do not call \`update-slice-status\` to claim it again — it is already InProgress. If you do and it reports "already in status", that's expected — ignore it and continue.\n` +
          `- Only if this slice is missing, or already Done/Blocked by the time you look (a rare late race): fall back to Step 4's normal Planned-scan (still restricted to chapter-scope.json when present) and build whatever else is Planned instead. Do not touch this slice's status further either way — it's already accounted for.\n\n` +
          `---\n\n${basePrompt}`
        : basePrompt;
      await runWithRetry(`onPlannedSlice: building slice "${planned.title}"...`, () => onPlannedSlice(prompt), {
        onGiveUp: planned.id
          ? () => markSliceBlocked(cfg, planned.id, `onPlannedSlice failed repeatedly while building "${planned.title}"`)
          : undefined,
      });
      console.log(`[ralph] Slice build complete — waiting for next slice`);
      slicesBuilt++;
      if (credentialed) await fetchAndPersistSlices(cfg, kitDir).catch(() => {});
      didWork = true;

      if (maxSlicesPerRun && slicesBuilt >= maxSlicesPerRun) {
        console.log(`[ralph] Reached max slices per run (${maxSlicesPerRun}) — stopping this instance now. Restart it (via orchestrate.mjs or ralph-claude.js directly), or raise maxSlicesPerRun in .eventmodelers/config.json, to keep going.`);
        // process.exit rather than return: startRalph() runs this loop
        // alongside startRealtimeAgent() via Promise.all, which never
        // resolves on its own — returning here would leave the process
        // hanging instead of actually stopping it.
        process.exit(0);
      }
    }

    if (!didWork) {
      // No planned work in the current context — wait, do NOT switch contexts.
      const ctx = readCurrentContext(kitDir);
      if (ctx !== lastIdleCtx) {
        console.log(`[ralph] No planned slices in current context "${ctx}" — waiting. Switch context on the board to continue.`);
        lastIdleCtx = ctx;
      }
      await new Promise((r) => setTimeout(r, 10_000));
    } else {
      lastIdleCtx = undefined;
    }
  }
}

// ── Public API ────────────────────────────────────────────────────────────────

export { loadLocalConfig, fetchPlatformConfig, retryOn401, startRealtimeAgent };

export async function startRalph({ kitDir, projectDir, onTask, onPlannedSlice }) {
  const local = loadLocalConfig(kitDir);

  console.log(`Ralph — kit: ${kitDir}`);
  console.log(`         project: ${projectDir}`);

  if (!hasCredentials(local)) {
    console.log(`         mode: local-only (no platform sync)\n`);
    await ralphLoop(kitDir, projectDir, local, onTask, onPlannedSlice);
    return;
  }

  const cfg = await retryOn401('fetchPlatformConfig', () => fetchPlatformConfig(local));
  console.log(`         org=${cfg.organizationId}, board=${cfg.boardId}, base=${cfg.baseUrl}\n`);

  await Promise.all([
    startRealtimeAgent(cfg, kitDir),
    ralphLoop(kitDir, projectDir, cfg, onTask, onPlannedSlice),
  ]);
}
