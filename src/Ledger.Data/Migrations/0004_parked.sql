-- A return or void that arrives before its sale is stored in the log but held out of the totals until the sale shows up.
CREATE TABLE parked_events (
    event_id    text PRIMARY KEY REFERENCES events (event_id),
    sale_id     text        NOT NULL,
    parked_at   timestamptz NOT NULL,
    resolved_at timestamptz
);

CREATE INDEX parked_open_idx ON parked_events (sale_id) WHERE resolved_at IS NULL;
CREATE INDEX events_sale_idx ON events (sale_id) WHERE type = 'sale';
