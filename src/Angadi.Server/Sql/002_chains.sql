-- v2 phase 4: chains. A head shop creates a chain; branches join with a one-time-shown code (only its hash is kept).
-- A shop belongs to at most one chain. Only the head reads the chain dashboard.
CREATE TABLE chains (
    id            uuid PRIMARY KEY,
    name          text        NOT NULL,
    head_shop_id  uuid        NOT NULL UNIQUE REFERENCES shops(id),
    join_hash     bytea       NOT NULL UNIQUE,
    created_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE chain_members (
    shop_id    uuid        PRIMARY KEY REFERENCES shops(id),
    chain_id   uuid        NOT NULL REFERENCES chains(id),
    joined_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX chain_members_chain ON chain_members (chain_id);
-- Dashboard queries: bills by type/time, and "was this bill voided?" lookups.
CREATE INDEX events_shop_type_time ON events (shop_id, event_type, created_at_ms);
CREATE INDEX events_shop_type_aggregate ON events (shop_id, event_type, aggregate_id);
