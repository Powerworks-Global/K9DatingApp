# Getting Started — Running K9Crush Locally

The solution builds and runs cleanly (`dotnet build` succeeds with 0 warnings/errors), and every module built so far has real, passing automated tests (~160 tests across `K9Crush.Modules.*.Tests`, `K9Crush.IntegrationTests`, and `K9Crush.ArchitectureTests`). This doc reflects the current API shape as of 2026-07-21 — see Section 6 for what's genuinely still missing (mainly: no UI, and no way to create the first Admin via the API).

## Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) — **not .NET 8.** The solution originally targeted .NET 8, but current Marten (9.x) and Wolverine (6.x) have dropped net8.0 support entirely; see ADR-020 in the Solution Architecture doc. `global.json` pins the SDK to `10.0.100`.
- Docker Desktop (or equivalent) for the local infra containers
- `dotnet --version` should print `10.0.1xx`. If you have both .NET 8 and .NET 10 SDKs installed side by side, that's fine — `global.json`'s `rollForward: latestFeature` will pick the right one for this repo specifically without affecting other projects on your machine.

## 1. Start local infrastructure

```bash
docker compose -f deploy/compose/docker-compose.yml up -d
```

This brings up RabbitMQ, Redis, smtp4dev, the Grafana LGTM-in-one-container image, and a local Postgres container too — self-hosted Postgres is the permanent, intentional architecture per ADR-047, superseding ADR-024's Postgres leg (`appsettings.Development.json`'s `ConnectionStrings:Postgres` already points at it, `k9crush`/`k9crush`/`localhost:5432`). Storage and Auth remain Supabase-managed per ADR-024, set up in Step 2 below. Give it a minute — check everything's healthy:

```bash
docker compose -f deploy/compose/docker-compose.yml ps
```

Useful UIs once it's up:
| Service | URL | Login |
|---|---|---|
| RabbitMQ management | http://localhost:15672 | guest / guest |
| Grafana | http://localhost:3000 | admin / admin |
| smtp4dev (email inbox) | http://localhost:5080 | none — this is where Notifications' real SMTP sends actually land in dev (ADR-027); check here after triggering a match or an application approval/rejection |

## 2. Set up the Supabase project (manual, one-time)

Supabase Cloud covers Auth, Postgres, and Storage now (ADR-005/024) — all external managed services, nothing local to run for any of them. **There is currently no local/mock stand-in for Supabase Auth** — every authenticated endpoint genuinely needs a real Supabase project.

**Secrets go into `dotnet user-secrets`, not `appsettings.Development.json`** (ADR-030) - that file keeps `CHANGE_ME` placeholders permanently; real values live in a per-project json file outside the repo entirely (`dotnet user-secrets set "Key:SubKey" "value"` from the project directory - both `Api.Host` and `Blazor.App` already have a `UserSecretsId`, nothing to init). **User Secrets has higher config precedence than `appsettings.{Environment}.json`** - if you ever edit the json file directly and changes don't seem to take effect, run `dotnet user-secrets list` in that project directory before assuming the json file is the actual source of truth (this cost a real debugging session once already).

1. Go to https://supabase.com, create a free account if you don't have one, and create a new project (pick any name/region - this is throwaway for local dev).
2. Wait for provisioning to finish (a couple of minutes).
3. **Project Settings → API** → copy the **Project URL** (`https://<project-ref>.supabase.co`):
   ```bash
   cd src/Host/K9Crush.Api.Host && dotnet user-secrets set "Supabase:Url" "https://<project-ref>.supabase.co"
   cd ../../Web/K9Crush.Blazor.App && dotnet user-secrets set "Supabase:Url" "https://<project-ref>.supabase.co"
   ```
4. **Project Settings → API → JWT Settings** → copy the **JWT Secret** (a long random string - if what you see is instead a JWKS document or a UUID-shaped "kid", your project is on Supabase's newer asymmetric ES256 signing mode, not the legacy shared-secret mode; look for a "Legacy JWT Secret" toggle/section on the same page - `Api.Host`'s `Program.cs` uses Authority-based OIDC/JWKS discovery and doesn't actually need this value at all, so if there's no legacy secret available, skip this step entirely):
   ```bash
   cd src/Host/K9Crush.Api.Host && dotnet user-secrets set "Supabase:JwtSecret" "<jwt-secret>"
   ```
5. **Project Settings → API** → copy the **anon/public key** (`sb_publishable_...` or "anon public" - not the `sb_secret_...`/service-role key, which is privileged and shouldn't be used here):
   ```bash
   cd src/Web/K9Crush.Blazor.App && dotnet user-secrets set "Supabase:AnonKey" "<anon-key>"
   ```
6. **Project Settings → Database → Connection string.** Skip this step — `ConnectionStrings:Postgres` already points at the local Postgres container from Step 1 (`appsettings.Development.json`, not a user-secret), since self-hosted Postgres is the permanent architecture per ADR-047 (see above). Storage and Auth still use Supabase, so keep reading for those — the gotcha below applies if you ever connect to a Supabase-managed Postgres instance directly. This is the step most likely to bite you: Supabase shows multiple connection string variants (Direct connection, Session pooler, Transaction pooler). **Use "Session pooler" — not "Direct connection" (IPv6-only on new projects, likely unreachable if you're behind an IPv4-only network) and never "Transaction pooler"** (Marten's async daemon relies on Postgres advisory locks for leader election, and transaction-mode pooling doesn't reliably support session-level features like those - getting this wrong doesn't fail loudly, the app starts fine and only misbehaves subtly around projection/subscription processing). Reformat into Npgsql format (`Host=...;Port=...;Database=...;Username=...;Password=...;SSL Mode=Require;Trust Server Certificate=true`):
   ```bash
   cd src/Host/K9Crush.Api.Host && dotnet user-secrets set "ConnectionStrings:Postgres" "Host=aws-0-<region>.pooler.supabase.com;Port=5432;Database=postgres;Username=postgres.<project-ref>;Password=<db-password>;SSL Mode=Require;Trust Server Certificate=true"
   ```
7. **Database → Extensions** → enable `postgis` if you want it ready for later (ADR-014 calls for it eventually for Discovery/Places/Lost & Found proximity queries) — **not required today**: `GetDiscoveryFeedHandler` currently does an in-memory haversine calculation, not a PostGIS query, so you can skip this step for now without anything breaking.
8. **Authentication → Users → Add user** → create a test owner account with an email/password, and confirm the email (Supabase's dashboard lets you manually confirm a test user without actually receiving an email) - or register through the real `/register` page (Section 5) and manually confirm via SQL: `update auth.users set email_confirmed_at = now() where email = '<email>';` (Database → SQL Editor) - this fires the same webhook a real confirmation click would.
9. To get a token for `curl` testing:
   ```bash
   curl -s -X POST "https://<project-ref>.supabase.co/auth/v1/token?grant_type=password" \
     -H "apikey: <anon key from step 5>" \
     -H "Content-Type: application/json" \
     -d '{"email":"<your test user email>","password":"<their password>"}' \
     | python3 -c "import sys,json; print(json.load(sys.stdin)['access_token'])"
   ```
   Confirmed live against a real project (2026-07-23) - this shape is correct.
10. **Database Webhooks** (needed before Identity will ever create an `OwnerAccount` for your test user — see Section 5's smoke test; note this requires enabling the "Database Webhooks" extension first if it isn't already): **Database → Webhooks → Create a new hook**, twice:
    - On `auth.users`, event **INSERT**, HTTP POST to `http://<wherever Api.Host is reachable>/api/v1/identity/webhooks/supabase/user-created`, header `X-Webhook-Secret: <Supabase:WebhookSecret you set via user-secrets>`.
    - Same again for event **UPDATE** (email confirmation), pointing at `.../user-confirmed`.
    Supabase's webhook sender can't reach `localhost` — if you're running `Api.Host` locally rather than on a public host, tunnel it first. `npx --yes localtunnel --port 5100` works with zero signup (confirmed reaches `Api.Host` directly, no interstitial); `ngrok http 5100` is the more common alternative if you have an account. Use the tunnel's HTTPS URL in both webhooks - note it changes every time the tunnel restarts, so both webhooks need re-pointing then. **A webhook only fires for events happening after it's configured** - a user created before the webhook existed won't retroactively get an `OwnerAccount`; you'd need to seed one manually or create a fresh test user after the webhooks are live.

## 3. Create the Supabase Storage bucket

Media module isn't scaffolded yet, so nothing needs this today, but it's a 30-second step to have ready:
1. Supabase dashboard → **Storage** → **New bucket** → `k9crush-media`.
2. If/when the Media module gets built, its S3-compatible client config points at `https://<project-ref>.supabase.co/storage/v1/s3` with an access key/secret generated from Storage settings — not the same credentials as `Supabase:JwtSecret` above.

## 4. Restore and build

```bash
cd K9Crush
dotnet restore
dotnet build
```

`Directory.Packages.props` uses Central Package Management with exact, pinned versions (no floating versions) — restore should be deterministic. If `dotnet build` fails on a specific package, that package's `PackageVersion` entry in `Directory.Packages.props` is the place to check/bump; each entry there is annotated with how confidently its version was verified.

## 5. Run it

Three separate terminals (or run configs in your IDE):

```bash
# Terminal 1
dotnet run --project src/Host/K9Crush.Api.Host
# → http://localhost:5100/swagger

# Terminal 2
dotnet run --project src/Web/K9Crush.Blazor.App
# → http://localhost:5200

# Terminal 3 (optional at this stage - you can hit Api.Host/Blazor.App directly first)
dotnet run --project src/Gateway/K9Crush.Gateway
```

**Start with just Api.Host.** Get that compiling and serving Swagger before layering on Blazor.App and Gateway — it's the piece with the most moving parts (Marten, Wolverine, Redis, Supabase auth all wire up in one `Program.cs`).

### Smoke test: the dog-to-dog matching loop end to end

Once Api.Host is running and you've got a Supabase JWT (Step 2.8) for a user whose signup/confirmation webhooks have actually fired (Step 2.9):

```bash
TOKEN="<paste the access_token from Step 2.8>"
API=http://localhost:5100

curl "$API/healthz/live"

# Start a dog profile (no body - just creates a Draft)
DOG_ID=$(curl -s -X POST "$API/api/v1/profiles/dogs" \
  -H "Authorization: Bearer $TOKEN" | python3 -c "import sys,json; print(json.load(sys.stdin)['dogProfileId'])")

# Add details
curl -X POST "$API/api/v1/profiles/dogs/$DOG_ID/details" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"Rex","breed":"Labrador","ageInMonths":24,"bio":"Good boy","latitude":53.35,"longitude":-6.26}'

# Add a photo (no Media module yet - any Guid works as a placeholder MediaAssetId)
curl -X POST "$API/api/v1/profiles/dogs/$DOG_ID/photos" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d "{\"mediaAssetId\":\"$(python3 -c 'import uuid; print(uuid.uuid4())')\"}"

# Publish - this is what actually makes the dog visible in the discovery feed
curl -X POST "$API/api/v1/profiles/dogs/$DOG_ID/publish" -H "Authorization: Bearer $TOKEN"

# Browse the feed (public, no auth needed) - Rex should show up once the
# DogProfileCreatedV1 -> DiscoveryFeedItem projection has caught up (near-instant locally)
curl "$API/api/v1/discovery/feed?latitude=53.35&longitude=-6.26&radiusMiles=50"
```

Profile creation is a 4-step wizard now (Start → Add Details → Add Photo → Publish), not a single call — `Publish` is gated on having at least one photo and is the step that actually fires the dog into the discovery feed. To see a match + real email notification, repeat this with a second Supabase user/dog, then:

```bash
curl -X POST "$API/api/v1/discovery/swipe" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d "{\"swiperDogId\":\"$DOG_ID\",\"targetDogId\":\"<the other dog's id>\",\"liked\":true}"
```

Once *both* dogs have liked each other, check http://localhost:5080 (smtp4dev) — you should see two real emails, one per owner, sent by `NotifyOnMatchHandler`.

## Run the tests

```bash
dotnet test K9Crush.sln
```

No external services needed beyond Docker (Testcontainers spins up its own disposable Postgres containers for Layer 3 integration tests — it does **not** touch your Supabase project). Expect ~160 tests, all passing. `K9Crush.ArchitectureTests` mechanizes several real bugs found during development (handler-naming convention, entity JSON-serialization attributes, module-boundary rules) as build-time-failing fitness tests rather than relying on code review to catch them again.

## 6. What's deliberately not done yet (don't be surprised)

- **UI is still minimal.** `Home.razor` (discovery feed) plus `Login.razor`/`Register.razor` (Supabase-backed auth, see below) exist; no profile-creation wizard, no swipe UI, no shelter dashboard, no application-review UI. Most of the backend is still only exercised via `curl`/Swagger.
- **First Admin: bootstrap via API, not a manual Postgres edit.** `VerifyShelterHandler`/`ApproveShelterAccountHandler` (the shelter-onboarding review steps) require the `Admin` role. Sign up and confirm a test owner normally (Step 2), then:
  ```bash
  curl -X POST "$API/api/v1/identity/me/bootstrap-admin" -H "Authorization: Bearer $TOKEN"
  ```
  Self-promotes the caller to Admin - but only while zero Admins exist anywhere in the system (`BootstrapAdminHandler`). Once any Admin exists, this permanently 409s for everyone, including the one who just bootstrapped - it's a one-time setup step, not a general role-grant endpoint (there's still no way to create a `Vendor`, or a second `Admin`, via the API).
- **No local/mock Supabase Auth.** Every authenticated endpoint needs a real Supabase Cloud project (Section 2), and Identity only learns about new users via Database Webhooks Supabase sends — which means Supabase must be able to reach wherever `Api.Host` is running (a tunnel like ngrok if running locally, not just `localhost`).
- **Supabase JWT validation uses a shared HS256 secret, not JWKS/OIDC discovery.** This is Supabase's default (simpler, but means the secret lives in `appsettings.Development.json` - fine for local dev, not for anything beyond it). Supabase's newer asymmetric (ES256/JWKS) signing mode is the better long-term fit and avoids that shared-secret exposure entirely, but requires explicitly enabling it in the Supabase project dashboard first - not done here.
- **No Media module.** `AddDogProfilePhotoHandler` only stores a `MediaAssetId` reference (any `Guid` will do for testing, per the smoke test above) — there's no actual upload endpoint or Supabase Storage integration yet.
- **Notifications covers email only, one provider (dev SMTP via smtp4dev), and 3 of the ~7 known triggers.** `NotifyOnMatch`, `NotifyOnApplicationApproved`, and `NotifyOnApplicationRejected` are built and send real SMTP in dev (ADR-027). No push notifications, no presence-based suppression (Redis is in the stack for SignalR but not consulted by Notifications yet), and production email provider (SendGrid/Postmark/SES) is still an open decision. `NotifyApplicantsOfCancellation`/`NotifyApplicantOfListingChange` (ShelterManagingListings) remain deferred — they need a same-module cascade pattern this codebase doesn't have a precedent for yet.
- **Wolverine's handler code is dynamically compiled at runtime (`WolverineFx.RuntimeCompilation`), not pre-generated.** Fine for local dev; production should switch to static codegen (`dotnet run -- codegen write` as a CI step, then `opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static` in `Program.cs`) for faster cold starts and to avoid shipping a Roslyn dependency in the container image. Not yet done.
- **Marten schema auto-create is not explicitly disabled for non-Development environments.** Local dev relies on Marten's own default (`CreateOrUpdate`), which is fine for local. **Do not deploy this anywhere beyond local dev until this is explicitly resolved** — your "local dev" Postgres is a real Supabase Cloud project (ADR-024), not a disposable local container, so schema auto-create is already running against real cloud infrastructure, not localhost.
- **Login/Register now exist in Blazor.App** (`/login`, `/register`) — call Supabase's own `/auth/v1` REST API directly (ADR-005) and, on success, sign the caller into a local auth cookie. Needs `Supabase:Url`/`Supabase:AnonKey` filled in under `Blazor.App/appsettings.Development.json` (separate from `Api.Host`'s `Supabase:Url`/`Supabase:JwtSecret` - the anon key comes from the same **Project Settings → API** page as Step 2.3/2.4). Pages calling `Api.Host`'s `[Authorize]`-gated endpoints still need to attach the cookie's `access_token` claim as a Bearer header themselves - not wired up automatically yet, since no page needs it yet.
- **Dockerfiles exist** (`deploy/docker/*.Dockerfile`) — build them yourself before trusting them for a real deploy:
  ```bash
  docker build -f deploy/docker/ApiHost.Dockerfile -t k9crush-api-host .
  docker build -f deploy/docker/Gateway.Dockerfile -t k9crush-gateway .
  docker build -f deploy/docker/BlazorApp.Dockerfile -t k9crush-blazor-app .
  ```
  Or build and run the whole MVP stack at once - see `deploy/compose/docker-compose.prod.yml` and copy `deploy/compose/.env.example` to `.env` first with real values.

## 7. Where this is most likely to break first

Most of the "unverified API surface" risk from earlier in this project has since been confirmed live (Wolverine's Marten event-forwarding, `AggregateStreamAsync<T>`, the health-check packages, JWT issuer/audience claims — all exercised repeatedly by the test suite and manual verification). What's actually left to trip over now is almost entirely **environment setup**, not code:

1. **Supabase connection pooling mode.** If you used the "Transaction pooler" connection string instead of "Session pooler"/"Direct connection" in Step 2, the app will very likely start fine and only misbehave subtly around Marten's async daemon (projection/subscription processing) - advisory locks don't work reliably under transaction-mode pooling. This is the specific kind of bug that's easy to burn hours on before suspecting the connection string, since nothing fails loudly at startup.
2. **Supabase webhooks never firing.** If `OwnerAccount` documents never appear after a real signup, the most likely cause is the webhook config in Step 2.9 — either the URL isn't reachable from Supabase (no tunnel), or the `X-Webhook-Secret` header doesn't match `Supabase:WebhookSecret`.
3. **Trying to skip straight to an authenticated endpoint without a confirmed test user.** `VerifiedOwner`-gated endpoints require `email_verified: true` on the JWT, which means both webhooks (INSERT and UPDATE) actually firing, not just the first one.
4. **The Admin bootstrap step (Section 6).** If shelter verification/approval 403s, check whether the caller's `OwnerAccount.Role` is actually `Admin` in Postgres yet.

If you hit something not covered here, paste the error back with which step you were on.
