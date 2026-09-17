# API versioning and compatibility

Covers MVP-060 and MVP-070: how the API, its events and its schema evolve without breaking the
systems that depend on them.

## The contract

The v1 contract is the OpenAPI document at `/openapi/v1.json`, checked in at
[`openapi/v1.json`](openapi/v1.json), together with:

- the problem `code` values in [`API.md`](API.md#failures-rfc-9457);
- the webhook event types and payload `schemaVersion` 1;
- the inbound HL7 v2 and FHIR mappings;
- the signature scheme `Betsi-Signature: t=…,v1=…`.

A test regenerates the OpenAPI document on every build and fails if it differs from the
checked-in copy, so no contract change reaches `main` without appearing as a reviewed diff.
To accept an intended change: `BETSI_UPDATE_OPENAPI=1 dotnet test Betsi.slnx`, then commit
`docs/openapi/v1.json` alongside the code.

## Versioning scheme

**URL path versioning**: `/api/v1/…`. A path segment is visible in logs, proxies and
browser tools, cannot be dropped by an intermediary the way a header can, and makes it
impossible to call v2 by accident.

A new major version is introduced only for a change that cannot be made compatibly. When it is:

1. `/api/v2` is added alongside `/api/v1`. Both run from the same build against the same data.
2. v1 is marked deprecated in its OpenAPI document and responses carry `Deprecation` and
   `Sunset` headers (RFC 8594) with a date at least **12 months** out — NHS integration changes
   need a procurement and assurance cycle, not a sprint.
3. Every registered webhook subscriber and inbound source owner is notified.
4. v1 is removed only after its sunset date and after its traffic has been confirmed at zero.

## What may change within v1

| Allowed (compatible) | Not allowed (breaking) |
|---|---|
| New endpoints | Removing or renaming an endpoint, field, query parameter or problem code |
| New optional request fields | Making an optional request field required |
| New response fields | Changing a field's type, format, or meaning |
| New problem codes | Reusing a problem code for a different condition |
| New enum values in responses* | Removing an enum value clients may send |
| New webhook event types | Changing an existing event's payload shape |
| Relaxing validation | Tightening validation of existing input** |
| New permissions granted to roles | Removing a permission from a role that relies on it** |

\* Clients must treat an unknown enum value (for example a new escalation `state`) as
"other" rather than failing. This is stated in the API reference and is a condition of
integration.

\** Unless required for clinical safety or security. Such a change is made in v1, logged in the
hazard log, and communicated to integrators before release.

## Events and webhook payloads

- Every webhook payload carries `schemaVersion`. Additive changes (new `data` fields) keep the
  version; a breaking change to a type's payload publishes a new version to subscribers who opt
  in, and the old version continues until its sunset, as for the API.
- Domain events in the internal event log are append-only and never rewritten. A change to an
  event's shape adds a new event type or new optional fields; readers of the log handle every
  version that has ever been written.
- Webhook payloads are an allowlist of fields, separate from the internal event shape, so
  internal refactoring never changes what subscribers receive.

## Database schema

Tenant and control-plane schemas change by **expand/contract**:

1. **Expand** — add new tables, columns (nullable or with defaults) and indexes. Release a build
   that writes both old and new shapes and reads the new one.
2. **Migrate data** if needed, idempotently.
3. **Contract** — in a later release, once no running build reads the old shape, remove it.

A single release never both adds and removes a column a running build depends on. This is what
lets `tenants migrate` run before new instances start, while old instances are still serving:
the registry refuses a tenant whose schema is *older* than a build requires, and accepts one
that is newer. CI fails any change whose model and migrations disagree
(`has-pending-model-changes`), for both database contexts.

## Idempotency and correlation

`idempotencyKey`, `commandId`, `correlationId`, `Betsi-Message-Id` and `Betsi-Event-Id` are part
of the contract and keep their semantics across versions: a client that retries correctly
against v1 retries correctly against any later version.
