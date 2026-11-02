-- The append-only event log. One row per distinct event; event_id is the idempotency key.
-- log_seq is the ingest order and is what a rebuild replays in.
CREATE TABLE events (
    log_seq       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id      text        NOT NULL UNIQUE,
    store_id      text        NOT NULL REFERENCES stores (store_id),
    terminal_id   text        NOT NULL,
    type          text        NOT NULL CHECK (type IN ('sale', 'return', 'void', 'giftcard_redeem', 'loyalty_earn', 'loyalty_burn')),
    occurred_at   timestamptz NOT NULL,
    received_at   timestamptz NOT NULL,
    sale_id       text,
    amount_minor  bigint      CHECK (amount_minor > 0),
    business_date date        NOT NULL
);

CREATE INDEX events_store_day_idx ON events (store_id, business_date);

-- Events we refused, kept with the raw payload and the reason. Nothing is dropped silently.
CREATE TABLE rejected_events (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    received_at timestamptz NOT NULL,
    event_id    text,
    reason      text        NOT NULL,
    raw         jsonb       NOT NULL
);
