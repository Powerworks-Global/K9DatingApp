# Project Plan — "K9Crush" Dog Owner Platform

## 1. Document Purpose
This plan defines scope, phasing, timeline, team structure, and risks for building a .NET modular monolith backend (vertical slice architecture, Marten/PostgreSQL, RabbitMQ, Redis) with a Blazor frontend, deployed to Kubernetes via GitHub Actions.

**Revision note:** the original plan scoped a dating-app MVP (8 modules, ~19 weeks). The feature set has since expanded to a full dog-owner platform — social feed, playdate scheduling, local services/booking, e-commerce, lost & found, and shelter/adoption — roughly doubling the module count. This revision re-phases delivery into **release trains** instead of extending the original phase list, so each train ships a coherent, usable product rather than a pile of half-finished modules.

## 2. Assumptions

| # | Assumption |
|---|---|
| A1 | Modules in scope (15): Identity, Profiles, Discovery/Matching, Scheduling, Community, Chat, Places (local services/directory), Shop, Lost & Found, Shelter & Adoption, Content, Notifications, Media, Subscriptions, Moderation — see Inventory doc for the per-module breakdown |
| A2 | Target runtime: .NET 10 LTS (moved from .NET 8 - Marten/Wolverine dropped net8.0 support; see ADR-020) |
| A3 | Blazor Web App (Interactive Server + WASM "auto" render mode) is the frontend, hosted inside the same solution |
| A4 | Single Postgres cluster, one Marten-managed schema per module (logical separation); PostGIS enabled from day one (ADR-014) |
| A5 | Target container platform: **Kubernetes**, decided (ADR-006) |
| A6 | Team size: **6–9 engineers** — grown from the original 4–6 to cover the marketplace/payments and services-booking surface area (2–3 backend, 1 frontend/Blazor, 1 DevOps/platform, 1 QA, 1 PM, and — new — 1 engineer with payments/compliance experience for the Shop train) |

## 3. Objectives
- Ship the core dating/matching/chat experience first as a real, usable product — not gated behind the full platform vision.
- Layer social, scheduling, marketplace, and civic (lost & found, adoption) features on top without re-architecting, using the module-boundary discipline already established.
- Establish a repeatable, automated path from commit to deployed environment, reused unchanged by every release train.
- Keep module boundaries clean enough that any module — Shop and Lost & Found are the most likely candidates — could be extracted into its own service without a rewrite.

## 4. Scope by Release Train

Each train is a shippable increment; trains run mostly sequentially given team size, though Content/Notifications work threads through all of them rather than being its own train.

| Train | Adds | Rationale |
|---|---|---|
| **A — Core Dating MVP** | Identity, Profiles, Discovery/Matching, Chat, Notifications (basic), Media (photos), Subscriptions (stubbed payment), Moderation (basic) | The original MVP scope, unchanged - still the right first thing to ship |
| **B — Social & Scheduling** | Community (feed, likes/follows, groups), Scheduling (playdate scheduler, events/meetups), Notifications (reminders via scheduled messages) | Turns matching into an ongoing relationship, not just a swipe - natural next layer once matching/chat work |
| **C — Local & Marketplace** | Places (local services directory + booking, dog-friendly cafes/bars), Shop (catalog, cart, checkout, payments), Media (video + transcoding) | The highest-complexity train - real payments (ADR-015), PostGIS-backed search, and a two-sided marketplace (vendor accounts) all land here |
| **D — Civic & Rescue** | Lost & Found, Shelter & Adoption | Lower technical risk than Train C, but real-world urgency (lost dogs) means Moderation/Trust & Safety maturity from Train A must already be solid before this ships |
| **Ongoing (all trains)** | Content (training/health tips) + Newsletter | Deliberately not custom-built - a lightweight CMS plus a third-party email platform (ADR-018), threaded in wherever cheap rather than scheduled as its own train |

### Explicitly out of scope for now
- Native mobile apps (Blazor is web-first; PWA is a stretch goal)
- In-app video calling
- Multi-region active-active deployment
- Custom-built search engine (Postgres full-text/PostGIS is sufficient until Community or Shop catalog size proves otherwise)

## 5. Team & Roles

| Role | Responsibility |
|---|---|
| Tech Lead / Architect | Module boundaries, ADRs, code review gate on cross-module changes |
| Backend Engineers (2-3) | Vertical slices, Marten schema, Wolverine handlers |
| Payments/Compliance Engineer (Train C onward) | Stripe integration, PCI-relevant handling, vendor payout flows |
| Frontend Engineer | Blazor components, SignalR client, UX |
| DevOps/Platform Engineer | Kubernetes operators (RabbitMQ, Redis, OTel), Helm, GitHub Actions, observability stack, Supabase project administration |
| QA Engineer | Test strategy, integration/E2E suites, release sign-off |
| Product/PM | Backlog, acceptance criteria, per-train sign-off |

## 6. Phased Delivery Plan

| Phase | Train | Duration | Goal |
|---|---|---|---|
| 0 | - | 2 weeks | Finalize module boundaries, ADRs, environment strategy, repo scaffolding |
| 1 | - | 3 weeks | Platform foundation: solution skeleton, self-hosted Postgres/PostGIS (ADR-047) with its own backup story, Supabase Cloud project setup (Storage/Auth), RabbitMQ/Wolverine, Redis, Grafana LGTM, docker-compose dev env, base CI pipeline |
| 2-5 | A | 10 weeks | Core dating MVP - Identity, Profiles, Discovery/Matching, Chat, Notifications, Media, Subscriptions, Moderation |
| 6 | A | 2 weeks | Beta & launch prep for Train A: soak test, production CD, runbooks |
| 7-9 | B | 7 weeks | Community feed, Scheduling (playdates + events), reminder notifications |
| 10-13 | C | 9 weeks | Places (directory + booking), Shop (catalog/cart/checkout/Stripe), video media pipeline, vendor accounts |
| 14-16 | D | 6 weeks | Lost & Found, Shelter & Adoption |
| 17 | - | 2 weeks | Cross-train hardening: load test the now-much-larger system, security review (payments + location-sharing surfaces), Moderation maturity pass |

**Total estimate: ~41 weeks (approx. 9.5 months)** for Trains A-D with the grown team, vs. the original 19-week MVP-only estimate. Contingency of 20% recommended given the payments/compliance and marketplace-trust surfaces are new territory for the team. **Train A alone can ship independently at week 21** if you want a real product live before committing to the rest.

### Milestone Gantt (indicative, weeks)

```
Phase 0-1  |=====|
Train A          |============|
Train B                        |=======|
Train C                                |=========|
Train D                                          |======|
Hardening                                                |==|
Week:      1  3  5  7  9  11 13 15 17 19 21 23 25 27 29 31 33 35 37 39 41
```

## 7. Deliverables per Train

| Train | Key Deliverables |
|---|---|
| 0-1 | Architecture doc set, ADR log, buildable solution, Kubernetes cluster with all operators live, first green pipeline |
| A | Identity/Profiles/Discovery/Chat/Notifications/Media/Subscriptions/Moderation modules; production deploy of the dating MVP |
| B | Community + Scheduling modules; scheduled-reminder notifications; playdate/event flows end-to-end |
| C | Places + Shop modules; Stripe integration live; vendor account onboarding; video upload + transcoding |
| D | Lost & Found + Shelter/Adoption modules; geo-broadcast alerting |
| 17 | Load test report across all trains, security review sign-off, updated runbooks |

## 8. Risks & Mitigations

| Risk | Impact | Likelihood | Mitigation |
|---|---|---|---|
| Module boundaries leak as module count nearly doubles | High | Medium | Architecture fitness tests (NetArchTest) in CI, expanded per ADR-008's slice-discipline rules |
| Payments/PCI scope creep or misconfiguration (Train C) | High | Medium | Use Stripe Checkout/Elements (tokenized, K9Crush never touches raw card data); dedicated payments/compliance engineer; external security review before Train C launch |
| Vendor marketplace trust & fraud (fake listings, scam sellers) | High | Medium | Vendor verification step before listing goes live; reviews/ratings ship with Places/Shop, not after |
| Trust & safety incidents from location-sharing (playdates) or lost & found | High | Medium | Moderation module matures during Train A, not deferred; panic/report button ships with Scheduling in Train B, not left to Train D |
| RabbitMQ/outbox misconfiguration causes lost events | High | Medium | Transactional outbox via WolverineFx.Marten; integration tests for event delivery |
| Team unfamiliarity with vertical slice/Event Modeling discipline at 15-module scale | Medium | Medium | Blueprint doc (05-event-modeling-blueprint.md) as onboarding material; pair programming on the first slice of each new module |
| Video transcoding costs/complexity (Train C) | Medium | Medium | Start with a simple async FFmpeg worker job; revisit managed transcoding service if volume grows |
| Full self-hosted data/observability/identity stack (6 stateful systems) under a growing feature set | Medium | Medium | Already flagged in Solution Architecture doc Section 7.2 - re-confirm ops capacity before Train C, since marketplace uptime expectations are higher than a social app's |
| Scope creep beyond the 4 trains | Medium | High | New feature ideas go into a Train E backlog, not into an active train mid-flight |

## 9. Definition of Done (per module)
- Vertical slices implemented with unit + integration tests (>80% coverage on domain logic), each slice tagged state-change/state-view/automation per ADR-008
- Marten document/event schema documented
- Published/consumed integration events documented in the module's README
- OpenAPI spec generated and reviewed
- Passes architecture fitness tests in CI
- Dashboards/alerts wired for new module's key metrics in Grafana

## 10. Success Metrics

| Train | Metric |
|---|---|
| A | P95 API latency < 300ms (discovery/profile); chat delivery < 500ms P95; commit-to-staging-deploy < 15 min |
| B | Playdate scheduling completion rate; reminder delivery reliability > 99% |
| C | Checkout success rate > 98%; booking confirmation latency < 2s; payment webhook processing < 1 min P95 |
| D | Lost-dog alert broadcast latency < 30s to all owners within radius |
| All | Zero cross-module compile-time references outside published contracts |
