# rod-sdk (Python)

The official Python SDK for the Rod teamserver's versioned operator API
(`/api/v1`, OpenAPI 3.1). The committed contract lives at
[`sdk/openapi.v1.json`](../openapi.v1.json); this client is its Python face.
Standard library only -- no dependencies.

## Authentication

Two credential shapes, one client:

```python
from rod_sdk import RodClient

# Cookie session: the console's own credential, full operator authority.
client = RodClient("http://127.0.0.1:59443")
me = client.login("operator", "password")

# Bearer API token: a minted credential capped at its scope
# (read / operate / admin). Mint one from an authenticated session;
# the secret is shown exactly once.
minted = client.mint_token(me["id"], scope="operate")
api = RodClient("http://127.0.0.1:59443", token=minted["token"])
```

Scope floors (enforced server-side, ADR-006): `read` reads; `operate`
additionally acts inside engagements (tasking, notes, comments); `admin`
additionally mutates the deployment's management surface (operator
accounts, the redirector fleet, runtime settings).

## The arc

```python
engagement = api.create_engagement("Operation Nightfall")
implants = api.list_implants(engagement["engagementId"])

task = api.issue_task(engagement["engagementId"], implants[0]["implantId"],
                      "shell.exec", arguments="whoami")

# Live events arrive over SSE.
for event in api.events(engagement["engagementId"]):
    print(event["event"], event["data"])

# Poll the task, then collect the evidence it produced.
current = api.get_task(engagement["engagementId"], task["taskId"])
listed = api.list_artifacts(engagement["engagementId"], task["taskId"])
content = api.get_artifact(engagement["engagementId"],
                           listed["items"][0]["artifactId"])
```

Listings are cursor-paged: pass `limit`/`cursor`, follow `nextCursor`.

## Acceptance smoke

`sdk/python/smoke.py` carries the ADR-006 acceptance arc -- login, create
an engagement, issue a task, collect the artifact -- against a running
teamserver that has one enrolled implant:

```
python3 smoke.py --url http://127.0.0.1:59443 \
    --handle operator --password <password> \
    --token <operate-scoped secret> \
    --engagement <id> --implant <id>
```

The repository's integration suite runs this smoke automatically against
a rigged teamserver (`PythonSdkSmokeTests`); it skips, not fails, on a
host without `python3`.
