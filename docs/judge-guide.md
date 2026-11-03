# Judge guide — verifying each claim

Everything runs from a clean clone. The integration tests need a PostgreSQL 17 you may create databases on:

```bash
export LEDGER_TEST_PG="Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=postgres"
dotnet test tests/Ledger.Integration.Tests --filter "FullyQualifiedName~<name below>"
```

## Definition of Done (Problem 2)

| # | Requirement | Where it is proven |
|---|---|---|
| DoD-1 | 100,000 events (12% duplicates, shuffled within 48 h, 3% orphan returns, 20 outages): every store-day total matches the checksum | `ChaosTests` — the full 100k profile through **two real API processes**, compared with the independent oracle (`Ledger.Harness/Oracle.cs`) by hash. Scaled-down variants with the same profile: `BatchApiTests.Harness_stream_through_two_replicas…`, `DayCloseTests.Harness_stream_with_a_mid_run_close…` |
| DoD-2 | Ingest while totals are queried at ~50 req/s; no query sees a partly applied batch | `ChaosTests` (HTTP polling at 50/s + a REPEATABLE READ check that sales in the log always equal sales in the totals, across a replica kill) and `TotalsTests.A_reader_never_sees_a_partly_applied_batch…` |
| DoD-3 | After day close, events appear only as adjustments; the closed day's stored total never changes | `DayCloseTests` (late events → adjustments linked to the original day; the closed row is byte-identical; a DB trigger refuses writes to a closed day; close racing two replicas mid-ingest) |
| DoD-4 | 40 cards redeemed at once at 2–5 offline stores: overspend ≤ limit, every overspend flagged | `GiftCardTests.Forty_cards_redeemed_at_two_to_five_offline_stores…` (also checks the balance equals the log, flags equal the real deficit, and that rogue terminals are caught) |
| DoD-5 | Drop and rebuild the read models → same SHA-256 | `RebuildTests` (wipe → rebuild → identical hashes and API answers; independent pure replay agrees with live ingest), `SnapshotTests` (rebuild from a snapshot equals rebuild from the whole log) |

## Levels

* **L1** idempotent ingest on 2 replicas (`EventIngestorTests`, `BatchApiTests`), correct net daily totals (`TotalsTests`), orphan returns parked and resolved (`ParkingTests`).
* **L2** day close + adjustments (`DayCloseTests`), provable gift card limit (`GiftCardTests`; invariants stated in `docs/design.md`), identical rebuild hash (`RebuildTests`).
* **L3** convergent loyalty with a negative policy (`LoyaltyTests`; seeded random order/duplication runs), snapshots (`SnapshotTests`, benchmark in `PROJECT_CHECKLIST.md` Stage 8).

## Hard-core properties as properties

* `ProjectorSeedTests` — resuming from a snapshot state equals replaying everything (100 random histories with orphans, closes, loyalty).
* `ProjectorTests` — arrival order never changes totals when no day has closed.
* `BusinessDayTests` — an instant is in a business day exactly when it is before that day's end, across DST in six zones.

## Try it by hand

```bash
dotnet run --project src/Ledger.Api --urls http://localhost:8080      # with ConnectionStrings__Ledger set
curl -X PUT  localhost:8080/stores/S1 -H 'content-type: application/json' -d '{"tz":"Asia/Kolkata"}'
curl -X POST localhost:8080/events/batch -H 'content-type: application/json' -d '{"events":[{"eventId":"e1","storeId":"S1","terminalId":"T1","type":"sale","occurredAt":"2026-01-05T10:00:00Z","saleId":"s1","amountMinor":1250}]}'
curl localhost:8080/totals
curl -X POST "localhost:8080/admin/stores/S1/close?through=2026-01-05"
curl -X POST localhost:8080/admin/rebuild            # identical = true
```

Open `http://localhost:8080/` for the dashboard (totals, parked returns, gift card flags, loyalty debt).

## Things a reviewer should push on

* The gift card bound is conditional on terminals obeying their lease, and money is reserved for lease expiry + 48 h — see "Friction trade-off" in the design doc.
* Live ingest and the rebuild are intentionally separate implementations that must agree.
* The harness format is an assumption (Q1–Q7 in `PROJECT_CHECKLIST.md`); the real harness will need an adapter.
