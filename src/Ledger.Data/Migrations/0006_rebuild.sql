-- A sale is identified by (store, sale_id): receipt numbers repeat across stores.
DROP INDEX events_sale_idx;
CREATE INDEX events_store_sale_idx ON events (store_id, sale_id) WHERE type = 'sale';

-- Rebuilding a read model has to rewrite closed days. It does so inside one transaction that sets ledger.rebuild = 'on';
-- anything else still hits the guard.
CREATE OR REPLACE FUNCTION guard_closed_day() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF current_setting('ledger.rebuild', true) = 'on' THEN
        RETURN NEW;
    END IF;
    IF EXISTS (SELECT 1 FROM stores s WHERE s.store_id = NEW.store_id AND s.last_closed_date >= NEW.business_date) THEN
        RAISE EXCEPTION 'business day % of store % is closed', NEW.business_date, NEW.store_id USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END
$$;
