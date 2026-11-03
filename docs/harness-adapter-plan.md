# Plan: adapting the ledger to the panel's harness

**Status: not started.** The panel's starter harness (data generator, mock services, load script, invariant checker) was not available while this was built. Everything was verified against our own seeded generator and an independent oracle (`tests/Ledger.Harness`) that mirror the five Definition-of-Done checks in the problem statement. This document is the plan for closing that gap once the real harness is in hand. The harness itself must not be modified; only our side adapts.

## Step 0 — read it (first 10 minutes)

Read the harness README, config and one sample payload. Answer these seven questions, which are Q1–Q7 in `PROJECT_CHECKLIST.md`:

| # | Question | Our current behaviour | If it differs |
|---|---|---|---|
| Q1 | How are events sent (URL, method, JSON field names, auth)? And what identifies an event? | `POST /events/batch` with `{events:[…]}`, camelCase fields, `eventId` is the idempotency key | Add routes / accept aliases in `Ledger.Api`; map `id` / `event_id` / `terminal+seq` to `eventId` in `Ledger.Core/Events/BatchParser.cs` and `IncomingEvent` |
| Q2 | Which business day does the checksum use, and are post-close events counted on their original day? | Own day by `occurredAt` in store-local time with a 02:00 cut-off; `/totals` returns own, reported and corrected net | Choose the field the checksum matches (`netMinor`, `reportedNetMinor` or `correctedNetMinor`); adjust the totals response shape if it reads a specific table |
| Q3 | Does the harness control the clock / when does a day close? | Real clock; scheduler on in compose (`AUTO_CLOSE_SECONDS`, default 60), explicit `POST /admin/stores/{id}/close?through=` | Run with `AUTO_CLOSE_SECONDS=0` if it drives time (old timestamps would otherwise close immediately); wire its close trigger to our endpoint, or add an endpoint with the shape it expects |
| Q4 | Does it simulate the gift-card lease protocol or only send redeem events? | Terminals must take a lease (`POST /giftcards/{id}/leases`) and cite `leaseId`; redemptions without one are flagged `no_lease` | If it only sends redeems: either add a lease-grant call to the adapter, or relax the unleased case so overspend is bounded by the per-card limit alone |
| Q5 | Currency / rounding | Single currency per store, integer minor units | none expected |
| Q6 | Does a void reference its sale by id, and does a return carry an amount or line items? | By `saleId` with the amount in the event | Map the shape; if a return carries line items, sum them in the adapter |
| Q7 | How do store ids and time zones reach us? | `PUT /stores/{id}` first, unknown stores are rejected | Seed stores from the harness's store list at startup, or auto-register with a configured default zone |

Also read: how it reads results (HTTP vs direct database vs an export with a fixed table name — matters for the DoD-5 SHA-256 check) and how it triggers a rebuild.

## Step 1 — smallest adapter

All mismatches should be small and live in `Ledger.Api` (routes, DTOs, field aliases). The ledger rules in `Ledger.Core` / `Ledger.Data` should not need to change. Candidates, in the order they are likely to bite:

1. Field-name and endpoint aliases (snake_case, `id`, different paths).
2. Store seeding.
3. The totals shape and the hash format for DoD-5 (a view or endpoint that returns the totals in the exact columns the checker hashes).
4. Day-close and rebuild triggers.
5. Gift card lease handling.

## Step 2 — run it

```bash
AUTO_CLOSE_SECONDS=0 docker compose up --build      # if the harness uses old or simulated timestamps
# point the harness at http://localhost:8080 (nginx → 2 replicas)
```

Run the five checks in order (DoD-1 totals, DoD-2 query-during-ingest, DoD-3 post-close adjustments, DoD-4 gift cards, DoD-5 rebuild hash). Fix one mismatch at a time. Then kill a replica mid-run (`docker kill pos-ledger-api1-1`) and re-check; our own tests cover this, the harness run is the confirmation.

## Step 3 — record it

Update `PROJECT_CHECKLIST.md` (Stage 11 item, Q1–Q7 answers) and the README "Known limits", then commit.

## Risks to watch

* A harness that sends gift card redemptions with no lease will show `no_lease` flags and overspend beyond the limit: that is the trust-model limit described in `docs/design.md`, not a bug, but it needs an explicit decision (add leasing to the adapter, or relax the unleased case).
* Old fixed timestamps plus the day-close scheduler: every old day closes within a minute and later events become adjustments. Use `AUTO_CLOSE_SECONDS=0` and close explicitly.
* The "own" versus "corrected" total: if the checksum counts late events on their original day, serve the corrected figure for that field.
