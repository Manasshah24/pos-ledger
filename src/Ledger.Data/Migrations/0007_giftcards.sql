-- Gift cards. balance_minor is the central balance after every redemption we know of. It can go negative: that is the
-- overspend from offline redemptions, which the lease scheme bounds by the configured limit and the flags below record.
ALTER TABLE events ADD COLUMN card_id text, ADD COLUMN lease_id bigint;

CREATE TABLE giftcards (
    card_id       text PRIMARY KEY,
    issued_minor  bigint      NOT NULL CHECK (issued_minor >= 0),
    balance_minor bigint      NOT NULL,
    created_at    timestamptz NOT NULL
);

-- A store's right to redeem a card offline: secured_minor is carved out of the card's balance, unsecured_minor out of the
-- card's small overspend allowance. spent_minor is what the store has reported redeeming under it (secured is used first).
CREATE TABLE giftcard_leases (
    lease_id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    card_id         text        NOT NULL REFERENCES giftcards (card_id),
    store_id        text        NOT NULL REFERENCES stores (store_id),
    secured_minor   bigint      NOT NULL CHECK (secured_minor >= 0),
    unsecured_minor bigint      NOT NULL CHECK (unsecured_minor >= 0),
    spent_minor     bigint      NOT NULL DEFAULT 0,
    granted_at      timestamptz NOT NULL,
    expires_at      timestamptz NOT NULL,
    CHECK (secured_minor + unsecured_minor > 0)
);
CREATE INDEX giftcard_leases_card_idx ON giftcard_leases (card_id);

-- Every redemption that went wrong, and by how much. Nothing is silently absorbed.
CREATE TABLE giftcard_flags (
    event_id     text   NOT NULL REFERENCES events (event_id),
    kind         text   NOT NULL CHECK (kind IN ('overspend', 'no_lease', 'exceeds_lease', 'expired_lease')),
    card_id      text   NOT NULL,
    amount_minor bigint NOT NULL,
    PRIMARY KEY (event_id, kind)
);
CREATE INDEX giftcard_flags_card_idx ON giftcard_flags (card_id);
