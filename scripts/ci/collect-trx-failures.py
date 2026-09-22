#!/usr/bin/env python3
"""Collect the failed test names out of a lane's trx logs as a retry filter.

Reads every ``**/TestResults/*.trx`` a ``dotnet test --logger trx`` run left
behind, takes the failing tests' names, and writes a dotnet-test filter
expression to GITHUB_ENV under the variable this script is told to fill.
Theory cases carry their arguments in the display name; the bare method name
still selects every case, so the split keeps the filter portable.

Nothing parseable (a runner-level hang with no results) leaves the sentinel,
and the calling step falls back to the whole lane -- the old shape, kept for
the hang class it absorbed.
"""

import argparse
import glob
import os
import xml.etree.ElementTree as ET


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("env_var", help="GITHUB_ENV variable to fill")
    args = parser.parse_args()

    names = set()
    for trx in glob.glob("**/TestResults/*.trx", recursive=True):
        root = ET.parse(trx).getroot()
        # The trx schema carries an XML namespace; matching on the
        # namespace-stripped local name keeps the lookup immune to its
        # version suffix (ElementTree's iter has no wildcard form).
        id_to_name = {}
        results = []
        for element in root.iter():
            local = element.tag.rsplit("}", 1)[-1]
            if local == "UnitTest":
                id_to_name[element.get("id")] = element.get("name")
            elif local == "UnitTestResult":
                results.append(element)
        for result in results:
            if result.get("outcome") in ("Failed", "Error", "Timeout", "Aborted"):
                name = (
                    result.get("testName")
                    or id_to_name.get(result.get("testId"))
                    or ""
                )
                names.add(name.split("(")[0])

    retry = "|".join(f"FullyQualifiedName~{n}" for n in sorted(names) if n)
    with open(os.environ["GITHUB_ENV"], "a") as env:
        env.write(f"{args.env_var}={retry or '__all__'}\n")
    print("retry filter:", retry or "__all__ (whole lane)")


if __name__ == "__main__":
    main()
