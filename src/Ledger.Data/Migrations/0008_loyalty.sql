-- Loyalty points. An account's balance is earned - burned over the distinct events in the log. Both totals only ever grow
-- (a PN-counter), so merging in any order, with any repeats, converges to the same balance. The balance may go negative:
-- an offline store cannot know, and refusing afterwards would be refusing a purchase that already happened.
ALTER TABLE events ADD COLUMN account_id text, ADD COLUMN points bigint CHECK (points > 0);

CREATE TABLE loyalty_accounts (
    account_id     text   PRIMARY KEY,
    earned_points  bigint NOT NULL DEFAULT 0,
    burned_points  bigint NOT NULL DEFAULT 0,
    balance_points bigint GENERATED ALWAYS AS (earned_points - burned_points) STORED
);
