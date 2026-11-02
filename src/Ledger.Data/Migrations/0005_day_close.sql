-- Day close. A store's business days close in order, so "closed" is just a date: every day up to last_closed_date is closed.
ALTER TABLE stores ADD COLUMN last_closed_date date;

-- Append-only record of each close. log_seq_mark is the highest log_seq of the store's events at that moment: events of this
-- store at or below it were ingested before the close, events above it after. A rebuild uses it to replay the close in place.
CREATE TABLE day_closures (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    store_id      text        NOT NULL REFERENCES stores (store_id),
    through_date  date        NOT NULL,
    closed_at     timestamptz NOT NULL,
    log_seq_mark  bigint      NOT NULL
);
CREATE INDEX events_store_seq_idx ON events (store_id, log_seq);

-- Amounts that landed in this day because they belong to an earlier, already closed, day.
ALTER TABLE store_day_totals
    ADD COLUMN adj_sales_minor   bigint NOT NULL DEFAULT 0,
    ADD COLUMN adj_returns_minor bigint NOT NULL DEFAULT 0,
    ADD COLUMN adj_voids_minor   bigint NOT NULL DEFAULT 0;

-- One row per late event: which closed day it belongs to and which open day it was booked into.
CREATE TABLE adjustments (
    event_id      text PRIMARY KEY REFERENCES events (event_id),
    store_id      text        NOT NULL REFERENCES stores (store_id),
    original_date date        NOT NULL,
    applied_date  date        NOT NULL,
    sales_minor   bigint      NOT NULL,
    returns_minor bigint      NOT NULL,
    voids_minor   bigint      NOT NULL,
    created_at    timestamptz NOT NULL,
    CHECK (applied_date > original_date)
);
CREATE INDEX adjustments_original_idx ON adjustments (store_id, original_date);

-- Belt and braces: even a bug in application code cannot change the totals of a closed day.
CREATE FUNCTION guard_closed_day() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM stores s WHERE s.store_id = NEW.store_id AND s.last_closed_date >= NEW.business_date) THEN
        RAISE EXCEPTION 'business day % of store % is closed', NEW.business_date, NEW.store_id USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$$;

CREATE TRIGGER store_day_totals_guard BEFORE INSERT OR UPDATE ON store_day_totals
    FOR EACH ROW EXECUTE FUNCTION guard_closed_day();
