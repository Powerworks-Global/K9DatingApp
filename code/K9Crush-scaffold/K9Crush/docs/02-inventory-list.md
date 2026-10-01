# Inventory List — K9Crush Platform

## 1. Solution / Project Inventory

Modular monolith with one solution, one repo. Each business module is a set of projects following the same internal shape (vertical slice architecture — folders per feature/slice inside `Application`, not per technical layer).

```
K9Crush.sln
│
├── src/
│   ├── BuildingBlocks/
│   │   ├── K9Crush.BuildingBlocks.Domain          (base entity/aggregate, IEvent, ValueObject)
│   │   ├── K9Crush.BuildingBlocks.Messaging        (MassTransit conventions, outbox contracts)
│   │   ├── K9Crush.BuildingBlocks.Persistence      (Marten config helpers, session factory)
│   │   └── K9Crush.BuildingBlocks.Web              (minimal API conventions, ProblemDetails, auth helpers)
│   │
│   ├── Modules/
│   │   ├── Identity/
│   │   │   ├── K9Crush.Modules.Identity.Api        (module's minimal API endpoints / slices)
│   │   │   ├── K9Crush.Modules.Identity.Domain
│   │   │   ├── K9Crush.Modules.Identity.Infrastructure
│   │   │   └── K9Crush.Modules.Identity.Contracts  (public integration events/DTOs only)
│   │   │
│   │   ├── Profiles/          (dog + owner profiles, same shape as above)
│   │   ├── Discovery/         (matching/swiping, same shape)
│   │   ├── Scheduling/        (playdate scheduler, events/meetups - new, Train B)
│   │   ├── Community/         (feed, likes/follows, groups - new, Train B)
│   │   ├── Chat/              (messaging, same shape)
│   │   ├── Places/            (local services directory + booking, dog-friendly cafes/bars - new, Train C)
│   │   ├── Shop/              (catalog, cart, checkout, payments - new, Train C)
│   │   ├── LostAndFound/      (report/sighting, geo-broadcast alerts - new, Train D)
│   │   ├── ShelterAdoption/   (shelter listings, adoption applications - new, Train D)
│   │   ├── Notifications/     (same shape; gains scheduled/recurring reminders in Train B)
│   │   ├── Media/             (photo/video upload; gains transcoding pipeline in Train C)
│   │   ├── Subscriptions/     (same shape)
│   │   └── Moderation/        (same shape; priority elevated to Train A per updated risk assessment)
│   │
│   ├── Gateway/
│   │   └── K9Crush.Gateway                          (YARP reverse proxy, public entry point)
│   │
│   ├── Host/
│   │   └── K9Crush.Api.Host                        (composition root: wires all modules, Program.cs)
│   │
│   └── Web/
│       └── K9Crush.Blazor.App                       (Blazor Web App, Interactive Server + WASM)
│
├── tests/
│   ├── K9Crush.ArchitectureTests                    (NetArchTest boundary enforcement)
│   ├── K9Crush.Modules.*.UnitTests                   (per module)
│   └── K9Crush.Modules.*.IntegrationTests             (Testcontainers-based, per module)
│
├── deploy/
│   ├── docker/                                        (Dockerfiles per deployable: Gateway, Api.Host, Blazor.App)
│   ├── compose/                                        (docker-compose.yml for local dev infra)
│   ├── helm/                                           (charts per deployable + operator value files)
│   ├── keycloak/                                       (realm export JSON)
│   └── github/                                          (workflow YAML, composite actions)
│
└── docs/
    └── adr/                                            (Architecture Decision Records)
```

> **Content (training/health tips, newsletter) is deliberately not a code module** — per ADR-018, it's a lightweight CMS plus a third-party email platform integration, not custom-built. If a thin `K9Crush.Modules.Content` project ends up needed (e.g. to surface tips inside the Blazor app), it stays read-only: pulling from the CMS's API, never authoring content itself.

## 2. NuGet Package Inventory

| Category | Package | Purpose |
|---|---|---|
| Document DB / Event Store | `Marten` | Document store + event sourcing on Postgres |
| Document DB | `Marten.AspNetCore` | ASP.NET Core integration helpers |
| Messaging & Mediator | `WolverineFx` | In-process command/query handling **and** message bus (replaces a separate mediator library) |
| Messaging | `WolverineFx.RabbitMQ` | RabbitMQ transport |
| Messaging + Persistence | `WolverineFx.Marten` | Wires Marten's transactional outbox/inbox directly into Wolverine — durable messaging with no custom outbox code |
| API (optional) | `WolverineFx.Http` | Lets slices expose HTTP endpoints directly from Wolverine handlers, cutting minimal-API boilerplate further |
| Caching | `StackExchange.Redis` | Redis client |
| Caching | `Microsoft.Extensions.Caching.StackExchangeRedis` | `IDistributedCache` backed by Redis |
| Real-time | `Microsoft.AspNetCore.SignalR` | Chat hub |
| Real-time scale-out | `Microsoft.AspNetCore.SignalR.StackExchangeRedis` | SignalR Redis backplane |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer` | JWT auth |
| Auth | `Microsoft.AspNetCore.Identity` (or external IdP SDK, e.g. Duende/Auth0/Entra External ID) | Identity management |
| API | `Microsoft.AspNetCore.OpenApi` / `Swashbuckle.AspNetCore` | OpenAPI generation |
| Validation | `FluentValidation` | Slice request validation |
| Mapping | `Mapster` or manual mapping | DTO ↔ domain mapping (kept minimal by design) |
| Resilience | `Microsoft.Extensions.Http.Resilience` (Polly v8) | Retry/circuit breaker for outbound calls |
| Observability | *(none — zero-code auto-instrumentation, see Section 7)* | No `OpenTelemetry.Extensions.Hosting`/`Instrumentation.*` packages needed. Traces/metrics/logs come from the OpenTelemetry .NET Automatic Instrumentation agent installed into the container image and configured entirely via environment variables — not via `Program.cs` code. See Section 7. |
| Logging | `Serilog.AspNetCore` | Structured console logging; Alloy's OTLP log receiver picks these up too when `OTEL_LOGS_EXPORTER=otlp` is set, so Serilog's job stays "structure the message," not "ship it" |
| Health checks | `AspNetCore.HealthChecks.NpgSql`, `.Redis`, `.RabbitMQ` | Liveness/readiness probes |
| Testing | `xUnit`, `FluentAssertions`, `Testcontainers.PostgreSql`, `Testcontainers.RabbitMq`, `Testcontainers.Redis`, `NetArchTest.Rules`, `WireMock.Net` | Test suite |
| Object storage client | `AWSSDK.S3` | Supabase Storage is S3-API-compatible (ADR-024), so this client talks to it unchanged - just a different endpoint + credentials than the originally-planned MinIO target |
| Blazor | `Microsoft.AspNetCore.Components.WebAssembly`, `Microsoft.AspNetCore.Components.Server` | Render modes |

**New for the expanded scope (Trains B-D):**

| Category | Package | Purpose | Train |
|---|---|---|---|
| Geospatial | `NetTopologySuite` | .NET spatial types (points/polygons) so Marten/Npgsql can talk to PostGIS `geography` columns directly | C, but enabled from Phase 1 per ADR-014 |
| Payments | `Stripe.net` | Checkout sessions, payment intents, webhook signature verification - Shop never touches raw card data | C |
| Video | `Xabe.FFmpeg` (or a thin wrapper around the `ffmpeg` CLI) | Transcoding/thumbnail generation in the Media module's async worker | C |
| Scheduled messages | *(none - built into `WolverineFx`)* | Playdate/event/vet reminders use Wolverine's native `SchedulePublishAsync`, not a separate job scheduler like Hangfire/Quartz | B |
| Email/newsletter | *(none - third-party API, not a NuGet SDK)* | SendGrid/Mailchimp/etc. called via their HTTP API from the Notifications module; deliberately not a custom newsletter engine (ADR-018) | Ongoing |

> **Decision (ADR-002, confirmed):** Wolverine is the mediator + message bus for the whole solution. It acts as the in-process handler dispatcher inside a slice *and* the RabbitMQ transport across modules, using the same handler signature either way — a slice handler doesn't need to know whether it was invoked locally or over the bus. Combined with `WolverineFx.Marten`, the outbox/inbox pattern described in Section 4 of the Solution Architecture doc is "free" — Wolverine detects the `IDocumentSession` in scope and enlists outgoing messages in the same transaction automatically.

## 3. Infrastructure Component Inventory

| Component | Purpose | Local Dev | Target Platform |
|---|---|---|---|
| PostgreSQL 16+ | Marten document/event storage | **Local `postgres:16` Docker Compose container** — see `GETTING_STARTED.md` Step 1 | **Self-hosted, permanently (ADR-047, superseding ADR-024's Postgres leg)** — e.g. CloudNativePG in Kubernetes (ADR-011), a plain `postgres:16` container on the single-VPS MVP stage (ADR-025). Backups/PITR are this team's responsibility, not a managed service's. Direct connection, no pooler in front of it, so Marten's advisory-lock-based leader election works without any session-mode-vs-transaction-mode caveat |
| RabbitMQ 3.13+ | Async integration events between modules | Docker Compose container | **Self-hosted in-cluster via RabbitMQ Cluster Operator (ADR-011)**, quorum queues |
| Redis 7+ | Distributed cache, SignalR backplane, rate limiting | Docker Compose container | **Self-hosted in-cluster via Redis Operator/Bitnami Helm chart (ADR-011)**, primary/replica + Sentinel |
| Object storage | Dog photos/videos — **Supabase Storage (S3-compatible)**, decided (ADR-024), superseding the earlier MinIO decision (ADR-009) | **Supabase Cloud project** - no local container | Supabase-managed; the earlier MinIO single-node-durability caveat no longer applies since this isn't self-hosted anymore |
| Observability collector | Receives OTLP from every app via zero-code auto-instrumentation, routes to Loki/Tempo/Mimir | Grafana Alloy container (or the bundled `grafana/otel-lgtm` image — see below) | Grafana Alloy (Deployment/DaemonSet) |
| Container registry | Store built images | local Docker | GitHub Container Registry (GHCR) or ACR/ECR |
| Orchestrator | Run containers | Docker Compose | **Kubernetes, decided (ADR-006)** |
| Kubernetes operators | Manage the self-hosted data/observability tier declaratively | n/a (Docker Compose containers stand in locally) | RabbitMQ Cluster Operator, Redis Operator, OpenTelemetry Operator, cert-manager, CloudNativePG (restored per ADR-047, superseding ADR-024's Postgres leg) — MinIO Operator no longer needed per ADR-024 |
| Ingress/Reverse proxy | TLS termination, routing | n/a | NGINX Ingress + cert-manager/Let's Encrypt (YARP Gateway sits behind this) |
| Secrets management | Connection strings, JWT keys | `.env` (gitignored) / user-secrets | Kubernetes Secrets + external vault (Azure Key Vault / AWS Secrets Manager) |
| Observability stack | Logs/metrics/traces — **Grafana LGTM (Loki, Grafana, Tempo, Mimir) fed by Grafana Alloy**, decided (ADR-010) | Single `grafana/otel-lgtm` container (bundles all four for local dev) | Separate Helm charts per component (`grafana/loki`, `grafana/tempo`, `grafana/mimir-distributed`, `grafana/grafana`) + Alloy as collector |
| Email provider | Notifications | **smtp4dev container, decided** - captures every email sent in dev/staging into a local web inbox (http://localhost:5080), nothing actually delivered | SendGrid / Postmark / SES |
| Push notification provider | Mobile/web push | Provider sandbox | Firebase Cloud Messaging / OneSignal |

> **Superseded:** the original MinIO single-node-durability caveat no longer applies (ADR-024 moved object storage to Supabase Storage, managed). Kept here for history since it's exactly the kind of risk that reversal was meant to avoid.

## 4. Environment Inventory

| Environment | Purpose | Deployed From |
|---|---|---|
| Local | Developer machine, docker-compose infra | n/a |
| CI | Ephemeral, spun up per pipeline run via Testcontainers | GitHub Actions runners |
| Dev/Integration | Shared team environment, auto-deployed on merge to `main` | GitHub Actions on push to `main` |
| Staging | Pre-prod, mirrors prod topology, used for soak/E2E tests | GitHub Actions on tag/release branch |
| Production | Live | GitHub Actions on approved release, manual gate |

## 5. External Integrations Inventory

| Integration | Used By Module | Notes |
|---|---|---|
| Geolocation/maps API (e.g., Google Maps Platform, Mapbox) | Discovery, Places, Lost & Found | Geocoding addresses to coordinates and rendering maps client-side; the actual radius/proximity *queries* run against our own PostGIS data (ADR-014), not this API - it's for geocoding and map tiles, not search |
| Email provider (SendGrid/Postmark) | Notifications | Transactional email (match alerts, booking confirmations, reminders) |
| Newsletter/marketing email platform (e.g., Mailchimp/Customer.io) — **new, ADR-018** | Content (thin) | Deliberately separate from the transactional provider above; newsletter content/subscriber management lives in the third-party platform, not built in-house |
| Push provider (FCM/OneSignal) | Notifications | Web push (PWA) |
| Payment provider — **Stripe, real integration, Train C** | Shop, Subscriptions | Stripe Checkout/Elements only - K9Crush never stores or touches raw card data; webhooks confirm payment/subscription state asynchronously, consumed idempotently via Wolverine's inbox |
| Identity provider — **Supabase Cloud (Auth), decided (ADR-005/ADR-023)** | Identity | Owns registration/login/MFA/token issuance as an external managed service, no local container; Owner/Vendor/Shelter/Admin roles (ADR-017) are looked up from our own Postgres, not carried in the Supabase token |
| Object storage — **Supabase Storage (S3-compatible), decided (ADR-024)** | Media, Shop product images, Places listing photos | Superseded MinIO (ADR-009) - durability is now Supabase's managed responsibility. **Not** used as the Grafana LGTM stack's backing store (ADR-024 flags this as unverified against Loki/Tempo/Mimir's specific needs) - LGTM runs on local-disk storage for now |
| "Dog-friendly" venue data | Places | Not available as a clean external feed - expect a curated/user-submitted listings model (owners or venues self-report dog-friendly status) rather than a data source that hands this to you for free; budget content-sourcing effort into Train C, not just engineering effort |
| Reporting — **FastReport (ADR-022), tier not yet decided** | Not tied to a specific module yet | Not currently referenced by any module or in `Directory.Packages.props` - recorded as the chosen tool for whenever a reporting requirement actually shows up, so it isn't improvised ad hoc then. Add the package (`FastReport.OpenSource` or the commercial `FastReport.Net`, per ADR-022) at that point, not before. |

## 6. GitHub Actions Workflow Inventory

| Workflow File | Trigger | Purpose |
|---|---|---|
| `ci.yml` | PR to `main` | Restore, build, unit tests, architecture tests, lint |
| `ci-integration.yml` | PR to `main` (or nightly) | Integration tests via Testcontainers |
| `cd-dev.yml` | Push to `main` | Build & push images, deploy to Dev |
| `cd-staging.yml` | Tag `v*-rc*` | Deploy to Staging, run E2E suite |
| `cd-prod.yml` | Manual approval / release tag | Deploy to Production (blue/green or rolling) |
| `security-scan.yml` | Scheduled + PR | Dependency scanning (Dependabot/CodeQL), container image scan (Trivy) |

## 7. Zero-Code Observability Wiring (ADR-010, revised for Kubernetes per ADR-006)

Two injection mechanisms, same underlying OTel .NET SDK — pick based on environment:

**Kubernetes (primary, all real environments):** the **OpenTelemetry Operator**'s auto-instrumentation injects the agent via pod annotations — no Dockerfile changes needed at all:
```yaml
# Deployment metadata, not application code
annotations:
  instrumentation.opentelemetry.io/inject-dotnet: "true"
```
The Operator manages an `Instrumentation` custom resource holding the OTLP endpoint, service name conventions, and agent version centrally — bumping the agent version is a CR update, not a rebuild-and-redeploy of every image.

**Local `docker-compose` (no operator available):** the Dockerfile-based install is the documented fallback, unchanged from the original approach:
```dockerfile
RUN apt-get update && apt-get install -y curl unzip \
 && curl -sSfL https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/releases/latest/download/otel-dotnet-auto-install.sh -o otel-install.sh \
 && sh ./otel-install.sh \
 && rm ./otel-install.sh
```

**Environment variables (same either way — set via Helm values/annotations in-cluster, or Compose env locally, never in code):**
```
OTEL_SERVICE_NAME=api-host                 # differs per deployable
OTEL_EXPORTER_OTLP_ENDPOINT=http://alloy:4318
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_TRACES_EXPORTER=otlp
OTEL_METRICS_EXPORTER=otlp
OTEL_LOGS_EXPORTER=otlp
OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=production,service.version=1.0.0
```
Npgsql and StackExchange.Redis are both on the auto-instrumentation agent's supported-library list, so Postgres and Redis calls get traced automatically without any Marten- or StackExchange-specific package. If a manual span is ever needed for something business-specific (e.g. wrapping "detect mutual match" as its own span), that's added with the plain `System.Diagnostics.ActivitySource` API — the auto-instrumentation agent picks up any `ActivitySource` registered via `OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES`, so manual and automatic instrumentation coexist without conflict.

**Local dev:** the `grafana/otel-lgtm` container bundles Grafana + Loki + Tempo + Mimir + its own OTLP receiver in one image — point `OTEL_EXPORTER_OTLP_ENDPOINT` at it directly and skip standing up a separate Alloy instance locally. **Production** replaces that single container with Grafana Alloy as the collector (receiving OTLP from every deployable) fronting separately-scaled Loki/Tempo/Mimir/Grafana, all running in-cluster now that Kubernetes is the decided platform.

