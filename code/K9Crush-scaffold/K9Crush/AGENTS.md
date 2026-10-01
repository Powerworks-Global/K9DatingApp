# K9Crush — Project Configuration

Read `docs/05-event-modeling-blueprint.md` to understand the three slice
lanes (state-change / state-view / automation) and the folder convention.
Read `TestingApproach/TestingApproach.md` for the 4-layer testing strategy
before writing any test.

## File Structure Constraints

- **Slice organization**: each feature/domain lives under its module's
  `Api` project, organized by lane — `Commands/<Name>/`, `ReadModels/<Name>/`,
  or `Automations/<Name>/` — never a flat `Features/` folder (that was the
  first pass of this scaffold and was deliberately replaced).
- If not instructed otherwise when working on a specific slice, only touch
  the files under that slice's own folder, its module's `Domain`/`Contracts`
  projects if a new entity/event/integration-event is genuinely needed, its
  test files, and the shared wiring points called out per-skill (`<Module>Module.cs`,
  `Api.Host/Program.cs`) — don't wander into unrelated modules or slices.

## Code Standards

- **Language**: C#, `net10.0`, nullable enabled (see `Directory.Build.props`).
- **Validation**: `System.ComponentModel.DataAnnotations` attributes on
  request records, `IValidatableObject` for cross-field/non-empty-`Guid`
  rules. **Never** a separate `AbstractValidator<T>`/FluentValidation class
  — Wolverine.Http endpoints bypass that pipeline entirely (confirmed live;
  see blueprint doc Section 5.1).
- **Handler naming**: any class with a public static `Handle` method must
  have a class name ending in `Handler`, even when the file is named after
  a trigger event (a projector) rather than the handler itself. Wolverine's
  convention-based discovery silently skips anything else (blueprint doc
  Section 5.2 — this has broken a real slice before).
- **Entity serialization**: any `Entity` subtype with a non-public
  constructor needs `[JsonConstructor]`; any non-publicly-settable property
  on it needs `[JsonInclude]` (blueprint doc Section 6.1).
- Ignore case for context/slice names in prompts — "ShelterAdoption" is the
  same as "shelteradoption".
- Do not change existing test files (`*Tests.cs`) unless explicitly
  instructed — write new ones alongside the slice you're building instead.

## Building a Slice

**Always use the skills in `.claude/skills/` at the repo root to build a
slice. Do not implement a slice manually.** All fields, event names,
command names, and business rules must come exclusively from the slice's
`slice.json`. Do not invent, assume, or guess anything not present there.

When asked to build a slice:

1. If you don't already have a fresh `slice.json` for it, run the
   `load-slice` skill (which itself runs `connect` first) —
   `build-kit-dotnet/.slices/<context>/<slicename>/slice.json`.
2. Determine the slice type from the slice.json:
   - **Translation** — `sliceType === "TRANSLATION"` → read `description`/`notes` for hints; default to `build-automation` if nothing else is specified
   - **Automation** — `processors[]` is non-empty → invoke `build-automation`
   - **State-view** — `projections`/`queries`/`readmodels` is non-empty → invoke `build-state-view`
   - **State-change** — default (has `commands`/`events`) → invoke `build-state-change`
3. Invoke the matching skill and follow it completely. Do not deviate —
   in particular, each skill's Step 1–2 on picking document-store vs.
   event-sourced storage, and its tests-first ordering, are not optional.
4. **Verify against slice.json**: after the skill completes, check every
   command field, event field, and specification in slice.json appears in
   the implementation. No invented fields or business rules — if it's not
   in slice.json, it's not in the code.
5. Run quality checks:
   ```bash
   dotnet build code/K9Crush-scaffold/K9Crush/K9Crush.sln
   dotnet test code/K9Crush-scaffold/K9Crush/K9Crush.sln --filter "FullyQualifiedName~<SliceName>"
   ```
   (Only the slice's own tests — not the full suite.)
6. If checks pass, commit with message `feat: [Slice Name]` on the current
   branch. Do **not** merge to `main` or push automatically as part of this
   flow — this repo only pushes `dev`, and `main` is synced deliberately,
   not as a side effect of finishing a slice.
7. Set the slice status to `Done` via the `update-slice-status` skill.

## Example Slice Structure

```
src/Modules/<Context>/K9Crush.Modules.<Context>.Api/
├── Commands/<CommandName>/
│   ├── <CommandName>.cs
│   └── <CommandName>Handler.cs
├── ReadModels/<ReadModelName>/
│   ├── <ReadModelName>.cs
│   ├── <ReadModelName>Handler.cs
│   └── <TriggerEvent>Projector.cs   (class named <TriggerEvent>ProjectorHandler)
├── Automations/<AutomationName>/
│   └── <AutomationName>Handler.cs
└── <Module>Module.cs
```

## Infra

RabbitMQ and Postgres both run locally via `deploy/compose/docker-compose.yml`
(a local `postgres:16` container) — Postgres is self-hosted permanently per
ADR-047, which supersedes ADR-024's Postgres leg; see
`docs/03-solution-architecture.md` and `GETTING_STARTED.md` Step 1. Storage
and Auth remain Supabase-managed per ADR-024. No Flyway/SQL migration files
for application schema either way; Marten manages document/event schema
automatically (`AutoCreateSchemaObjects`, see `Program.cs`).

## Maintaining this file

Keep this file for knowledge useful to almost every future agent session in this project.
Do not repeat what the codebase already shows; point to the authoritative file or command instead.
Prefer rewriting or pruning existing entries over appending new ones.
When updating this file, preserve this bar for all agents and keep entries concise.
