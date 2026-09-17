# Runbook: identity provider, webhooks and inbound feeds

For platform engineers connecting a deployment to an identity provider, and site administrators
connecting other systems (MVP-063–066).

---

## 1. Connect the identity provider

Betsi accepts access tokens from any OIDC provider. Configure, per deployment:

```json
"Authentication": {
  "Jwt": {
    "Authority": "https://login.example.nhs.uk/tenant-or-realm",
    "Audience": "api://betsi"
  },
  "Claims": {
    "Tenant": "betsi:tenant_id",
    "Subject": "sub",
    "Role": "roles",
    "ActingRole": "betsi:acting_role"
  }
}
```

- **Authority** enables discovery: issuer and signing keys come from the provider's metadata and
  rotate automatically. Without discovery, set `Issuer` and `SigningKeys` (`KeyId`,
  `PublicKeyPem`) instead, and rotate by adding the new key before the provider starts using it.
- **Audience** must be a value only Betsi tokens carry. Tokens for other APIs are refused.
- **Claims** map the provider's claim names:
  - The **tenant** claim must hold the tenant id from `tenants list`. With Entra ID this is
    usually an app-role-scoped custom claim or a claims-mapping policy; with Keycloak a mapper.
  - **Roles** must use the role names in `docs/API.md` (*Permissions*). A role Betsi does not know
    grants nothing.
  - **ActingRole**: where the provider supports session role selection (NHS CIS2 does), map the
    selected role here. Otherwise users with several roles send `X-Betsi-Acting-Role`.

The service refuses to start outside Development without `Authentication:Jwt` configured.

**Verify:** call `GET /api/v1/license` with a token for each role you have mapped. A 403 with
code `RESERVED_ROLE`, `ACTING_ROLE_REQUIRED` or `TENANT_MISMATCH` names what to fix.

> Never assign users the `System` or `Integration` role in the provider. Betsi refuses both from
> tokens, and seeing them there means the role mapping is wrong.

---

## 2. Share the Data Protection key ring

Webhook and inbound-source secrets are encrypted with ASP.NET Data Protection. With more than one
instance, every instance must use the same keys, or one cannot read a secret another created and
deliveries fail with a cryptographic error.

Set `DataProtection:KeysDirectory` to a directory every instance mounts (and back it up — losing
the keys means re-issuing every secret). A managed key store is Phase I.

---

## 3. Add a webhook subscriber

1. **Agree what they receive.** Payloads carry ids, roles, states and times only — no patient
   identifiers. If the subscriber believes it needs names or NHS numbers, that is an information
   governance decision, and the answer is an authorised API client, not a webhook.
2. **Register** as a Site Administrator:

   ```bash
   POST /api/v1/webhooks/register
   { "url": "https://pager-bridge.example.nhs.uk/betsi", "eventTypes": ["escalation.raised", "escalation.follow_up_required"], "description": "Pager bridge" }
   ```

3. **Hand over the `secret` securely**, once. It is not retrievable later; rotate if it is lost.
4. **Give the subscriber the verification rules** from `docs/API.md` (*Webhooks*): check the
   signature and timestamp, dedupe on `Betsi-Event-Id`, answer 2xx within 10 seconds.
5. **Verify:** trigger the event in a test tenant and check
   `GET /api/v1/webhooks/{id}/deliveries` shows `Delivered`.

**Dead-lettered deliveries** mean the subscriber was down or refusing for about an hour. Once it
is fixed, retry each with `POST /api/v1/webhooks/deliveries/{id}/retry`. Escalations are never
dependent on a webhook: the escalation board and in-app workflow are the primary channel.

**Rotating a secret:** `POST /api/v1/webhooks/{id}/rotate-secret` takes effect immediately. Agree a
time with the subscriber, or have them accept both old and new signatures during the switch.

---

## 4. Connect an EPR arrival/discharge feed

1. **Register the source** as a Site Administrator:

   ```bash
   POST /api/v1/integrations/sources
   { "name": "YGC Emergency Care EPR", "format": "Hl7v2" }
   ```

   The response gives the `inboundPath` and the `secret`, once.

2. **Configure the sender** (usually the trust's integration engine) to POST each ADT message to
   `https://<betsi host><inboundPath>` with `Betsi-Signature` and a unique `Betsi-Message-Id`.
   HL7: A01/A04 arrival, A03 discharge, A11 cancel; PID-3 with the NHS number (type `NH`), PID-5
   name, PID-7 date of birth, PV1-19 visit number. FHIR: see `docs/API.md`.
3. **Send a test arrival and discharge** for a test patient, and check the HL7 ACK is `AA`.
4. **Watch the quarantine** for the first days:

   ```bash
   GET /api/v1/integrations/sources/{id}/messages?status=Quarantined
   ```

### Reviewing quarantined messages

Each quarantined message has an error. Common ones:

| Error | Meaning | Action |
|---|---|---|
| `Unsupported message type ADT^A08` | The sender sends events Betsi does not use | Filter at the integration engine |
| `NHS number must be 10 digits with a valid check digit` | Bad identifier in the source record | Correct in the EPR; resend with a new message id |
| `No arrival has been received for visit …` | Discharge before arrival, or the arrival was quarantined | Fix and resend the arrival, then the discharge |
| `…conflicts with an existing record (duplicate NHS number)` | The patient already has an open episode | Check for a duplicate registration on the board |

Reading a quarantined message's body needs a clinical role (episodes.read) and is audited:
`GET /api/v1/integrations/messages/{id}`.

A quarantined message is never retried automatically. Correct the cause and have the sender
resend with a **new** message id — the old id is permanently associated with the failed attempt.

### Turning a feed off

`POST /api/v1/integrations/sources/{id}/deactivate`. Messages are then refused with 401, so the
sender's own alerting notices.
