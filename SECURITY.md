# Security Policy

Rod is remote-code-execution infrastructure: a compromised control plane
(teamserver) is fleet-wide code execution against every connected implant. We
take vulnerabilities seriously and appreciate coordinated disclosure. The full
threat model and controls are described in [docs/architecture.md](docs/architecture.md)
(security section).

## Reporting a vulnerability

**Do not open a public issue for a security vulnerability.** Instead, report it
privately:

- Preferred: use GitHub's **Private vulnerability reporting** ("Report a
  vulnerability" under the repository **Security** tab), or
- Email the maintainer with the details below.

Please include:

- affected component (teamserver, redirector, a build unit, an implant class,
  the wire protocol, a capability module),
- version / commit,
- a description and, ideally, a minimal reproduction,
- the impact you believe it has.

We aim to acknowledge a report within **3 business days** and to agree a
disclosure timeline. Please give reasonable time for a fix before any public
disclosure. Reporters are credited unless they prefer to remain anonymous.

## Scope

In scope: anything that breaks Rod's security model -- for example authentication
bypass, engagement isolation escape, certificate or CA weaknesses, protocol
parser vulnerabilities, redirector trust violations, build unit or
capability-module supply-chain integrity, or audit-trail tampering. (Sealing and
command signing are documented future work in architecture.md Sec 9; once they
land, bypasses join this scope.)

Out of scope (by design, documented -- not vulnerabilities):

- Evasion and exploit modules are pluggable capability contracts. Their concrete
  behavior is implementation-defined and lives outside the core; the core
  provides the contract and the dispatch, not bypass techniques.
- Operator-authorized capabilities (shell, file transfer, tunneling, etc.) do
  exactly what an authorized operator requests. That is the product's purpose,
  not a flaw.
- Rod ships **no** protection against its own detection by defenders. Stealth is
  a deployment and capability concern, not a security boundary of the platform.
