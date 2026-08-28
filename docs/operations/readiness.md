# Pre-engagement readiness gate

The last page between Rod and a client network. Facing a real target
raises the decisions the framework deliberately leaves to the crew --
who authorizes, who holds credentials, what happens to the evidence --
and answering them at the gate is far cheaper than answering them
mid-engagement. The engagement lead walks this checklist before the
first check-in; budget thirty minutes. An item that cannot be checked is
not a delay to waive -- it is the engagement not starting.

The [rehearsal walk](rehearsal.md) is the compressed form: the gate
anchors on its lifecycle walk, and item 3 pins its single-host form as
the pre-check-in minimum.

## Standing defaults

The defaults this repository pins. A crew can tighten them for an
engagement; it can never set them below
[RESPONSIBLE-USE.md](../../RESPONSIBLE-USE.md) or
[architecture.md](../architecture.md) Sec 13.

- **Authorization is written or absent.** No signed scope, no
  engagement -- the authorized-use condition is not a policy to
  interpret around (RESPONSIBLE-USE.md).
- **The standard surface is the default surface.** The in-repo verbs
  (Sec 13) are the default capability set; out-of-tree modules are
  opt-in, each one reviewed against the Sec 13 line at the gate, and
  anything unclear stays out-of-tree -- Sec 13's own rule, restated as a
  gate default.
- **Credentials live in the secret store and are minted on use.**
  Operator passwords and the Pki passphrase ride the secret path
  ([teamserver.md](teamserver.md)), stager tokens are minted per
  deployment and spent once, and the engagement CA is provisioned out of
  band (architecture.md Sec 9) -- the teamserver never generates it.
- **Evidence moves as the trio and is accounted for at close-out.** The
  Postgres dump, the evidence data directory, and the DataProtection key
  ring back up together ([teamserver.md](teamserver.md)); the report is
  the deliverable and the trail is its source.
- **Infrastructure is disposable per engagement** (RESPONSIBLE-USE.md):
  fresh fronts, fresh engagement CA, fresh credentials.

## The checklist

Walk in order; each item names its evidence.

1. **Authorization on file.** The signed ROE names the targets,
   networks, window, and authorizing party; the engagement lead and the
   abort authority are named people. Evidence: the ROE document itself.
2. **Capability plan against the Sec 13 line.** List the verbs the
   engagement plans to task. Everything in the in-repo standard surface
   passes as-is; every out-of-tree module in `Tradecraft:Modules` gets a
   named review against Sec 13 -- technique kind, not capability
   category -- plus explicit ROE coverage for what it does; anything
   unclear defaults to out-of-tree. Evidence: the verb list with each
   module disposition recorded.
3. **The rehearsal walked on this deployment.** The single-host
   lifecycle walk ([rehearsal.md](rehearsal.md) Sec 1-3) ran green on
   the engagement's shape -- CA, persistence, a front, a repoint, a
   restart, a teardown to report. The first deployment to a client
   network walks the multi-host shape (Sec 4) as well. Evidence: the
   walk's own acceptance output.
4. **Credential custody named.** Where the operator password lives, who
   mints and carries stager tokens, where the engagement CA private key
   rests -- off the teamserver host unless the ROE accepts otherwise
   (architecture.md Sec 9). No secret sits inline in a config file.
   Evidence: the custody notes, one line per secret.
5. **Evidence retention decided.** The backup trio has an owner and a
   schedule; retention or destruction at close-out follows the client
   contract. Both are recorded now, not at teardown. Evidence: the
   retention note in the engagement record.
6. **Teardown path named.** Retire the implants, stop the fronts, export
   the report, decommission the infrastructure
   ([rehearsal.md](rehearsal.md) Sec 3 step 8 is the compressed form) --
   and the abort authority can execute it without the engagement lead.
   Evidence: the named procedure and its owner.
7. **Disclosure channel agreed.** Findings flow through the channel the
   ROE names, on its schedule (RESPONSIBLE-USE.md, minimizing harm).
   Evidence: the channel named in the ROE.

Items 1, 2, and 7 are the policy boundary in operational form; 3 through
6 are the runbooks it stands on. All seven trace somewhere -- a gate
item that cites only itself is a symptom, not a gate.
