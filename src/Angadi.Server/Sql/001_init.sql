-- Shops and their events. Every row carries shop_id and every query filters by it.
CREATE TABLE shops (
    id          uuid PRIMARY KEY,
    name        text        NOT NULL,
    key_hash    bytea       NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE events (
    seq             bigserial PRIMARY KEY,
    shop_id         uuid        NOT NULL REFERENCES shops(id),
    event_id        uuid        NOT NULL,
    aggregate_type  text        NOT NULL,
    aggregate_id    uuid        NOT NULL,
    event_type      text        NOT NULL,
    payload         jsonb       NOT NULL,
    created_at_ms   bigint      NOT NULL,
    device_id       text        NOT NULL,
    received_at     timestamptz NOT NULL DEFAULT now(),
    UNIQUE (shop_id, event_id)
);

CREATE INDEX events_shop_seq ON events (shop_id, seq);
