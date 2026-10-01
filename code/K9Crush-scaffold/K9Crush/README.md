# K9Crush

.NET modular monolith backend (vertical slice architecture, Marten + Wolverine on PostgreSQL/RabbitMQ, Redis) for a **dog shelter/adoption/foster/volunteering charity app** — foster applications, surrender requests, volunteer applications, listing review/approval. Blazor Web App frontend, YARP gateway.

**Not the dating app.** Despite the repo name, this is the shelter/adoption side (legacy naming) — the social/dating/marketplace app derived from the same original spec lives in a separate repo (PawMatch).

## Documents
See `docs/` for the full design set:
- `01-project-plan.md`
- `02-inventory-list.md`
- `03-solution-architecture.md`
- `04-high-level-design.md`
- `05-event-modeling-blueprint.md` — **read this first** if you're adding a new slice; it defines the state-change / state-view / automation discipline every module follows.

## Status of this scaffold
- **Modules** (`src/Modules/`): `Admin`, `Identity`, `Media`, `Notifications`, `ShelterAdoption` — each with Domain/Api/Contracts projects and its own test project.
- **`K9Crush.ArchitectureTests`** — four fitness-function suites, each mechanizing a real past incident rather than a style preference: module boundary isolation (NetArchTest), Wolverine handler naming convention (reflection scan), entity JSON-serialization requirements, and an IL-level scan (Mono.Cecil) catching commands that read persisted snapshots instead of live aggregate state. Verified 2026-10-01: builds clean, **124/124 tests pass**.
- `K9Crush.Api.Host` — composition root: Marten, Wolverine (RabbitMQ + Marten outbox/inbox + event forwarding), Redis, Supabase JWT auth, health checks.
- `K9Crush.Gateway` — YARP reverse proxy with centralized rate limiting.
- `K9Crush.Blazor.App` — Blazor Web App (Interactive Server render mode), calls the API through a typed HttpClient.
- CI/CD/CodeQL/Dependabot workflows are live (`.github/workflows/`). CI gates the `dev`→`main` promotion PR only, by design — `dev` itself is pushed to directly and frequently and is intentionally not CI-gated per-PR.
- Dockerfiles and a production-style `docker-compose.prod.yml` exist under `deploy/` for a single-host MVP deployment — see `GETTING_STARTED.md`. Kubernetes/Helm (ADR-006/011) is the later-scale target, not built yet.
- Not yet scaffolded: Chat, Subscriptions, Moderation modules.

## Identity and storage: Supabase

Postgres is self-hosted, permanently (ADR-047) — this was Supabase-managed earlier (ADR-024) and was deliberately moved off. Supabase Cloud currently still covers two things:
- **Auth** (Identity module, ADR-005) — there's no local/mock stand-in; every authenticated endpoint needs a real Supabase project for local dev (see `GETTING_STARTED.md` Step 2).
- **Object storage** (Media module, ADR-024).

**2026-10 note:** there's an active move away from Supabase for the pieces above, though it may still end up activated specifically for production rather than removed outright — not yet a finalized decision. Treat anything in `GETTING_STARTED.md` describing Supabase setup as the current state, not a settled long-term dependency.

## Build state (verified 2026-10-01)

`BuildingBlocks` + all 5 modules' Domain/Api/Contracts projects + `K9Crush.ArchitectureTests` restore and build clean in Release, and all 124 fitness-function tests pass.

`K9Crush.Api.Host`, `K9Crush.Gateway`, `K9Crush.Blazor.App`, and the per-module unit/integration test projects were **not** independently re-verified in this pass — run `dotnet build K9Crush.sln` / `dotnet test` yourself from a machine with normal NuGet access to confirm the full solution end-to-end.

## Building this locally

```bash
dotnet restore
dotnet build
```

Package versions in `Directory.Packages.props` are pinned to exact versions (NuGet Central Package Management doesn't allow floating versions) — bump deliberately via `dotnet list package --outdated`, one package at a time, re-testing after each, rather than mass-upgrading.

You'll also need RabbitMQ, Redis, and Postgres running locally (`deploy/compose/docker-compose.yml`), plus a Supabase Cloud project for Auth/Storage (see `GETTING_STARTED.md`). Postgres is self-hosted permanently (ADR-047); object storage and auth are still Supabase-managed, per the note above.
