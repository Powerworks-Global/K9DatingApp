# Agent Learnings

Patterns and gotchas discovered during task processing. Update this file whenever you encounter something reusable.

## tasks.json

- Tasks are objects with `id`, `createdAt`, and `payload` (a `SliceChangedPayload`).
- After completing a task, remove it from the array entirely — do not add a status field.
- Write `[]` to `tasks.json` if the last task is completed.

## SliceChangedPayload fields

```
event           always "slice:changed"
organizationId  org UUID or null
boardId         board UUID
sliceId         SLICE_BORDER node UUID — use this with load-slice
sliceTitle      human-readable slice name (may be null)
sliceStatus     e.g. "Created", "InProgress", "Done", "Blocked" (may be null)
timestamp       unix ms when the change was emitted
```

## Slice files

Ralph (and the `load-slice` skill) write one file per slice, refreshed on every board poll:

```
.slices/<context>/<sliceName>.json
```

- `<context>` is the slice's context value, or `default` if none.
- `<sliceName>` (the folder) — Ralph's own polling loop uses spaces-removed-lowercased (no "slice:" prefix stripped); the `load-slice` skill additionally strips a leading `"slice:"` prefix. If a slice title happens to start with `slice:`, the two tools will compute slightly different folder names for it — check both if a slice folder seems to be missing.
- `current_context.json`'s `"name"` field holds the **context slug** (e.g. `"discovery"`), not the display context name (`"Discovery"`) — it's written from the same dictionary key used to name the `.slices/<contextSlug>/` directory, and both the Ralph loop and the skills look it up directly as a folder name. Writing the display name there instead would silently break "no planned slice found" on any context whose name isn't already all-lowercase with no spaces (case-sensitive filesystem lookup miss). Confirmed while hand-authoring a proof-of-concept slice cache for `UndoLastSwipe`.

These files are refreshed on every poll (roughly every 15s while Ralph is running with credentials) — read them directly before invoking any skill.

## Skill Usage

- Always run `connect` first to load credentials from `.eventmodelers/config.json` (repo root) before calling any other skill.
- `load-slice sliceId=<uuid>` re-fetches all slices from the API, refreshes the slice files, and returns the requested slice. Use it when you need a guaranteed-fresh view of a specific slice.
- Read `.slices/<context>/<sliceName>.json` directly when you already know the context and name and the file is recent enough.

## Board API

- The `boardId` and `organizationId` from each payload provide full context — pass them to skills.
- Node events use `node:created`, `node:changed`, `node:deleted` — always POST to `/api/org/:orgId/boards/:boardId/nodes/events`.
- Slice metadata (title, status) lives on the SLICE_BORDER node under `meta.sliceStatus` and `meta.title`.
- `update-slice-status` rejects moving a slice into a status it's already in — this is a concurrency guard, not a bug. It means another agent already claimed the slice. Treat it as `ALREADY_IN_STATUS`, skip that slice, and move on to the next `Planned` one instead of erroring out.

## .NET / Wolverine / Marten specifics (not present in the node/emmett build-kit)

- Any class with a public static `Handle` method must be named `*Handler`, even when the file itself is named after a trigger event (a projector). Wolverine's convention-based discovery silently skips anything else — this has broken a real slice before (`DogProfileCreatedProjector` → had to become `DogProfileCreatedProjectorHandler`).
- `IDocumentSession.Store(...)` only stages a change — always call `SaveChangesAsync` explicitly, especially in projectors/automations reacting to an event, where it's easy to forget since the handler "did work" without it.
- Validation is DataAnnotations + `IValidatableObject` on the request record, wired centrally via `UseDataAnnotationsValidationProblemDetailMiddleware()` — never a separate FluentValidation class; it silently never runs against `[WolverineGet]`/`[WolverinePost]` endpoints.
- A module consuming a cross-module integration event for the first time needs `IntegrationEventQueueName` set on its `<Context>Module.cs`, or the published event has nothing bound to receive it and is silently dropped.
- Document entities with a private constructor/setters need `[JsonConstructor]`/`[JsonInclude]` or Marten's serializer throws `NotSupportedException` on the first real read (write path works fine either way — this is a read-path-only bug, easy to miss until a GET actually exercises it).
- Per ADR-019, command/automation state needing stream history is computed live via `AggregateStreamAsync<T>`, never a persisted/shared snapshot — a new `[CommandName]State`/`[AutomationName]State` type per handler, never reused across handlers.
- No SQL/Flyway migration files for application schema — Marten auto-manages it (`AutoCreateSchemaObjects`). Only `build-state-view`'s Testcontainers fixtures need `AutoCreate.All` explicitly set, since Development's default is what production currently relies on too.
- **DCB (Dynamic Consistency Boundary) is real in Marten 9.20.1/Wolverine 6.0 but has an unresolved registration gap**: `FetchForWritingByTags<T>` throws `InvalidProjectionException: No source-generated dispatcher found` for a purpose-built DCB boundary type even with correct `Apply` methods — the `JasperFx.Events.SourceGenerator` produces zero output for the whole project in at least one real build (confirmed via `EmitCompilerGeneratedFiles=true`), narrowed to a generator activation/visibility question, not a wrong-shape problem. A custom `IProjection` was tried and ruled out — it's for persisted read-model documents, not the in-memory state `FetchForWritingByTags` builds. Don't re-attempt either dead end; see `poc/DcbRewrite/NOTES.md` in `github.com/Powerworks/internal-modernization-accelerator` (branch `poc/dcb-modernization`) for the full investigation and the working (non-DCB-registration) parts of the rewrite on `poc/dcb-modernization-approve-application` in this repo.

## OpenCode routing

If driving this loop via OpenCode rather than Claude Code directly: the global config defaults to `opencode/gemini-3.8-flash` for routine execution and `opencode/claude-sonnet-5` (`plan` agent) for design decisions. Picking a slice's storage pattern or debugging the DCB registration gap above are `plan`-mode-worthy; running an already-scoped `build-state-change`/`build-state-view`/`build-automation` skill against a settled `slice.json` is not.
