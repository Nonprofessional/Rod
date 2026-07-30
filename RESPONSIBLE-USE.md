# Responsible Use

Rod is offensive-security tooling. It is remote-code-execution infrastructure
intended **solely for authorized use**. By using Rod you accept the conditions
below.

## Authorized use only

You may only use Rod against systems, networks, devices, or applications that:

1. you own, or
2. you have **explicit, written authorization** to test from the owner or a party
   legally entitled to grant it.

This covers, for example: penetration tests conducted under a signed statement
of work or rules of engagement, internal red-team exercises within your own
organization, capture-the-flag and lab environments, and defensive security
research.

## Your responsibilities

If you run Rod, you are responsible for:

- **Having authorization** before touching any target, and staying within the
  agreed scope and rules of engagement.
- **Knowing and obeying the law** in every jurisdiction you operate in. Many
  activities are criminalized by computer-misuse, wiretap, privacy, and
  cybercrime statutes unless covered by explicit authorization.
- **Minimizing harm**: handling any data you encounter lawfully, avoiding
  disruption to production systems, and disclosing findings through agreed
  channels.
- **Securing the infrastructure**: a compromised teamserver is fleet-wide code
  execution. Protect operator credentials, per-implant keys, and the teamserver
  host. Use disposable infrastructure per engagement.

## What Rod is not

- Rod is **not** a tool for unauthorized access, theft, harassment, surveillance
  of people without legal basis, or any activity targeting systems you do not
  have the right to test.
- Rod is **not** intended to facilitate any illegal act. The maintainer does not
  condone or assist such use.

## No warranty

Rod is provided "AS IS", without warranty of any kind, express or implied, under
the Apache License, Version 2.0. The maintainer is not liable for any damage
arising from use or misuse of the software. You alone bear responsibility for
your use of it.

## Reporting abuse

If you become aware of Rod being used to harm systems without authorization, or
of a vulnerability in Rod itself, see [SECURITY.md](SECURITY.md) for the
disclosure process.
