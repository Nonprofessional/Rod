# ADR 0010 -- Production implant CA: consume an externally provisioned engagement CA

- **Status:** Accepted
- **Date:** 2026-08-13
- **Related:** [architecture.md](../architecture.md) Sec 9 (mTLS identity),
  [ADR 0008](0008-operator-authentication.md) (the production-hardening sibling
  that established the config-opt-in, fail-fast stance this follows)

## Context

The walking skeleton ships a single implant CA: `DevCertificateAuthority`
generates a throwaway self-signed root in process memory at construction and
signs every implant leaf with it. That is correct for development and tests --
the mTLS handshake works end to end -- but it has two properties no production
deployment can keep:

1. **The CA key lives in the teamserver process.** It is generated at
   construction and never leaves memory; there is no external PKI, no
   separation between the C2 and the key that vouches for the fleet.
2. **The CA is non-rotatable.** A new process mints a new root, invalidating
   every issued leaf. There is no way to bind enrollment to the operator's own
   engagement CA.

The production-hardening todo ("Real implant CA") calls for enrollment to bind
to a non-dev CA chain. The PKI contract already anticipated this:
`IImplantCertificateAuthority` states "production rotates to an externally
provisioned engagement CA without changing this contract," and the dev
authority's doc says "substitute an externally provisioned, per-engagement CA
behind the same port." This ADR records the decision that substitution makes
concrete.

Two constraints, both shared with ADR 0008:

1. **The contract stays put.** `IImplantCertificateAuthority`,
   `EnrollmentService`, the wire enroll response, the mTLS validation, and the
   implant's CA pinning are unchanged. The implant already trusts whatever root
   the enroll response hands back; switching the issuer changes only which key
   signs.
2. **Zero new NuGet.** PEM parsing, RSA import, and X.509 signing are all in the
   .NET shared framework.

## Decision

**The teamserver consumes an externally provisioned engagement CA as PEM files
on disk; it does not generate the production CA.** `FileBackedCertificateAuthority`
(`src/teamserver/Rod.CoreState/Pki/FileBackedCertificateAuthority.cs`) loads a
CA certificate and its RSA private key (optionally passphrase-encrypted) from
configured paths and signs implant leaves with the same leaf construction the
dev authority uses, so every identity and handshake invariant is preserved --
only the issuer changes.

The pieces:

- **An externally provisioned CA, not an in-process root.** The CA certificate
  and key are produced out of band by the operator's PKI and placed on disk; the
  teamserver reads them. This keeps the CA private key out of the C2's own
  keygen path and makes rotation an operational matter (replace the files,
  restart) rather than a code change. `GetCaCertificate` returns the loaded
  root, so the transport's thumbprint-pinning client-cert validation
  (`TransportHost.ClientCertificateChainsToCa`) accepts leaves that chain to it.

- **Configuration-selected, like the audit store.** `AddRodTransport` picks the
  CA the same way it picks the audit/artifact adapter: presence of the
  `Pki:CaCertificatePath` and `Pki:CaPrivateKeyPath` keys selects
  `FileBackedCertificateAuthority`, absence keeps `DevCertificateAuthority`, and
  every existing test is unchanged. A partial configuration (one path without
  the other) throws at startup.

- **Fail fast at startup.** The authority is constructed eagerly during DI
  registration, so a missing file, an unparseable PEM, a non-RSA key, or a key
  that does not match the certificate fails the host build -- not the first
  enrollment. RSA is the only supported CA key type, matching the implant leaf
  path (which imports a DER `SubjectPublicKeyInfo` as RSA).

- **An optional passphrase for an encrypted key.** `Pki:CaPrivateKeyPassphrase`
  decrypts an encrypted PKCS#8 key; it is meant for an environment-variable or
  secret-store override, never inline in `appsettings.json`.

## Rationale

- **Consume, do not provision.** Generating the production CA in the teamserver
  would re-create the dev posture (key in the C2) at higher privilege. An
  operator running real engagements already has a PKI story; the C2 should slot
  into it, not replace it. This is also the posture the contract has always
  described.
- **Mirror the audit-store selection.** The codebase already has a
  config-driven, presence-opts-in adapter swap (`Audit:DataDirectory`); reusing
  that pattern keeps the composition root uniform and makes the opt-in obvious
  to an operator reading `appsettings.json`. The outer-layer `services.Replace`
  alternative (ADR 0003, the Postgres store) is heavier than PKI needs: PEM
  loading is BCL-only and lives in `Rod.CoreState.Pki`, reachable from transport
  without a new layer.
- **Same leaf construction as the dev CA.** Identity and handshake checks read
  the subject CN, the Rod engagement extension, the client-auth EKU, and the
  end-entity basic constraints. Keeping the leaf identical means the only
  variable is the issuer, so the acceptance test reduces to "does the leaf chain
  to the configured CA."
- **Fail fast.** A misconfigured CA discovered at first enrollment is a bad
  operational experience and, worse, a silent window where implants cannot
  connect. Constructing at registration surfaces the problem on `dotnet run`.
- **RSA only.** The leaf path is RSA (the enrollment port imports an RSA
  `SubjectPublicKeyInfo`), so an RSA CA key closes the loop. ECDSA would require
  widening the leaf and implant key paths; it is a future concern, not a
  configuration toggle here.

## Consequences

- **Positive:** enrollment binds to the operator's engagement CA chain; the CA
  private key stays with the operator's PKI, not the C2; rotation is a file swap
  plus restart; the dev path and every existing test are untouched; no new NuGet.
- **Negative:** the CA certificate is still presented as the mTLS **server**
  identity (the dev path does this too). A real engagement CA cert has no
  server-auth EKU or hostname SAN; today both sides pin by thumbprint and do not
  enforce server-identity correctness, so the handshake works, but a proper TLS
  server leaf with SAN is a separable hardening this ADR deliberately does not
  bundle. The code already carries the "a real deployment presents a proper
  server certificate" note.
- **Risk:** a configured CA with under 30 days to expiry cannot issue the
  30-day default leaves -- `CertificateRequest.Create` rejects a leaf that would
  outlive its issuer, surfacing at enrollment. This is acceptable fail-loud
  behavior that tells the operator to rotate; it is not silently masked.
- **Risk:** the passphrase, if set via an environment variable, is
  process-readable. Mitigation: file-system permissions on the key, and a
  deployment that does not persist the passphrase. A future hardening could add
  an HSM or key-provider seam behind the same port.

## Alternatives considered

- **Generate and persist the CA from the teamserver.** Rejected: it re-creates
  the dev posture (key held by the C2) at production privilege and makes the
  teamserver the PKI root, which is exactly the separation the operator's
  external PKI provides. Persistence would also need its own key-management and
  rotation story.
- **An outer-layer `services.Replace` extension (the ADR 0003 / Postgres
  pattern).** Rejected as heavier than necessary: PEM loading is BCL-only and
  lives cleanly in `Rod.CoreState.Pki` next to the dev authority, reachable from
  transport. The audit-store precedent (select in `AddRodTransport` by config)
  is the closer fit.
- **`IOptions<T>` binding for the `Pki` section.** Rejected for consistency: the
  audit store reads its key directly off `IConfiguration`, and the file-backed
  authority takes a plain options record constructed at the call site. Adding an
  `IOptions` binding here would diverge from the only comparable config-selected
  adapter.
- **Bundling a proper TLS server leaf + SAN.** Rejected as scope creep: the
  acceptance criterion is enrollment binding, which the CA-as-trusted-root
  satisfies. Server identity is its own concern and stays a documented gap.
