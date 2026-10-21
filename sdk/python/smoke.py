#!/usr/bin/env python3
"""The SDK acceptance smoke (architecture.md Sec 15.3, ADR-006's AC).

Against a running teamserver: log in, create an engagement, issue a task
through a scoped API token, and collect the artifact the task produces.
Exit code 0 and a ``smoke-ok`` line on stdout mean the SDK carried the
full arc; anything else is a failure with the reason on stderr.

The task's implant side is the environment's business (the .NET smoke
rig drives a fake beacon while this script polls), so the smoke itself
stays operator-side only.
"""

from __future__ import annotations

import argparse
import sys
import time

from rod_sdk import RodClient, RodError


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True, help="teamserver base URL")
    parser.add_argument("--handle", required=True)
    parser.add_argument("--password", required=True)
    parser.add_argument("--token", required=True,
                        help="an operate-scoped API token secret")
    parser.add_argument("--engagement", required=True,
                        help="the rig's engagement id (holds the implant)")
    parser.add_argument("--implant", required=True,
                        help="the rig's enrolled implant id")
    parser.add_argument("--timeout", type=float, default=60.0)
    args = parser.parse_args()

    # 1. Login: the cookie session, the same credential shape the console
    #    uses. The summary names the operator the server resolved.
    session = RodClient(args.url)
    me = session.login(args.handle, args.password)
    print(f"login-ok: {me['handle']} ({me['role']})")

    # 2. Create an engagement: the operator's working record.
    engagement = session.create_engagement(
        "Operation Python SDK", description="sdk/python acceptance smoke")
    print(f"engagement-ok: {engagement['engagementId']}")

    # 3. Issue the task through the scoped token (the versioned surface).
    api = RodClient(args.url, token=args.token)
    task = api.issue_task(
        args.engagement, args.implant, "exfil.push",
        arguments="loot.txt /opt/secret/loot.txt")
    task_id = task["taskId"]
    print(f"task-ok: {task_id} ({task['verb']})")

    # 4. Wait for the beacon side to complete the task and stream the
    #    exfil chunk that lands as the artifact.
    deadline = time.monotonic() + args.timeout
    while True:
        current = api.get_task(args.engagement, task_id)
        if current["status"].lower() in ("completed", "failed", "cancelled"):
            break
        if time.monotonic() > deadline:
            print(f"timed out waiting for task {task_id}; "
                  f"last status {current['status']}", file=sys.stderr)
            return 1
        time.sleep(0.25)

    if current["status"].lower() != "completed":
        print(f"task ended {current['status']}: {current.get('outcome')}",
              file=sys.stderr)
        return 1

    # 5. Collect the artifact: list the task's evidence, pull the bytes.
    listed = api.list_artifacts(args.engagement, task_id)
    artifacts = listed["items"]
    if not artifacts:
        print("task completed with no artifacts", file=sys.stderr)
        return 1
    artifact = artifacts[0]
    content = api.get_artifact(args.engagement, artifact["artifactId"])
    print(f"artifact-ok: {artifact['name']} {len(content)} bytes "
          f"({artifact['contentType']})")
    print(f"smoke-ok: {artifact['artifactId']}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except RodError as error:
        print(f"api error: {error}", file=sys.stderr)
        raise SystemExit(2) from error
