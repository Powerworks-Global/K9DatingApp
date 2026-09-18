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

RabbitMQ runs locally via `deploy/compose/docker-compose.yml`. Postgres is
**deliberately, temporarily** local too (`docker compose -f deploy/compose/docker-compose.yml up -d postgres`,
`k9crush`/`k9crush`/`k9crush` on `localhost:5432`) — a stated reversal of
ADR-024's move to Supabase-managed Postgres, kept for fast local iteration
during active slice build-out. Revert to Supabase-managed Postgres before
this project ships; see ADR-024 for why that's still the target state. No
Flyway/SQL migration files for application schema either way — Marten
manages document/event schema automatically (`AutoCreateSchemaObjects`,
see `Program.cs`).

## Marten DCB (Dynamic Consistency Boundary) — real, working, one open gap

Marten 9.20.1 / Wolverine 6.0 ship real DCB support (shipped May 2026 —
after most model training cutoffs; fetch `martendb.io/events/dcb.html`
fresh rather than trusting memory on this). A real DCB modernization of
`ApproveApplicationHandler` exists on branch `poc/dcb-modernization-approve-application`:
replaces two independent `FetchForWriting<Application>`/`FetchForWriting<DogListing>`
calls with one `FetchForWritingByTags<T>` consistency boundary. Verified:
120/121 `K9Crush.ArchitectureTests` pass (the 1 expected failure is
`CommandStateFitnessTests`, since DCB reads via `LoadAsync` against Inline
snapshots instead of the ADR-019 live-aggregation pattern above — expected,
not a regression); full `K9Crush.Modules.ShelterAdoption.Tests` 203/203 pass.

**Open, unresolved gap**: `FetchForWritingByTags<T>` throws
`InvalidProjectionException: No source-generated dispatcher found` for a
purpose-built DCB boundary type, even with correct `Apply` methods and
`EmitCompilerGeneratedFiles=true` showing zero generator output for the
whole project. Narrowed to a `JasperFx.Events.SourceGenerator`
activation/visibility question, not a wrong-API-shape problem (a custom
`IProjection` was tried and ruled out with real evidence — wrong extension
point entirely, see full writeup). Full investigation, including the exact
errors from three failed registration attempts:
`poc/DcbRewrite/NOTES.md` at `github.com/Powerworks/internal-modernization-accelerator`
(branch `poc/dcb-modernization`) — read that before attempting DCB on a new
slice, don't re-derive the same dead ends.

## Sandbox / CI build environment gotchas

- MSBuild's persistent build-server nodes crash (`SocketException: Permission
  denied` on Unix-socket creation) in sandboxes that block Unix sockets —
  cosmetic (MSBuild falls back to a single-shot build) but noisy. Use
  `-m:1 -nodeReuse:false` on `dotnet build`/`dotnet test`, or set
  `MSBUILDDISABLENODEREUSE=1`, in any such environment.
- TestContainers-via-Docker-socket isn't reachable from some sandboxed tool
  contexts even when `docker ps`/raw TCP work fine. Workaround: a standalone
  probe console app against an already-running `docker compose` Postgres
  instance, rather than TestContainers spinning up its own.

## OpenCode routing guidance

Global OpenCode config (`~/.config/opencode/opencode.jsonc`) defaults the
`build` agent to `opencode/gemini-3.8-flash` (fast/cheap) and the `plan`
agent to `opencode/claude-sonnet-5` (deep reasoning), plus a `reviewer`
subagent on `deepseek/deepseek-v4-flash` (direct key, not routed through
Zen). On this repo:

- **Use `plan` mode** for anything DCB-related (the open source-generator
  gap above is exactly this kind of problem), cross-module boundary
  questions, or picking a slice's storage pattern (Step 1–2 of
  `build-state-change`/`build-state-view`) — these are the genuine
  decision points worth paying for.
- **Default `build` mode is fine** for routine slice construction once a
  skill and `slice.json` are already picked — matches the "follow the skill
  completely, don't deviate" instruction above; there's little judgment
  left to spend a strong model on at that point.
- **Noisy-output traps**: don't run the full `dotnet test` suite across all
  6 modules in agent context — always scope with
  `--filter "FullyQualifiedName~<SliceName>"` per the Building a Slice
  section above. The MSBuild node-reuse crash noise (previous section) is
  harmless but will otherwise flood output on every build/test invocation
  in a sandboxed session.

## Maintaining this file

Keep this file for knowledge useful to almost every future agent session in this project.
Do not repeat what the codebase already shows; point to the authoritative file or command instead.
Prefer rewriting or pruning existing entries over appending new ones.
When updating this file, preserve this bar for all agents and keep entries concise.
