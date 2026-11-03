# POS Ledger — offline-first point-of-sale ledger with gift card protection

A central ledger for a grocery chain (300 stores, 2,400 terminals) whose terminals go offline for hours and upload buffered events later — duplicated, out of order and up to 48 hours late. It produces correct daily totals per store, closes business days without ever rewriting them, bounds offline gift card overspend with a hard configurable limit, and keeps loyalty balances convergent.

* Design: [docs/design.md](docs/design.md) · How to verify every claim: [docs/judge-guide.md](docs/judge-guide.md) · Build log and decisions: [PROJECT_CHECKLIST.md](PROJECT_CHECKLIST.md)
* Stack: C# / .NET 10 (minimal API), PostgreSQL 17, Npgsql with plain SQL, xUnit + FsCheck, nginx.
* AI assistance: this project was built with an AI coding assistant (Claude Code). Every design decision and its reasoning is written down in the design doc and the build log.

## Run it

```bash
docker compose up --build        # postgres + 2 API replicas + nginx
# dashboard:  http://localhost:8080          health: http://localhost:8080/health
```

> Verified on Docker Desktop: the images build, Postgres/both replicas report healthy, nginx serves the API and the dashboard, and killing a replica container mid-traffic loses nothing (see the build log, Stage 10). The day-close scheduler is on (every 60 s); start with `AUTO_CLOSE_SECONDS=0 docker compose up` to close days only through the admin endpoints, e.g. when replaying old simulated timestamps against the real clock.

Without Docker (needs a local PostgreSQL 17):

```bash
export ConnectionStrings__Ledger="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=ledger"
dotnet run --project src/Ledger.Api --urls http://localhost:8080
```

## Test it

```bash
export LEDGER_TEST_PG="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres"   # any Postgres you may create databases on
dotnet test tests/Ledger.Core.Tests          # pure rules, property tests
dotnet test tests/Ledger.Harness.Tests       # the seeded data generator and the independent oracle
dotnet test tests/Ledger.Integration.Tests   # real Postgres: concurrency, 2 replicas, day close, rebuild, gift cards, loyalty, snapshots, chaos
dotnet run -c Release --project bench/Ledger.Bench -- 1000000 20000   # rebuild benchmark
```

Integration tests create and drop their own databases and fail (not skip) if `LEDGER_TEST_PG` is not set. The test classes run one at a time on purpose; see the notes in the build log.

## API at a glance

| | |
|---|---|
| `PUT /stores/{id}` `{tz,currency}` | register a store (its time zone decides its business day) |
| `POST /events/batch` `{events:[…]}` | idempotent ingest of `sale`, `return`, `void`, `giftcard_redeem`, `loyalty_earn`, `loyalty_burn`; per-event rejects reported by position |
| `GET /totals[?storeId=]`, `GET /stores/{id}/days/{yyyy-MM-dd}` | store-day totals (own, closed, adjustments in, corrections, corrected net) |
| `GET /parked` | returns/voids still waiting for their sale |
| `POST /admin/stores/{id}/close?through=yyyy-MM-dd`, `POST /admin/close-due` | close business days |
| `GET /admin/hash`, `POST /admin/rebuild[?useSnapshot=true]`, `POST /admin/snapshot` | fingerprints, rebuild from the log, snapshots |
| `PUT/GET /giftcards/{id}`, `POST /giftcards/{id}/leases`, `POST /giftcards/{id}/redeem`, `GET /giftcards/flags` | gift cards, store leases, exact online redemption, rule-break flags |
| `GET /loyalty/{id}`, `GET /loyalty/negative`, `POST /loyalty/{id}/burn` | loyalty balances and exact online burns |

Event fields: `eventId` (the idempotency key), `storeId`, `terminalId`, `type`, `occurredAt` (ISO 8601 **with** offset), then `saleId` + `amountMinor` (sales, returns, voids), `cardId` + `amountMinor` + optional `leaseId` (gift card), `accountId` + `points` (loyalty). Money is a JSON integer in minor units; `12.5` or `"500"` is rejected.

Configuration (`Ledger:` section / `Ledger__…` environment variables): `BusinessDayCutoffHour` (2), `MaxLateHours` (48), `OfflineUnsecuredLimitMinor` (0 = strict), `AutoCloseIntervalSeconds` (0 = off; compose sets 60).

## Known limits

* **The panel's harness was not available while building.** Its event format, endpoints and checksum format are unknown; the project was built against its own seeded generator and an independent oracle that mirror the five Definition-of-Done checks. Expect a small adapter in `Ledger.Api` (and possibly the day-close and rebuild triggers) when the real harness is in hand.
* **Playwright UI test cut**; the dashboard is a thin read-only page. The panel's harness has not been run against the compose stack.
* Rebuild of 10,000,000 events is **extrapolated**, not measured (1M measured: 6.8 s full, 1.0 s from a snapshot). Snapshots are on demand; no schedule or retention.
* Gift card guarantee holds for terminals that obey their lease; one that does not is flagged but cannot be stopped. Gift card state is not in the rebuild hash. Returns at a different store than the sale are not supported.
* A return larger than its sale's remaining amount is not flagged.

## What I would do next with another day

1. Plug in the panel harness (adapter + endpoint names), run the full DoD suite against compose, and repeat the chaos run with several seeds.
2. Early lease release when a store reports back; gift card top-ups; gift card state in the rebuild and hash.
3. Snapshot scheduler + retention; measure the rebuild at 10M events.
4. Over-refund detection; cross-store returns through a receipt lookup.
5. Playwright smoke test for the dashboard; metrics endpoint.
