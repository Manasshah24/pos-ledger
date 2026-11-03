# Design — Offline-first POS ledger with gift card protection

*(Written to the brief: component diagram, data model, consistency model, failure modes, and what was deliberately left out. Where the build departed from the original plan, the departure and its reason are listed at the end.)*

## The hard core, named first

1. **Exactly-once *effect* on at-least-once delivery.** Duplicates, reordering and 48-hour-late events must change nothing twice.
2. **Bounding offline gift card overspend**, against customer friction.
3. **Convergent loyalty balances** whatever the arrival order, and what a negative balance means.
4. **A rebuild that gives byte-identical answers**, every time, from the log alone.

## Components

```
 tills / terminals ──HTTP──►  nginx  ──►  api1 ─┐
 (offline up to 6 h,                      api2 ─┼──►  PostgreSQL 17
  upload batches, retry)                         │     (the event log AND the only coordinator)
                                                 │
 ops dashboard (static page) ◄── served by the API, reads the same endpoints
```

* **Ledger.Core** — pure rules, no I/O: event validation, business-day rule, the day-delta arithmetic, the `Projector` (a pure function from the log to every read model) and the canonical hashes.
* **Ledger.Data** — everything that touches Postgres: ingest, day close, gift cards, loyalty, rebuild and snapshots. Plain SQL through Npgsql.
* **Ledger.Api** — thin HTTP layer plus a background day-close scheduler. Replicas are stateless: no in-process lock, queue or cache is ever the source of truth, so any replica can be killed at any time.

## Data model

| Table | Role |
|---|---|
| `events` | **The log.** One row per distinct event, `event_id` unique (the idempotency key), `log_seq` = ingest order. Typed columns for the hot fields (`sale_id`, `amount_minor`, `card_id`, `lease_id`, `account_id`, `points`, `business_date`). All money is `bigint` minor units. |
| `rejected_events` | Every refused event with its raw payload and reason. Nothing is dropped silently. |
| `stores` | Time zone and `last_closed_date` (days close in order, so "closed" is one date). |
| `store_day_totals` | Read model: sales / returns / voids per store-day, plus the adjustment columns that landed there. |
| `adjustments` | One row per late event for a closed day, linked `original_date → applied_date` (the next open day). |
| `parked_events` | Returns/voids whose sale has not arrived. |
| `day_closures` | Append-only; `log_seq_mark` places each close in the log so a replay can put it in the right place. |
| `giftcards`, `giftcard_leases`, `giftcard_flags` | Balance, store leases (secured + unsecured), every rule broken. |
| `loyalty_accounts` | Earned and burned totals per account (a PN-counter). |
| `snapshots` + `snapshot_*` | Verified copies of the read models at a log position. |

## Consistency model

Postgres is the arbiter; there is no other shared state.

* **Idempotency.** `INSERT … ON CONFLICT (event_id) DO NOTHING`; only rows that were *actually inserted* are projected. A retry, a duplicate, or the same batch on both replicas has no second effect. The same id with different content is recorded as a conflict and the first version wins.
* **Atomic batches.** One transaction per batch: log rows and every read-model change commit together. A replica killed mid-batch leaves nothing; the terminal resends. Readers see a batch entirely or not at all.
* **Deadlock freedom by lock order.** Everything takes locks in one global order: store advisory locks (shared for ingest, exclusive for close/rebuild) → event rows (sorted by id) → per-sale advisory locks → total rows (sorted) → card/account rows (sorted). The online gift-card and loyalty paths insert the event first, then the card/account row, to match.
* **Day close.** `last_closed_date` plus a per-store advisory lock. A close waits for in-flight batches and makes later ones queue behind it (an exclusive advisory request blocks new shared ones, so a steady stream cannot starve a close). A late event for a closed day is booked as an adjustment into the next open day; a DB trigger refuses any write to a closed day's totals.
* **Orphan returns.** A return/void is applied only once its sale (same store) is in the log, otherwise parked; a new sale releases what waits for it, in the same transaction. A per-(store, sale) advisory lock stops a return and its sale, committing on different replicas at once, from missing each other.
* **Gift cards.** A store takes a *lease* before going offline: a secured part carved out of the card's balance plus an unsecured part carved out of the card's overspend allowance `L`. Invariants: `Σ secured outstanding ≤ balance` and `deficit + Σ unsecured outstanding ≤ L`. Terminals redeem only up to their lease, online redemption may only spend what is unreserved (so it is exact), hence **final balance ≥ −L**. Money stays reserved until lease expiry **plus the 48 h maximum upload delay**. Every rule break is a flag (`overspend`, `no_lease`, `exceeds_lease`, `expired_lease`). *Friction trade-off:* a card with no lease cannot be redeemed offline by a compliant terminal, and leased money is frozen for up to ttl + 48 h; raising `L` buys fewer declines with more fraud exposure.
* **Loyalty.** Earned and burned totals only grow and only newly inserted events are added, so the balance is a sum over the *set* of distinct events: commutative, associative, idempotent. Offline burns are always accepted (the purchase already happened); a negative balance is reported, repaid by later earns, and blocks *online* burns.
* **Rebuild.** `Projector` replays the log in `log_seq` order, with each recorded close at its position, producing the same rows live ingest produced. Tests require the two independent implementations to agree. Hashes are canonical (sorted, integers, ISO dates, SHA-256). A rebuild can start from a **snapshot** that is verified against its recorded hash before use and falls back to the whole log if it fails.

## Failure modes

| Failure | Handling |
|---|---|
| Same batch sent twice / to both replicas | `ON CONFLICT DO NOTHING`; zero effect the second time |
| Replica `kill -9` mid-request | Transaction rolls back; terminal retries on the other replica; tested with real processes |
| Return arrives before its sale | Parked, released in the same transaction as the sale; visible in `/parked`; never dropped |
| Event 47 h late for a closed day | Adjustment in the next open day, linked to the original day; closed total untouched (DB trigger as backstop) |
| Close racing a stream of ingest | Per-store advisory lock; close lands between batches; tested mid-run on two replicas |
| Two stores redeem a card offline | Bounded by leases + `L`; every excess flagged |
| Replica unreachable / bad database | Replica refuses to start (migrations at startup, advisory-locked so several can start together) |
| Query during ingest | One statement = one snapshot; never a partly applied batch |
| Snapshot fails mid-write / is corrupted | Written in one transaction so a partial one never exists; verified by hash before use, else full replay |
| Malformed event | Rejected individually with its raw payload; the rest of the batch commits |

## Deliberately left out

* Real payment, PCI, authentication and multi-tenancy; multi-currency conversion.
* A terminal client: the offline *rules* (leases, limits) are specified and enforced by the ledger, not shipped as till software.
* A message broker: HTTP batch ingest plus Postgres meets the need with far fewer moving parts.
* Sharding or partitioning the log; releasing a lease early when a store reports back; gift card top-ups; snapshot scheduling and retention; gift card state in the rebuild hash.
* A returns-at-another-store flow (a sale is identified by *store* + sale id).

## Where the build changed course from the plan (and why)

* **Live ingest and rebuild are two implementations, not one shared path.** Live is incremental SQL (cheap per batch); the rebuild is a pure replay. The tests demand they agree — stronger evidence than one code path agreeing with itself.
* **Sale identity became `(store, sale_id)`** — receipt numbers repeat across stores, and it makes each store's log independent.
* **Per-store advisory locks, not a row lock, for day close** — a `FOR UPDATE` row lock can be starved by a steady stream of shared lockers; a queued exclusive advisory lock cannot.
* **Day close is off by default** (`Ledger:AutoCloseIntervalSeconds`): replaying old simulated timestamps against the real clock would otherwise close everything. Compose turns it on.
