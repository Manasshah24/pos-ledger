# Problem 2 — Offline-first POS Ledger with Gift Card Protection

Master checklist. Tick `[x]` only when the stage's unit + automation tests are green.

**Legend:** `[ ]` todo · `[~]` in progress · `[x]` done (tests green) · **L1/L2/L3** = level from the PDF · **DoD** = Definition of Done item
**Source:** `build-round-problems_.pdf`, Problem 2.

> **Stage gate rule:** a stage is closed only when (a) its unit tests pass, (b) its automation/integration tests pass against real Postgres, and (c) all earlier stages' tests still pass (full regression). No exceptions, no moving forward on red.

---

## PART A — Everything the problem statement requires from us

### A1. Functional requirements (from "Requirements")

| ID | Requirement | Level | Done |
|----|-------------|-------|------|
| FR-1 | Ingest batches of events: `sale`, `return`, `void`, `giftcard_redeem`, `loyalty_earn`, `loyalty_burn` | L1 | [ ] |
| FR-2 | Tolerate duplicates, out-of-order arrival, up to 48h late events | L1 | [ ] |
| FR-3 | A return can arrive before its sale → park it, resolve when the sale arrives | L1 | [ ] |
| FR-4 | Correct **net daily totals per store** | L1 | [ ] |
| FR-5 | Day close at 02:00 per store: day is closed and reported | L2 | [ ] |
| FR-6 | Late events never change a closed day; they create **adjustment entries in the next open day, linked to the original day** | L2 | [ ] |
| FR-7 | Offline gift card redemption with a **configurable hard limit** on worst-case overspend | L2 | [ ] |
| FR-8 | Online gift card redemption stays **exact** (never overspends) | L2 | [ ] |
| FR-9 | Loyalty earn/burn offline at several stores; merged balances converge in any arrival order | L3 | [ ] |
| FR-10 | All money is **integer minor units** (no floats/decimals anywhere) | L1 | [ ] |
| FR-11 | Any read model can be **rebuilt from the event log** | L1/L2 | [ ] |

### A2. The four "hard core" problems (what the panel scores hardest)

| ID | Hard core | Our chosen answer (see Part B) | Done |
|----|-----------|-------------------------------|------|
| HC-1 | Exactly-once *effect* over at-least-once delivery, for **every** event type | Natural idempotency key + unique constraint + same-transaction projection | [ ] |
| HC-2 | Bound offline gift-card overspend; state the friction trade-off | Store-level escrow leases + per-card unsecured offline limit | [ ] |
| HC-3 | Convergent loyalty balances + negative-balance policy | Per-origin PN-counter (G-Set of events), negative allowed + flagged | [ ] |
| HC-4 | Rebuild gives byte-identical result every time | Log-sequence-ordered deterministic replay; day-close is an event in the log | [ ] |

### A3. Levels

| Level | Item | Done |
|-------|------|------|
| **L1 (pass bar)** | Idempotent ingest on 2 replicas | [ ] |
| | Correct net daily totals per store | [ ] |
| | Orphan returns parked and resolved | [ ] |
| **L2 (strong)** | Day close with adjustment entries | [ ] |
| | Gift card scheme with a **provable** overspend limit | [ ] |
| | Read-model rebuild gives identical hash | [ ] |
| **L3 (exceptional)** | Convergent loyalty balances with negative-balance policy | [ ] |
| | Snapshots so rebuild is fast at 10M events | [ ] |

(Level bonus: L2 complete ≤ +5, any L3 item working ≤ +5, total capped at 100.)

### A4. Definition of Done — the harness checks

| ID | Check | Done |
|----|-------|------|
| DoD-1 | Replay **100,000 events**: 12% duplicates, shuffled within 48h, 3% orphan returns, 20 simulated store outages → **every store-day total matches the harness checksum exactly** | [ ] |
| DoD-2 | Ingest runs while totals are queried at **50 req/s**; no query returns a **partly applied batch** | [ ] |
| DoD-3 | Events after day close appear **only as adjustments**; closed day's stored total never changes | [ ] |
| DoD-4 | **40 cards** redeemed concurrently at **2–5 offline stores**; overspend per card ≤ configured limit; **every overspend is flagged** | [ ] |
| DoD-5 | Drop and rebuild read models → **same SHA-256** over the totals table | [ ] |

### A5. Deliverables

| ID | Deliverable | Done |
|----|-------------|------|
| DEL-1 | **Design doc (1–2 pages)**: component diagram, data model, consistency model, failure modes + handling, what was deliberately left out. **Locked at 25 min**; deviations explained in demo | [ ] |
| DEL-2 | Code with automated tests on the **hard core** (concurrency, money, ordering), not just happy path | [ ] |
| DEL-3 | `docker compose up` starts everything | [ ] |
| DEL-4 | README: how to run, known limits, what we'd do next with another day | [ ] |
| DEL-5 | One Git repo, **commits at least every 30 min** (history is evidence) | [ ] |
| DEL-6 | Zip `Name_P2.zip` → hr.india@aptos.com | [ ] |

### A6. Rules & constraints

- [ ] Any language/framework; any OSS datastore/broker that runs in Docker
- [ ] **Do not modify the panel's harness** (generator, mock services, load script, invariant checker)
- [ ] Service runs with **≥ 2 replicas**; no in-memory locks/queues/caches as source of truth
- [ ] If an AI assistant is used, **say so**; we must be able to explain/defend every line; design doc is our own reasoning
- [ ] **Automatic fail:** any broken invariant in the live run (money doesn't add up, lost event, oversell-style bug) unless we found and explained it first → *we run our own invariant checks continuously*

### A7. Scoring map (where marks come from)

| Dimension | Weight | What earns a 4 | Our plan |
|-----------|-------:|----------------|----------|
| Architecture & design | 25 | Hard core named in first 10 min; consistency model chosen on purpose w/ failure modes; diagram matches build | Part B decisions written *before* code; diagram kept in sync |
| Correctness of hard core | 30 | Invariants pass under concurrency + chaos; tests that try to break it | Stages 5–9: concurrency/chaos/property tests |
| Code quality & tests | 15 | Clear modules, small surface, risk-aimed tests, no dead abstractions | Small module set (Part D), no speculative abstractions |
| Trade-offs & scoping | 15 | Cuts scope on purpose, states the risk, defends under pushback | "Out of scope" list in Part B, updated as we go |

> Note: the PDF's four weights sum to 85, not 100. Pass = 60+ and every L1 item met. We treat all four as equally critical and aim for L1+L2 fully, L3 as stretch.

---

## PART B — Requirements analysis (retail domain)

### B1. Domain model (what these events mean at a grocery till)

| Event | Retail meaning | Effect on store-day net |
|-------|----------------|-------------------------|
| `sale` | Completed basket paid at a terminal (lines, tenders, total) | `+ total` |
| `return` | Customer refund referencing an earlier sale (receipt) — full or partial | `− refund_amount` |
| `void` | Cashier/manager cancels a sale (post-tender void) | `− original sale total` (reverses it entirely) |
| `giftcard_redeem` | Stored-value card used as a tender | **Tender**, not revenue — affects card balance + tender report, *not* net sales (the `sale` already counted the revenue) |
| `loyalty_earn` / `loyalty_burn` | Points accrued / spent on an account | Affects loyalty balance only, never money totals |

Real-world facts we must respect:
- A terminal's clock is untrusted but the **event's `occurred_at` + `store_id`** decide the business day. Business day = store-local time with a **02:00 cut-off** (a 01:30 sale belongs to *yesterday's* business day).
- Stores are in different time zones → `stores` table carries `tz`.
- A `void` can also arrive before its sale (same orphan problem as returns) — we park **both**.
- A return may exceed the remaining refundable amount of a sale (double refund) — we **flag**, never silently drop.
- Money moves in **minor units (paise/cents)**, `BIGINT`, currency code stored per store.

### B2. Open questions — the harness format is unknown until the panel hands it over

These shape the code; we design so the answer only touches one adapter layer.

| # | Question | Our working assumption (reversible) |
|---|----------|-------------------------------------|
| Q1 | What uniquely identifies an event (UUID? `terminal_id + seq`)? | Every event carries an `event_id` (UUID/string) that is stable across retries; dedupe key = `event_id` |
| Q2 | What identifies the business day in the harness checksum: event `occurred_at`, or arrival-based after close? | `occurred_at` in store-local time with 02:00 cut-off; late-after-close → adjustment in next open day (linked to original). Checksum keys *original* day + adjustment rows separately |
| Q3 | Is "now" a real clock or a harness-controlled simulated clock for day close? | Inject an `IClock` — day close is triggered by a command/clock, never by `DateTime.Now` inside logic |
| Q4 | How do terminals behave offline for gift cards — does the harness implement a lease protocol or just emit redeem events? | We define the terminal rules (lease + offline limit) **and** can also detect overspend purely from ingested events (works either way) |
| Q5 | Currency / rounding rules | Single currency per store, no rounding needed (already minor units) |
| Q6 | Does `void` reference sale by `sale_id`? Does `return` carry `sale_id` + amount or line items? | Reference by `sale_id`; amount given in the event |
| Q7 | How do store ids and their time zones reach us? | Stores are registered via `PUT /stores/{id}` before events; events for unknown stores are rejected (`unknown_store`), never silently bucketed in a guessed zone |

### B3. Key design decisions (draft — to be frozen into the 1–2 page design doc)

**D1 — Event log is the source of truth.** Append-only `events` table, one row per *unique* event, plus a monotonically increasing `log_seq` (global ingest order). Everything else is a projection.

**D2 — Exactly-once effect (HC-1).** `UNIQUE(event_id)` + `INSERT … ON CONFLICT DO NOTHING`. Only rows that actually inserted are projected, **inside the same DB transaction**. Duplicate → no insert → no projection → no effect. Works identically on 2 replicas because the unique index is the arbiter (no in-memory state). Natural idempotency, so no separate "idempotency key" table needed for event ingest.

**D3 — Whole batch atomic (DoD-2).** One transaction per batch: insert events → apply projections → commit. Readers use `READ COMMITTED` on committed projections only, so a query sees the batch entirely or not at all. Per-event poison isolation: a malformed event goes to a `rejected_events` table (batch still commits the good ones) — decision to confirm: *reject whole batch vs. per-event*. Default: per-event reject + report.

**D4 — Out-of-order & orphans (FR-3).** Returns/voids referencing an unknown `sale_id` are inserted in the log and recorded in `parked_events(ref_sale_id)`. When a `sale` is ingested, in the same transaction we look up `parked_events` for that `sale_id` and apply them. Convergence property: *final state is independent of arrival order.*

**D5 — Business-day assignment & day close (FR-5/6).**
- `business_date(event)` = store-local date of `occurred_at − 2h`.
- `store_days(store_id, business_date, status OPEN|CLOSED, closed_at_seq)`.
- Day close writes a `day_closed` marker **into the log** (with its own `log_seq`), freezes the total.
- If an event's business date is already CLOSED → its effect is booked as an **adjustment** row in the store's *next open* business day: `adjustments(store_id, applied_date, original_date, event_id, delta)`. Closed day's stored total is never UPDATEd (enforced by DB trigger/guard + test).
- Race: close vs. ingest concurrently → serialize per `(store_id, business_date)` with a row lock (`SELECT … FOR UPDATE` on `store_days`), not a global lock.

**D6 — Deterministic rebuild (HC-4 / DoD-5).** Replay events **ordered by `log_seq`** (day-close markers are in the same sequence) through the *same* projection functions. Because day-close position is recorded in the log, a rebuild decides "closed vs adjustment" identically. No wall-clock, no randomness, no dictionary-iteration order in projection code. Hash = SHA-256 over the totals table serialized in a canonical sorted order (sorted by `store_id, business_date, kind`).
- *Caveat to defend:* rebuild order is **log order**, not `occurred_at` order, for exactly this reason.

**D7 — Gift cards: hybrid escrow lease + capped offline limit (HC-2, FR-7/8).**

- *Online:* `giftcard_accounts.balance` updated with a conditional `UPDATE … SET balance = balance − :amt WHERE balance >= :amt` → **exact, no overspend** (row-level atomic, safe on 2 replicas).
- *Offline lease (secured):* before going offline (or periodically online), a store requests a **lease** `L(card, store)` carved out of the card's balance (`available = balance − Σ active leases`). Leases are debited from `available`, so the sum of all leases ≤ balance. A store redeeming ≤ its lease can **never** overspend.
- *Offline limit (unsecured):* a card presented at a store with **no lease** may still be redeemed offline up to a configurable `OFFLINE_UNSECURED_LIMIT` per card (global across stores: the *pool* is granted via the same lease table so Σ unsecured grants ≤ limit). This is the **hard bound**: `max overspend per card ≤ OFFLINE_UNSECURED_LIMIT` (configurable; 0 = strict, no offline use without lease).
- **Provable bound:** `overspend(card) ≤ Σ unsecured grants(card) ≤ LIMIT`. Test proves it under N-store concurrency.
- Reconciliation at ingest: redemptions are applied; if balance goes negative → mark `overspend_flag` with the amount. Overspend (shortfall) is flagged and recorded as a loss, **never** hidden.
- *Friction trade-off (to state in the doc):* higher lease/limit → fewer declines at a store with stale info, more fraud exposure; limit = 0 → zero exposure but customers declined offline.
- Lease expiry: unspent lease returns to the pool at reconcile/expiry (e.g. ≤ 6h, matching max offline time).

**D8 — Loyalty: convergent by construction (HC-3, FR-9).** Per account, per origin (terminal/store) keep `earned_total` and `burned_total` — a **PN-counter**: merge = per-origin `max`, balance = `Σ earned − Σ burned`. Equivalent and simpler since events are already uniquely identified: balance = sum over the *set* of distinct events (a G-Set of deltas) → commutative, associative, idempotent → converges for any arrival order and duplicates. **Negative policy:** allow the ledger to go negative (offline burn can't be refused retroactively), **flag** it, and let future earns pay it down; online burn is rejected if it would go negative. Decision documented as friction/fraud trade-off.

**D9 — Snapshots for 10M-event rebuild (L3).** Periodic snapshot of projections at `log_seq = N` (stored with its hash); rebuild = load latest snapshot + replay events with `log_seq > N`. Must prove `rebuild_from_snapshot hash == rebuild_from_zero hash`.

**D10 — Concurrency model across 2 replicas.** All coordination in Postgres (unique indexes, row locks, conditional updates, advisory locks per store-day if needed). **No** in-process locks/caches as truth. Replicas are stateless behind a load balancer.

### B4. Failure modes → handling

| Failure | Handling |
|---------|----------|
| Same batch sent twice / to both replicas | `ON CONFLICT DO NOTHING`; second batch has zero effect |
| Replica killed mid-batch | Single transaction → rolls back; terminal retries whole batch; idempotent |
| Return before sale | Parked; auto-resolved on sale arrival (same txn) |
| Return never gets its sale | Stays parked; surfaced in `/parked` report with age (not silently dropped) |
| Event 47h late for a closed day | Adjustment in next open day, linked to original date |
| Close and late ingest race | Row lock on `(store, date)`; whoever commits first defines the outcome, and the log order records it deterministically |
| Two stores redeem same card at once offline | Bound by lease/limit; overspend flagged at reconcile |
| Query during ingest | Transaction isolation → sees whole batch or none |
| Rebuild during live traffic | Rebuild into shadow tables, hash-verify, swap — *or* run offline in tests (decide at stage 7) |
| Malformed event | `rejected_events` + reason; rest of batch unaffected |
| Clock skew on terminal | Business date from `occurred_at` but ordering/closing authority is `log_seq` |

### B5. Explicitly out of scope (cuts we will defend)

- Real payment/PCI handling, auth/tenancy, multi-currency conversion
- Terminal-side software (we define & simulate the offline *rules*; no real till client)
- Full-blown UI product (only a thin demo/ops dashboard — see C3)
- Kafka/broker: not needed; Postgres is log + coordination (fewer moving parts, 2-hour budget)
- Partitioning/sharding of the log (mention as "next with another day")

---

## PART C — Tech stack analysis

### C0. What is already on this machine (checked)

| Tool | Version | Verdict |
|------|---------|---------|
| .NET SDK | 10.0.402 | ✅ **our runtime** |
| Node.js / npm | 24.14 / 11.9 | ✅ ready |
| Python | 3.14.3 | ✅ ready (scripts) |
| Docker + Compose | 29.8 / v5.5 | ✅ ready |
| Git | 2.50 | ✅ ready |
| pgAdmin 4 | installed | ✅ for inspecting data |
| Visual Studio, Cursor, Postman | installed | ✅ |
| Docker engine | **installed but not running** | needs start; may need admin/WSL. Not required to start coding |
| Go / Java / Maven / psql / Redis / k6 | missing | ❌ not needed |

### C1. Decisions (final: C# / .NET 10, because it needs zero admin installs)

**Why .NET now:** Go's installer needs admin rights and the LA account password is expired. .NET 10 SDK is already installed and works, NuGet is reachable (nuget.org returns 200), and the language matters less than people think here: Postgres does the hard work (unique index = idempotency, row locks = lease/close races, transactions = atomic batches). C# gives `long` money, good concurrency tooling and fast development. Go stays documented as the alternative we considered.

| Concern | Choice | Why / rejected alternatives |
|---------|--------|-----------------------------|
| **Language / framework** | **C# on .NET 10, ASP.NET Core Minimal API** | already installed, no admin; strong typing for money |
| **Database** | **PostgreSQL 17** | unique-index idempotency, `ON CONFLICT`, row locks, tx atomicity, JSONB, `BIGINT`. Rejected: Kafka (ops for no gain), Redis (not durable truth), SQLite (single-writer, no multi-replica), RabbitMQ/NATS (ingest is HTTP batch, no queue need) |
| **How Postgres runs** | **Docker container if the engine works; otherwise portable Postgres `.zip` binaries run as a normal user (no admin, no install)** | removes Docker as a single point of failure. The app only needs a connection string |
| **Data access** | **Npgsql + Dapper** (raw SQL) | correctness lives in SQL; EF Core would hide what judges want to see |
| **Migrations** | versioned `.sql` files, embedded resources, tiny own runner | visible, reviewable |
| **Unit tests** | **xUnit** (plain asserts) | standard. FluentAssertions v8+ needs a paid licence, so not used |
| **Property-based tests** | **FsCheck (FsCheck.Xunit)** | proves: any permutation + duplication of events gives the same state/hash (HC-1/3/4) |
| **Integration/automation** | **`WebApplicationFactory` x2 (= 2 replicas in one test) against real Postgres.** Connection comes from env var `LEDGER_TEST_PG` if set (portable or Docker Postgres), else **Testcontainers** spins one up | real DB semantics; mocks hide the bugs that matter |
| **Chaos** | `docker compose kill` script if Docker works; otherwise kill + restart a second server process mid-batch | matches the DoD |
| **Own harness** | `Ledger.Harness` console project: seeded generator (100k, 12% dups, 3% orphans, shuffle within 48h, 20 outages) + oracle + 50 rps query runner | panel harness not on this machine; adapter added later |
| **Load** | in-harness runner (50 rps queries); optional k6 via Docker | nothing to install |
| **Reverse proxy** | **nginx** in compose (Docker, later stage); in dev, tests call replicas directly | standard |
| **UI** | **Thin ops dashboard: static HTML + vanilla JS (Chart.js), served by the API** | rubric doesn't score UI; no build step; cut-able |
| **UI automation** | **Playwright** (`npm install` into the project, user-level), small smoke suite, optional | validates dashboard shows live totals/flags |
| **API exploration** | Postman (installed) / `.http` files | demo |
| **Observability** | Serilog structured logs + `/health` + JSON counters | enough for failure-modes story |
| **CI (later)** | GitHub Actions: build, unit, integration | when pushed to your GitHub |

**Admin-needing items:** only Docker Desktop (engine currently not running; may need `docker-users` group / WSL2). Everything else installs at user level or is a NuGet/npm package. We are NOT blocked on admin credentials to start Stages 0-9; Docker is needed only for Stage 10 (compose) and optionally Testcontainers.

### C2. Money & determinism rules (code conventions)

1. Money is `long` minor units end to end; `NUMERIC` never; no `double/decimal` in money paths.
2. Projection code is **pure**: input = events in `log_seq` order, output = rows. No `DateTime.Now`, `Guid.NewGuid()` or unordered iteration inside.
3. Time via an injected `IClock`.
4. All coordination in the DB; replicas stateless.

### C3. UI scope decision

Judges score design/correctness/tests; UI earns nothing directly. We build **one read-only dashboard page + a "simulate" button panel** *only after* all L1/L2 stages are green. If time is short it is the first thing cut (listed in B5).

---

## PART D — Proposed solution layout (.NET)

```
pos-ledger/
├─ PROJECT_CHECKLIST.md          ← this file
├─ docs/                         ← design doc, judge docs, diagrams
├─ src/
│  ├─ Ledger.Api/                ← minimal API, endpoints, static dashboard, composition root
│  ├─ Ledger.Core/               ← pure domain: events, business-day rules, projections, gift card/loyalty rules
│  └─ Ledger.Data/               ← Npgsql/Dapper, SQL migrations, transactions
├─ tests/
│  ├─ Ledger.Core.Tests/         ← unit + property-based (FsCheck)
│  ├─ Ledger.Integration.Tests/  ← real Postgres, 2 server instances, concurrency, rebuild hash
│  └─ Ledger.Harness/            ← our seeded generator + oracle + load/chaos runner
├─ deploy/                       ← docker-compose.yml, nginx.conf (later)
└─ README.md
```
Small surface: 3 src projects, no repository-per-entity abstractions, no dead interfaces.

---

## PART E — Coding stages (each ends with a test gate)

Time budget assumes the real round is 120 min; stages are ordered so **L1 → L2 → L3 → polish** and anything unfinished is a deliberate cut.

### Stage 0 — Foundation & harness skeleton
- [x] Solution + 3 src + 4 test projects build; `.gitignore`; first commit
- [x] Postgres available (portable 17.2 in `tools/`, port 54329, `scripts/pg-start.sh`); tests read `LEDGER_TEST_PG`
- [x] Migration runner applies embedded SQL at startup (advisory-locked, replica-safe). Docker compose deferred to Stage 10
- [x] `IClock`/`ManualClock`, `LedgerOptions` (cut-off hour, late limit, offline limit)
- [x] Our harness generator v1 (seeded, sale/return/void) + oracle + canonical SHA-256 (unit-tested itself)
- [x] `/health` endpoint
- **Gate:** build green; migration test (real Postgres) green; generator determinism test (same seed → same output) green
- **Result: PASSED** — 26 unit tests (4 core, 22 harness) + 8 integration tests (5 migrator incl. 8 concurrent replicas, 3 health incl. 2 replicas) green

### Stage 1 — Event log & idempotent ingest *(L1, HC-1)*
- [x] Schema: `events`, `stores`, `rejected_events`; `UNIQUE(event_id)`; `log_seq` (migration `0002_events.sql`)
- [x] `POST /events/batch` — validation, per-event reject (reason + raw payload kept), single batch transaction
- [x] Duplicate handling (`ON CONFLICT DO NOTHING`), response reports `inserted` / `duplicates` / `rejected[{index,eventId,reason}]`
- [x] Same id + different content = `event_id_conflict` (first version wins, conflict recorded)
- [x] `PUT /stores/{id}` store registry (unknown store rejected; time-zone change refused with 409)
- [x] Business-day function (02:00 cut-off, store tz, DST spring-forward/fall-back)
- **Gate (unit):** validation, business-date function (incl. 02:00 edge, DST, tz) · **(automation):** same batch ×N → 1 effect; 2 replicas ingest same batch concurrently → 1 effect; replica killed mid-batch → no partial rows; property test: any duplication/permutation → same log set
- **Result: PASSED** (trimmed to risk-focused tests) — 52 tests green across all projects (Core 23, harness 10, integration 19). Hard-core coverage: same batch ×5 = 1 effect; 8 concurrent senders over 2 ingestors = each event once; opposite-order overlapping batches don't deadlock; injected mid-batch crash rolls back everything and the retry lands once; seeded random split/repeat/shuffle gives an identical log; 2 HTTP replicas on the same batch insert once; and a 20k-delivery harness stream (dups, orphans, outages) through 2 replicas gives log totals hash-equal to the independent oracle
- **Decisions made in this stage**
  - Rows are inserted sorted by `event_id` so overlapping batches lock in the same order (deadlock-free, proven by test); `log_seq` is therefore ingest order across batches, not arrival order within a batch.
  - Lateness (48h) is **not** enforced as a rejection: the panel's harness replays old simulated timestamps against a real clock, and the requirement is to *accept* late events.
  - Timestamps must carry an explicit offset (`Z` or `+hh:mm`); naive times are rejected as ambiguous. Money must be a JSON integer (`"500"` and `12.5` are malformed).
  - Real `kill -9` of a replica process is covered in Stage 9 chaos; here the same property is proven with an injected DB error mid-transaction.
  - New open question **Q7**: how stores/time zones reach the ledger (we require `PUT /stores/{id}` first; the panel's harness may need an adapter or a seed list).

### Stage 2 — Projections: net store-day totals + atomic visibility *(L1, DoD-2)*
- [x] `store_day_totals` projection (sales / returns / voids / generated net, integer minor units); gift card tender breakdown deferred to Stage 6
- [x] Projection applied **in the same transaction** as insert; only *newly inserted* events add to it (duplicates never double count)
- [x] `GET /totals[?storeId=]` (one-statement snapshot) and `GET /stores/{id}/days/{yyyy-MM-dd}`
- **Gate:** unit tests for each event type math · concurrency: ~50 rps queries during ingest through 2 replicas, every response a whole-batch state (never partial) · money is integer-only
- **Result: PASSED** — 25 core + 21 integration (+10 harness unchanged). New risk tests: totals per business day incl. 01:59:59 vs 02:00:00, replayed batch changes nothing; reader polling ~50 rps via one replica while 2 replicas ingest 60 batches (every observed sum a multiple of the batch size; a REPEATABLE READ check that log sum == totals sum never differs); crash test also asserts totals never moved; the 20k harness stream now compares **served totals** hash to the oracle
- **Decisions:** return/void are booked to the business day of their own `occurredAt` (matches oracle); totals rows are upserted in (store, day) order so concurrent batches can't deadlock. Stage 3 will change returns/voids so they are *held* until their sale is known (today they apply immediately).

### Stage 3 — Orphan returns/voids parking *(L1, FR-3)*
- [x] `parked_events`; a return/void whose sale is not in the log is stored in the log but **held out of the totals**; a new sale releases everything parked for it, in the same transaction
- [x] `GET /parked` report (event, type, store, sale, amount, parked-at), oldest first; a return whose sale never arrives stays visible
- [x] Race-safe: per-sale advisory locks (fixed lock order: events, sale locks, totals rows) so a return and its sale committing on different replicas at the same instant can't miss each other
- **Gate:** return-before-sale, void-before-sale, same batch any order, duplicate while parked / after release, 150 sale+return pairs racing across 2 replicas leave nothing stuck, and the 20k harness stream (with its orphan returns) still matches the oracle with `/parked` empty at the end
- **Result: PASSED** — 25 core + 10 harness + 25 integration. Integration test classes now run one at a time (parallel classes exhausted local Postgres connection setup; concurrency *inside* each test is unchanged)
- **Not done / deferred (not in the DoD):** flagging a return that exceeds its sale's remaining amount (Stage 2 gate text mentioned it; it was not built). Duplicate sale ids with different event ids are not rejected.
- **Decision:** totals are independent of parking order — once every sale has arrived, the result equals the oracle; only never-resolved orphans are excluded.

### Stage 4 — Day close & adjustments *(L2, FR-5/6, DoD-3)*
- [x] Close model: a store's days close in order, so "closed" is one date (`stores.last_closed_date`). `POST /admin/stores/{id}/close?through=yyyy-MM-dd` (idempotent, never reopens) and `POST /admin/close-due` (clock-driven: closes each store's days that have ended per its own time zone). A background scheduler runs close-due on every replica but is **off by default** (`Ledger:AutoCloseIntervalSeconds`, 0 = off) so a replay of old simulated timestamps against the real clock can't close everything by accident
- [x] Close is recorded in the log order: `day_closures` stores `log_seq_mark` (highest log_seq of that store at close), so Stage 5's rebuild can replay the close in the right place
- [x] Late events (day already closed) never touch the closed day: they become rows in `adjustments` (linked `original_date` → `applied_date` = next open day) and add to that open day's `adj_*` columns
- [x] `GET /totals` now returns per store-day: own `netMinor` (frozen on close), `closed`, `adjustmentsInMinor` (landed here from earlier days), `correctionsMinor` (belongs here, landed later), and computed `reportedNetMinor` / `correctedNetMinor`
- [x] DB guard: a trigger refuses any insert/update on a closed day's totals row, so even a bug in app code cannot change it
- [x] Orphans released after a close are booked as adjustments too
- **Gate:** late event → adjustment with the closed day byte-unchanged · cascade (several days closed → lands in the next open day) · close racing 2 replicas mid-ingest · DoD-3 style harness run
- **Result: PASSED** — 27 core (incl. day-end-vs-business-day property over 6 zones with DST) + 10 harness + 33 integration. Harness test: 60% of a 10k stream → close every store through Jan 6 → remaining 40% (late events) → closed days identical to their close-time snapshot, adjustments exist, and corrected totals equal the oracle; money conserved (reported sum == corrected sum); `/parked` empty
- **Decisions / findings**
  - Race protection is a **per-store advisory lock** (ingest shared, close exclusive, held to commit). A queued exclusive lock makes later ingests wait behind it, so a close cannot be starved by a steady stream of batches. The close deliberately takes no `FOR UPDATE` row lock on `stores`.
  - The race test first "failed" because opening a new Postgres connection took 1.6 s on this machine (longer than the whole run), not because of locking; the test now pre-warms the pool.
  - "Which total is the harness's checksum" is still open (Q2): we expose own, reported and corrected so either reading can be matched.
  - Ambiguous wall-clock cut-offs (fall-back hour) use the first occurrence in both `For` and `EndUtc`; verified by the property test.
- **Note for Stage 5:** rebuild must write totals into shadow tables (the guard trigger would block rewriting closed days in place).

### Stage 5 — Deterministic rebuild & hash *(L2, HC-4, DoD-5)*
- [x] Pure replay engine `Projector.Replay(events in log_seq order, recorded closes)`: no clock, randomness or database; encodes park / release / close-then-adjust
- [x] `POST /admin/rebuild` (replaces totals, adjustments and open parked rows from the log in one transaction; returns before/after hashes + `identical`) and `GET /admin/hash`
- [x] Canonical SHA-256 per read model (totals incl. adjustment columns, adjustments, still-parked) + an overall hash; same functions fingerprint DB tables and an in-memory projection
- **Gate:** rebuild ×N → identical hash · live hash == rebuilt hash on a harness stream with orphans, dups, outages and a mid-run close · includes adjustments & parked/resolved · property test over random event sets
- **Result: PASSED** — 30 core + 10 harness + 36 integration. Key tests: live tables (built incrementally by ingest) vs the pure replay of the log agree on all three hashes; 3 rebuilds identical and `/totals` byte-for-byte unchanged; **TRUNCATE all read models → rebuild → same fingerprint and same API answers**; 2 replicas ingesting while rebuilds run → every rebuild identical, no events lost, final state == replay
- **Decisions / deviations (to defend)**
  - **Not literally one code path.** The checklist said live and rebuild would share code. Live ingest is incremental SQL (cheap per batch); making it share the replay would mean loading state per event. Instead the two are separate implementations and the tests require them to agree, which is stronger evidence than one piece of code agreeing with itself.
  - **Sale identity is now `(store_id, sale_id)`.** Receipt numbers repeat across stores, and it makes each store's log an independent world, so replay and locking are per store. A return at a different store than its sale is out of scope. (Locks, known-sale lookup and parked release all changed; Stage 3/4 tests still pass.)
  - Rebuild holds every store's advisory lock exclusively, so ingest and close queue for the (short) duration; reads are not blocked. A session flag `ledger.rebuild` lets the closed-day guard trigger allow the rewrite, only inside the rebuild transaction.
  - Rebuild keeps only *still-open* parked rows (who was released when is history, not state) and stamps rebuilt adjustments with their event's receive time, so hashes cover state only, never wall-clock bookkeeping.
  - The whole log is read into memory to replay: fine for 100k, not for 10M. That is exactly what Stage 8 (snapshots) is for.
  - DoD-5's "SHA-256 over the totals table" is hashed in **our** canonical format; the panel's table format is still open (Q7/Q2). `Oracle.Hash` remains the cross-check for per-day corrected totals.

### Stage 6 — Gift card escrow & overspend bound *(L2, HC-2, FR-7/8, DoD-4)*
- [x] Schema: `giftcards` (central balance, may go negative), `giftcard_leases` (secured + unsecured, spent), `giftcard_flags`; events carry `card_id` / `lease_id`
- [x] **Online redeem is exact:** `POST /giftcards/{id}/redeem` approves only money not reserved by a lease, under a card row lock, idempotent on `eventId` (same id + different money = conflict, nothing deducted twice)
- [x] **The scheme (mix of store-level escrow and a per-card limit):** a store takes a *lease* before going offline = secured part carved out of the balance + unsecured part carved out of the card's overspend allowance `L` (`OfflineUnsecuredLimitMinor`, default 0 = strict). Invariants kept under concurrency: `Σ secured outstanding ≤ balance` and `deficit + Σ unsecured outstanding ≤ L`. Terminals redeem offline only up to their lease, so final balance ≥ −L.
- [x] **Reservation outlives the lease:** money stays reserved until lease expiry **+ the 48h maximum upload delay** (a store can report spending that late); releasing it earlier could let it be spent twice. Friction trade-off: leased money is frozen up to ttl+48h unless spent/reported.
- [x] Offline redemptions (`giftcard_redeem` via `/events/batch`) are applied in log order at ingest; every rule break is a flag: `overspend` (part below zero), `no_lease`, `exceeds_lease`, `expired_lease`. Unknown cards are auto-registered at zero (the sale happened) and flagged, never dropped.
- [x] `PUT/GET /giftcards/{id}`, `POST /giftcards/{id}/leases`, `GET /giftcards/flags`
- **Gate:** 1000-style concurrent online redeems never go negative · 40 cards × 2–5 stores concurrent offline → overspend ≤ limit, all flagged (**DoD-4**) · limit=0 and limit>0 · lease expiry returns funds
- **Result: PASSED** — 31 core + 10 harness + 42 integration (6 new). Key tests: 100 online attempts (16 at a time) on 5,000 → exactly 50 approved, balance exactly 0, retry/reuse handled; 20 concurrent lease requests reserve exactly the balance (5,000) and exactly the limit (2,000), never more; single-card walk-through with leases, an unleased store, an over-spender and an expired-lease spender (every cent of overspend flagged, with its cause); **40 cards, 2–5 stores each, competing for leases, duplicated + shuffled uploads through 2 replicas: compliant cards ≤ limit, balance == log, flagged overspend == actual deficit, rogue terminals caught**; gift events don't disturb the totals rebuild; HTTP walk-through
- **Decisions / limits to defend**
  - The bound holds for terminals that obey their lease. A terminal that doesn't can overspend, but is visible (`exceeds_lease` / `no_lease`) and the cards it touched are the only ones allowed past the limit. This is stated as the trust model.
  - A card with no lease cannot be redeemed offline by a compliant terminal (maximum safety, maximum friction). Raising `L` trades fraud exposure for fewer declines.
  - Not done: releasing a lease early when the store reports back, top-ups, and rebuilding gift card state from the log (balance is verified equal to *issued − Σ redemptions in the log* in the 40-card test, but it is not part of the Stage 5 rebuild/hash yet).
  - Tests cap concurrency at 16 because opening a fresh connection to this local Postgres is slow; they test the logic, not connection storms.

### Stage 7 — Loyalty convergence *(L3, HC-3, FR-9)*
- [x] `loyalty_earn` / `loyalty_burn` events (`accountId`, `points`); `loyalty_accounts` = two grow-only totals (earned, burned) = a **PN-counter**; balance = earned − burned, summed over the *distinct* events in the log, so any merge order and any repeat give the same balance (commutative, associative, idempotent)
- [x] **Negative-balance policy:** offline burns are always accepted (the purchase already happened), the account may go negative, `GET /loyalty/negative` lists it, later earns pay the debt down automatically, and an **online** burn (`POST /loyalty/{id}/burn`, exact, idempotent on `eventId`) is refused unless the balance covers it, so an account in debt cannot spend until it is back in credit. No clawback.
- [x] Loyalty is part of the Stage 5 replay, rebuild and hashes (a fourth hash `loyalty`, folded into `overall`)
- **Gate:** any permutation + duplicates → identical balance · negative balance flagged, recovers with earns · online burn rejected when it would go negative · included in rebuild hash
- **Result: PASSED** — 33 core + 10 harness + 47 integration (5 new). Key tests: 6 seeded runs of 80 events over 6 accounts and 3 stores, with random repeats, shuffled, cut into random batches and uploaded concurrently through 2 replicas → every account equals the sum of its distinct events; two offline stores each burning 80 of a 100 balance → −60, reported, blocks online burns, repaid by a later earn; 40 concurrent online burns of 10 on a balance of 100 → exactly 10 approved, balance 0; truncate loyalty + totals → rebuild restores both exactly; HTTP walk-through
- **Decisions:** "negative" is a reported state, not a per-event flag, so it does not depend on arrival order (unlike gift card overspend flags). Online loyalty burns and online gift card redemptions share one small event-appender helper, so both take locks in the same order as batch ingest (events first, then the account/card row) and cannot deadlock with it.

### Stage 8 — Snapshots for fast rebuild at 10M events *(L3)*
- [ ] Snapshot table (`log_seq`, hash); rebuild = snapshot + tail
- [ ] Synthetic 10M-event load benchmark (documented timing)
- **Gate:** snapshot-rebuild hash == full-rebuild hash · crash-safe snapshot (partial snapshot never used) · benchmark result recorded in docs

### Stage 9 — Full-system chaos & load verification
- [ ] Run our 100k harness against **2 replicas** behind nginx: DoD-1..5 as one automated suite
- [ ] `kill -9` / `docker kill` a replica mid-ingest → invariants hold
- [ ] 20 store outage simulation, 50 rps query load
- **Gate:** entire DoD suite green, repeated 3× with different seeds

### Stage 10 — Docker, deployment & UI polish
- [ ] `docker compose up`: postgres, 2× api, nginx — healthchecks
- [ ] Dashboard (static) + Playwright smoke test (optional, cut-able)
- **Gate:** clean-machine `compose up` → suite runs green against it

### Stage 11 — Judge documentation & delivery
- [ ] Design doc (1–2 pages) finalized to match what was built (diagram = reality)
- [ ] README (run, limits, next-day plan), judge guide (how to verify each DoD item), trade-off log
- [ ] Plug in panel's real harness via adapter; fix format deltas (Q1–Q6)
- [ ] Push to your GitHub (+ CI workflow); commit history sanity check
- [ ] Build `Name_P2.zip` → hr.india@aptos.com
- **Gate:** fresh clone → `docker compose up` → tests green

---

## PART H — What the PDF says about the harness (all of it)

The PDF mentions the harness only in a few places; there is **no harness on this machine** (searched Desktop/Downloads, only unrelated Harness.io CI keys). Everything below comes from the PDF.

**Rules (apply to every problem)**
- The panel provides a **starter harness per problem: data generator, mock external services, load script and an invariant checker.**
- **Candidates must not change the harness.**
- "Harness invariants pass under concurrency and chaos" is what earns a 4 on Correctness (30%).
- **Automatic fail** = an invariant broken in the *live harness run* (money doesn't add up, a lost event...), unless we found it and explained the cause before the panel did.

**Problem 2 — what the harness does (from Definition of Done)**
1. Replays **100,000 events**: 12% duplicates, shuffled within 48 h, 3% orphan returns, 20 simulated store outages, then compares **every store-day total to its checksum**.
2. Runs ingest **while querying totals at 50 req/s** and checks no response shows a partly applied batch.
3. Sends events **after day close** and checks they appear only as adjustments and the closed day's stored total is unchanged.
4. Gift-card scenario: **40 cards, redeemed concurrently at 2-5 offline stores**; checks overspend per card is at or below the configured limit and every overspend is flagged.
5. Drops/rebuilds read models and compares **SHA-256 over the totals table** (so our totals table must exist in a form the checker can read and hash; format TBD).

**What the PDF does NOT tell us (read on day 1, first minutes of the round)**
- Language/runtime of the harness and how it is run (CLI? docker image? against a base URL?)
- Is our API contract fixed by the harness (endpoint paths, event JSON schema, auth) or do we define it and the harness adapts via config?
- How it reads results: HTTP query endpoints vs. direct DB access vs. a totals export/table with a specific name and columns (matters for the SHA-256 check)
- Event identity fields, the business-day rule it uses for its checksum, how it signals "store closed" / the simulated clock
- How it triggers day close and rebuild (endpoints? CLI?). We expose `admin` endpoints.
- Whether it simulates the gift-card lease protocol on the terminal side or only emits redeem events
- No "mock external services" are described for P2 (those matter for payments/partners in other problems)

**Our mitigation:** keep an **adapter layer** (in `Ledger.Api` + a mapping config) so a contract mismatch costs minutes, and build our own harness (`Ledger.Harness`) mirroring the 5 checks above so we're never blind. Open questions Q1-Q6 in Part B are exactly this list.

---

## PART F — Progress log

| Date | Stage | Result | Notes |
|------|-------|--------|-------|
| 2026-11-02 | Analysis | Part A–E written | Awaiting instructions to start Stage 0 |
| 2026-11-02 | Stack revisit | Briefly moved to Go; reverted to C#/.NET 10 + Postgres because Go install needs admin (LA password expired) | Part H (harness) added; can start Stage 0 without admin |
| 2026-11-02 | Stage 0 | PASSED (34 tests) | Portable Postgres 17.2 in tools/; oracle is independent of service code |
| 2026-11-02 | Stage 1 | PASSED (52 tests, trimmed) | 100k harness stream via 2 replicas = oracle totals at log level |
| 2026-11-02 | Stage 2 | PASSED (25 core + 10 harness + 21 integration) | Totals projected in the ingest transaction; atomic visibility verified through 2 replicas |
| 2026-11-02 | Stage 3 | PASSED (25 core + 10 harness + 25 integration) | Parking + release in the ingest transaction; sale-lock race test across 2 replicas |
| 2026-11-02 | Stage 4 | PASSED (27 core + 10 harness + 33 integration) | Per-store advisory lock; adjustments linked to original day; DB guard on closed days |
| 2026-11-03 | Stage 5 | PASSED (30 core + 10 harness + 36 integration) | Pure replay == live incremental on all read models; wipe+rebuild identical; sale identity scoped per store |
| 2026-11-03 | Stage 6 | PASSED (31 core + 10 harness + 42 integration) | Leases (secured+unsecured), reservation = expiry+48h, overspend flags; 40-card concurrent scenario within limit |
| 2026-11-03 | Stage 7 | PASSED (33 core + 10 harness + 47 integration) | PN-counter loyalty balances; negative policy; included in rebuild hash |

## PART G — Decisions awaiting your input

- [x] Stack decided: **C# / .NET 10 + PostgreSQL 17 + Npgsql/Dapper + xUnit/FsCheck** (no admin needed). Go kept as documented alternative
- [ ] Postgres source: portable zip (no admin) or Docker once engine works
- [ ] Confirm UI = thin static dashboard, late and cut-able
- [ ] Per-event reject vs whole-batch reject on a malformed event (default: per-event)
- [ ] Repo location/name (default: `C:\Users\mshah\Desktop\pos-ledger`, GitHub later)
- [ ] Do we disclose AI-assistant use to the panel (rule says say so)