-- Read model: money per store per business day, in integer minor units.
-- It is only ever written in the same transaction that appends the events it summarises.
CREATE TABLE store_day_totals (
    store_id      text   NOT NULL REFERENCES stores (store_id),
    business_date date   NOT NULL,
    sales_minor   bigint NOT NULL DEFAULT 0,
    returns_minor bigint NOT NULL DEFAULT 0,
    voids_minor   bigint NOT NULL DEFAULT 0,
    net_minor     bigint GENERATED ALWAYS AS (sales_minor - returns_minor - voids_minor) STORED,
    PRIMARY KEY (store_id, business_date)
);
