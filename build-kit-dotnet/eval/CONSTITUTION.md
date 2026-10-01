# K9Crush — Constitution

Declared up front, not generated from the board after the fact (WS4.2, Corrective Project Plan). This is the single place K9Crush's real rules — architectural, testing, and operational — are named as rules, with each one traced back to where it actually came from and whether it's mechanically enforced today or just declared.

**Status legend**: `enforced` = a real script runs this check today. `declared, not yet enforced` = a real rule, honestly not yet checked by anything.

---

## Tier A — Architectural rules

Sourced from `AGENTS.md` and `TestingApproach/TestingApproach.md` — these already govern every agent session on this codebase; this section names them as constitutional rather than leaving them scattered as separate convention docs.

### A1 — Slice organization by lane
Each feature/domain lives under its module's `Api` project, organized by lane — `Commands/<Name>/`, `ReadModels/<Name>/`, or `Automations/<Name>/` — never a flat `Features/` folder.
**Why**: the scaffold's first pass used a flat `Features/` folder and was deliberately replaced (`AGENTS.md`).
**Status**: declared, not yet enforced. (Future home: `K9Crush.ArchitectureTests`/NetArchTest, per `Infra/inventorylist.md` — not this eval-harness oracle.)

### A2 — Validation via DataAnnotations only
`System.ComponentModel.DataAnnotations` attributes and `IValidatableObject` for cross-field rules. Never a separate `AbstractValidator<T>`/FluentValidation class.
**Why**: Wolverine.Http endpoints bypass FluentValidation's pipeline entirely — confirmed live, not a style preference (`AGENTS.md`, blueprint doc Section 5.1).
**Status**: declared, not yet enforced.

### A3 — Handler naming
Any class with a public static `Handle` method must have a class name ending in `Handler`, even when the file is named after a trigger event (a projector) rather than the handler itself.
**Why**: Wolverine's convention-based discovery silently skips anything else — this has broken a real slice before (`AGENTS.md`, blueprint doc Section 5.2).
**Status**: declared, not yet enforced.

### A4 — Event-sourcing everywhere
One storage strategy for every entity, every module — no per-module or per-slice document-store-vs-event-sourced decision.
**Why**: a real hybrid retrofit surfaced two costs a single strategy avoids — a decision that has to be re-verified on every slice, and silent staleness when a written-down "mostly X, except Y" rule drifts from the code (`build-state-change/SKILL.md` Step 2).
**Status**: declared, not yet enforced.

### A5 — The Layer 2 / Layer 3 test-routing line
A handler belongs in Layer 2 (mocked) if it only touches `FetchForWriting`/`FetchForExclusiveWriting`/`AppendOptimistic`/`AggregateStreamAsync`. It belongs in Layer 3 (Testcontainers) the moment it touches `session.Query<T>()` — Marten's LINQ provider doesn't mock.
**Why**: this is a hard technical boundary, not a judgment call — confirmed directly against Marten's actual mockability, re-verified after ADR-031 (`TestingApproach.md`).
**Status**: declared, not yet enforced.

### A6 — `slice.json` is authoritative
Code follows what's defined in a slice's `slice.json`, never the reverse. No invented fields, event names, or business rules not present there.
**Why**: the entire spec-to-oracle propagation this constitution's own Tier B rules depend on only works if this holds (`AGENTS.md` "Building a Slice"; `build-state-change/SKILL.md`).
**Status**: partially enforced — Tier B's checks verify specification *coverage*, which presupposes this rule; nothing yet verifies the reverse (code containing fields/rules absent from `slice.json`).

---

## Tier B — Spec-to-oracle rules (WS3)

Built and verified this session (WS3.3–3.5). Named here as constitutional rather than left as standalone scripts with no governing document.

### B1 — Specification coverage
Every specification in a slice's wired `specificationsFile` must have a corresponding test.
**Why**: the mechanical gate previously trusted the agent's own self-reported "Done" status with no independent check — a real, observed false-green class (WS1: "25% of mechanically-passing runs had silent bugs the gate missed").
**Status**: **enforced** — `check-spec-coverage.sh <sliceFolder>`.

### B2 — Specification drift
A slice's specification content must not silently diverge from its last confirmed baseline without that being flagged.
**Why**: B1 only catches count changes (a specification added/removed with no matching test) — an *existing* specification's `given`/`when`/`then` text can change with the count untouched, and B1 alone would stay silent.
**Status**: **enforced** — `check-spec-drift.sh <sliceFolder>` (baseline recorded via `record-spec-baseline.sh`, a deliberate, separate action — never automatic, so a baseline can't silently absorb drift before anyone sees it).

---

## Tier C — NFRs / SLOs

New for WS4.2, grounded in real, current gaps (`RejectApplication`, WS3.3–3.5) and the board-vocabulary mapping (WS4.1) — not generic boilerplate.

### C1 — No duplicate rejection
A rejection attempt against an application that is not currently under review (already rejected, not found, or any other terminal/invalid state) must never produce a second `ApplicationRejected` event.
**Why**: this is the exact gap WS3.4's demo surfaced (specification 3, currently uncovered) and WS4.1's operability mapping named as a missing guard — an Outcome that should be unreachable, declared here *before* the code closes it.
**Status**: **enforced (as a failing check)** — `check-constitution.sh RejectApplication`, which reports this rule violated right now. That's correct: the rule is real, the gap is real, and the constitution should say so rather than stay silent until the gap happens to get closed. **Alert provisioned** (WS4.4, `deploy/observability/alerts.yml`, Grafana rule `constitution-c1-duplicate-rejection`), **not yet able to fire** — no OpenTelemetry instrumentation in `RejectApplicationHandler` emits an application-id-tagged event/metric yet. The alert exists so it's not a forgotten follow-up once instrumentation lands, not a live check today.

### C2 — `ApplicationRejected` is observable
Every emission of `ApplicationRejected` (and its cascade to Notifications) is logged with the application id and timestamp, so a *missing* expected rejection can be detected, not just a wrong one.
**Why**: the observability half of WS4.1's mapping — every Outcome is a candidate failure mode, and a failure mode that can't be observed can't be alerted on. Sets up WS4.4 (LGTM) with a concrete, already-declared target.
**Status**: declared, not yet enforced. Verifying real log output needs a running, observable instance — out of scope for this eval harness's static checks. **Alert provisioned** (WS4.4, `deploy/observability/alerts.yml`, Grafana rule `constitution-c2-missing-rejection`), **not yet able to fire** — same root cause as C1: no OpenTelemetry logging/metrics exist yet for `ApplicationRejected` emissions.

---

## Observability stack (WS4.4)

The `otel-lgtm` service in `deploy/compose/docker-compose.yml` (`grafana/otel-lgtm` — Grafana + Loki + Tempo + Mimir + an OTLP receiver bundled in one image, the known-good setup this project's inventory doc already named as its target, not a bespoke alerting engine) was verified running for real this session: brought up via `docker compose up -d`, Grafana's `/api/health` endpoint confirmed responding at `localhost:3000`, then the container recreated with `deploy/observability/alerts.yml` mounted into its alert-provisioning path (`/otel-lgtm/grafana/conf/provisioning/alerting/`, confirmed via direct image inspection, not assumed) and re-verified healthy. Its own compose comment cites "ADR-010" for the production topology it stands in for — that ADR, like every other ADR referenced across this codebase's comments (010/024/031/002/005), **doesn't exist as a file anywhere in the repo**. Flagged here, not solved — the same class of gap WS4.1 found for the constitution itself before this file existed.

## Enforcement summary

Run `./check-constitution.sh <sliceFolder>` for the mechanically-checkable subset (B1, B2, C1). Tier A and C2 have no automated check yet — visible here as a to-do, not hidden by omission. C1/C2 both have a provisioned Grafana alert (WS4.4) that cannot yet fire, for the same reason: no OpenTelemetry instrumentation exists in the product code today.
