-- A snapshot is a copy of the read models taken at a known point in the log, so a rebuild can start there and replay only
-- the events after it. It is written in one transaction: a snapshot that did not finish never becomes visible.
CREATE TABLE snapshots (
    id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    log_seq          bigint      NOT NULL, -- every event with log_seq <= this is reflected in the snapshot
    last_closure_id  bigint      NOT NULL, -- every day_closures row with id <= this is reflected too
    created_at       timestamptz NOT NULL,
    hash_totals      text        NOT NULL,
    hash_adjustments text        NOT NULL,
    hash_parked      text        NOT NULL,
    hash_loyalty     text        NOT NULL,
    hash_overall     text        NOT NULL
);

CREATE TABLE snapshot_days (
    snapshot_id       bigint NOT NULL REFERENCES snapshots (id) ON DELETE CASCADE,
    store_id          text   NOT NULL,
    business_date     date   NOT NULL,
    sales_minor       bigint NOT NULL,
    returns_minor     bigint NOT NULL,
    voids_minor       bigint NOT NULL,
    adj_sales_minor   bigint NOT NULL,
    adj_returns_minor bigint NOT NULL,
    adj_voids_minor   bigint NOT NULL,
    PRIMARY KEY (snapshot_id, store_id, business_date)
);

CREATE TABLE snapshot_adjustments (
    snapshot_id   bigint NOT NULL REFERENCES snapshots (id) ON DELETE CASCADE,
    event_id      text   NOT NULL,
    store_id      text   NOT NULL,
    original_date date   NOT NULL,
    applied_date  date   NOT NULL,
    sales_minor   bigint NOT NULL,
    returns_minor bigint NOT NULL,
    voids_minor   bigint NOT NULL,
    PRIMARY KEY (snapshot_id, event_id)
);

CREATE TABLE snapshot_loyalty (
    snapshot_id   bigint NOT NULL REFERENCES snapshots (id) ON DELETE CASCADE,
    account_id    text   NOT NULL,
    earned_points bigint NOT NULL,
    burned_points bigint NOT NULL,
    PRIMARY KEY (snapshot_id, account_id)
);

CREATE TABLE snapshot_parked (
    snapshot_id bigint NOT NULL REFERENCES snapshots (id) ON DELETE CASCADE,
    event_id    text   NOT NULL,
    PRIMARY KEY (snapshot_id, event_id)
);

CREATE TABLE snapshot_closed (
    snapshot_id bigint NOT NULL REFERENCES snapshots (id) ON DELETE CASCADE,
    store_id    text   NOT NULL,
    last_closed date   NOT NULL,
    PRIMARY KEY (snapshot_id, store_id)
);
