# Agent Learnings

Patterns and gotchas discovered during task processing. Update this file
whenever you encounter something reusable.

## ArchitectureTests: cross-entity LoadAsync in a Commands/Automations handler

`tests/K9Crush.ArchitectureTests/CommandStateFitnessTests.cs`
(`CommandsAndAutomations_MustNotLoadOrQueryARegisteredSnapshotType`) IL-scans
every `Commands/**`/`Automations/**` handler for `LoadAsync<T>`/`Query<T>`
calls where `T` is in `SnapshotRegisteredTypeFullNames` (ADR-019/ADR-031: a
handler must load its OWN mutation target live via
`FetchForWriting`/`AggregateStreamAsync`, never `LoadAsync` a snapshot of the
same type). A legitimate **read-only lookup of a DIFFERENT entity**
(ownership checks, existence checks) still trips this scan purely because
that other entity is also snapshot-registered — it must be added by name to
`ReviewedCrossEntityLoadExceptions` (for `LoadAsync`) or
`ReviewedCrossPopulationQueryExceptions` (for `Query`) with a one-line
comment justifying it, or the build fails. Any new ShelterAdoption
handler that does a cross-entity `LoadAsync` (e.g. dogId ->
`DogListing.ShelterAccountId` -> `ShelterAccount` ownership check, the same
shape as `UpdateListingStatusHandler`) needs this allowlist entry — check
`dotnet test --filter FullyQualifiedName~K9Crush.ArchitectureTests` before
assuming a slice is done, not just the slice's own test filter.

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
- `current_context.json`'s `"name"` field holds the **context slug** (e.g. lowercase, no spaces), not the display context name — it's written from the same dictionary key used to name the `.slices/<contextSlug>/` directory, and both the Ralph loop and the skills look it up directly as a folder name. Writing the display name there instead silently breaks "no planned slice found" on any context whose name isn't already all-lowercase with no spaces (case-sensitive filesystem lookup miss).

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
- On this board, `GET .../slicedata/slices` and the documented `GET .../slicedata?contextName=...&sliceId=...` detail endpoint are **not** useful for getting a slice's real fields/events/scenarios — the summary endpoint only ever returns `{id, title, status, sliceType}`, and the detail endpoint 404s with "No MODEL_CONTEXT node found" because this board has zero `MODEL_CONTEXT` nodes. The reliable path to full slice content: `GET .../nodes/:sliceBorderId` (its `node.data.colId` + `node.parentId` = the chapter), then `GET .../nodes/:chapterId` and read `meta.timelineData.{rows,cells}` — every cell whose `colId` matches the slice's own `colId` is one node of that slice (interaction row = COMMAND/READMODEL, swimlane row = EVENT, actor row = SCREEN, spec row = SCENARIO) — then `GET .../nodes/:nodeId` per id for `meta.title`/`meta.fields`/`meta.description`/`meta.givenWhenThenScenario`. Local `index.json`/`slice.json` on this board are correspondingly minimal stubs (same 4 fields) — don't expect `commands[]`/`events[]`/`specifications[]` arrays to already be populated there; always do the live node walk before implementing.
- Local `index.json`'s `status` can be stale relative to the live board when multiple Ralph instances work the same board concurrently — always re-`GET .../nodes/:sliceId` and check `meta.sliceStatus` before claiming a slice that looks "Planned" locally; it may already be Blocked/InProgress/Done for real.
- A `Done`-status task can arrive in `tasks.json` after the build already landed (e.g. a different Ralph instance, or this one on a prior iteration, completed and committed before this task was dequeued). Before writing up a "Done" task as a no-op summary, check `git log --oneline` for a matching `feat: <slice title>` commit — if found, just log it (no rebuild), don't treat it as evidence of missed work.

## .NET / Wolverine / Marten specifics — event-sourced only

This kit has a **single, fixed storage strategy**: every domain entity is an
event-sourced, self-aggregating aggregate (`Create`/`Apply`). There is no
per-module or per-slice document-vs-event-sourced decision — see
`build-state-change`'s Step 2 for the reasoning. Everything below assumes
that starting point.

- Any class with a public static `Handle` method must be named `*Handler`, even when the file itself is named after a trigger event (a projector). Wolverine's convention-based discovery silently skips anything else, with no error and no log line — the only symptom is "nothing happened."
- `IDocumentSession.Store(...)` only stages a change — always call `SaveChangesAsync` explicitly, especially in projectors/automations reacting to an event, where it's easy to forget since the handler "did work" without it.
- Validation is DataAnnotations + `IValidatableObject` on the request record, wired centrally via `UseDataAnnotationsValidationProblemDetailMiddleware()` — never a separate FluentValidation class; it silently never runs against `[WolverineGet]`/`[WolverinePost]` endpoints (Wolverine.Http bypasses the message-bus pipeline FluentValidation hooks into).
- A module consuming a cross-module integration event for the first time needs `IntegrationEventQueueName` set on its `<Context>Module.cs`, or the published event has nothing bound to receive it and is silently dropped — the publish succeeds, the exchange fans it out, and a queue-less consumer simply never gets it.
- Event-sourced entities with a private constructor/setters need `[JsonConstructor]`/`[JsonInclude]`, or Marten's serializer throws `NotSupportedException` on the first real read (write path — append/`SaveChangesAsync` — works fine either way; this is a read-path-only bug that a build and even a successful write won't catch).
- **Two separate snapshot/state rules — don't conflate them:**
  - An **entity's own Inline snapshot** (`options.Projections.Snapshot<T>(SnapshotLifecycle.Inline)`) is registered only when a read model genuinely queries that entity by id. It's durable, shared, and other handlers/read models are meant to depend on it — that's the point of adding it.
  - A **command/automation's own decision state** (a narrow `[Name]State` type used only to decide "should I act") is computed live via `AggregateStreamAsync<T>` on every invocation, **never** persisted as a snapshot and **never** reused across handlers, even when two handlers' state looks similar. Persisting or sharing this kind is what causes the fragility — a field added for one handler's decision silently affects another's, or a persisted copy drifts from what replaying the stream would actually produce.
  - If you're tempted to reuse a decision-state type from a second handler, that's usually a sign the second handler needs its own type, not that the first one should become shared/persisted.
- Read-model documents (built by a projector, or read directly off an entity's Inline snapshot) are always plain Marten documents, never themselves event-sourced — "event-sourced only" describes the write side (the source of truth), not every queryable thing derived from it. This is expected, not an exception to the rule.
- No SQL/Flyway migration files for application schema — Marten auto-manages it (`AutoCreateSchemaObjects`). Testcontainers fixtures need `AutoCreate.All` set explicitly on the test `DocumentStore`, since that's not necessarily what the app's own `AddMarten()` call resolves to outside Development — check your project's `Program.cs` for what it currently relies on before assuming the default matches.
- Concurrency: `FetchForWriting`/`FetchForExclusiveWriting` are optimistic by default. The exception on a stale fetch is `JasperFx.ConcurrencyException` — a different hierarchy from Marten's own *document*-level `Marten.Exceptions.ConcurrentUpdateException`. If your app also has any plain (non-event-sourced) documents with their own optimistic concurrency, map both exception types to a `409 Conflict`, once, centrally — not per-handler.
- Before deploying anywhere beyond local development, confirm what your Marten version's schema-auto-creation config surface actually is (this has moved across major versions — check your installed package version's own API via your IDE's "go to definition" rather than assuming a specific enum/namespace) and set it explicitly rather than relying on whatever the library's current default happens to be.

## Program.cs / Host wiring — validated baseline

The skills (`build-state-change` Step 5, `build-state-view` Step 6, README setup step 5) all assume `Api.Host/Program.cs` and `IMartenModuleConfiguration` already exist and don't set them up. They didn't exist on this kit's first real run — the following was worked out by scaffolding a solution from scratch and fixing it against real `dotnet build` errors, not from docs alone. Reuse this rather than re-deriving it.

- **Validated package combo** (net10.0, confirmed by an actual successful build): `WolverineFx`, `WolverineFx.Http`, `WolverineFx.Marten`, `WolverineFx.RabbitMQ` — all `6.22.0` — plus `Marten` `9.19.0`. Re-check for newer compatible versions on a much later date rather than assuming these still resolve, but this is a live-verified starting point, not a guess.
- **`WolverineFx.Marten` is a separate, easy-to-forget package.** Installing only `WolverineFx` + `Marten` restores and compiles fine right up until you write `AddMarten(...).IntegrateWithWolverine()` — that extension method only exists once `WolverineFx.Marten` is also referenced. Add all four Wolverine packages up front, not incrementally as errors appear.
- **`UseDataAnnotationsValidationProblemDetailMiddleware()` lives on `WolverineHttpOptions`, not `WebApplication`.** It is not an `app.Use(...)` middleware call. Wire it through `MapWolverineEndpoints`'s options callback:
  ```csharp
  app.MapWolverineEndpoints(opts => opts.UseDataAnnotationsValidationProblemDetailMiddleware());
  ```
  `builder.Services.AddWolverineHttp()` itself takes no options callback — don't look for one there.
- **Minimal skeleton that builds**, given `IMartenModuleConfiguration[] modules` (empty until the first module is scaffolded):
  ```csharp
  builder.Host.UseWolverine(opts =>
  {
      foreach (var module in modules)
          opts.Discovery.IncludeAssembly(module.GetType().Assembly);

      opts.UseRabbitMq(new Uri(rabbitConnectionString)).AutoProvision();

      foreach (var module in modules)
          if (module.IntegrationEventQueueName is { } queueName)
              opts.ListenToRabbitQueue(queueName).UseDurableInbox();
  });

  builder.Services.AddMarten(opts =>
  {
      opts.Connection(postgresConnectionString);
      foreach (var module in modules) module.Configure(opts);
  }).IntegrateWithWolverine();

  builder.Services.AddWolverineHttp();

  var app = builder.Build();

  app.Use(async (context, next) =>
  {
      try { await next(context); }
      catch (Exception ex) when (ex is JasperFx.ConcurrencyException or Marten.Exceptions.ConcurrentUpdateException)
      {
          context.Response.Clear();
          await Results.Conflict("This resource was modified by someone else since you last loaded it. Reload and try again.")
              .ExecuteAsync(context);
      }
  });

  app.MapWolverineEndpoints(opts => opts.UseDataAnnotationsValidationProblemDetailMiddleware());
  await app.RunJasperFxCommands(args);
  ```
- **`IMartenModuleConfiguration`** (the interface every `<Context>Module.cs` implements, referenced but never defined by the skills) lives in `<SolutionName>.BuildingBlocks.Domain`:
  ```csharp
  public interface IMartenModuleConfiguration
  {
      string SchemaName { get; }
      void Configure(StoreOptions options);
      string? IntegrationEventQueueName => null; // default-implemented — most modules never override this
  }
  ```
- Live reference: `src/Host/<SolutionName>.Api.Host/Program.cs` and `src/BuildingBlocks/<SolutionName>.BuildingBlocks.Domain/` in your `<path-to-your-.NET-solution>/` solution.
- When a handler resolves cross-entity ownership via a field on its own aggregate that's only populated once the aggregate reaches a specific status (e.g. a `ShelterAccountId` set only at `Accept`), the status guard must run *before* the ownership LoadAsync — otherwise a not-yet-eligible entity resolves ownership against a zero/default id and returns the wrong error (`Forbid` instead of `Conflict`). Order guards by data dependency, not by convention.
