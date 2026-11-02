CREATE TABLE stores (
    store_id text PRIMARY KEY,
    tz       text NOT NULL,
    currency char(3) NOT NULL DEFAULT 'USD'
);
