#!/usr/bin/env python3
"""Fail the build when Domain or Application line coverage falls below a threshold.

Phase C's exit criterion is "≥70% line coverage on Domain and Application". Those two
layers hold the clinical state machines and the command pipeline; the API layer is
covered by integration tests and the infrastructure layer by the SQL Server suites, so
neither is gated here.

Usage:
    coverage-gate.py <cobertura.xml> [--threshold 70]

Reads a Cobertura report (produced by `dotnet test -- --coverage
--coverage-output-format cobertura`) and reports coverage per gated layer. Line records
are de-duplicated by (file, line): Cobertura repeats every line once under its method
and again under its class, and a partial class appears once per part.
"""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import PurePosixPath

# Path fragments identifying a gated layer. Matched against the source file path with
# backslashes normalised, so a report generated on Windows is read the same way.
LAYERS = {
    "Domain": "/Betsi/Domain/",
    "Application": "/Betsi/Application/",
}


def coverage_by_layer(report: str) -> dict[str, tuple[int, int]]:
    """Return {layer: (covered, total)} counting each source line once."""
    hits: dict[str, dict[tuple[str, int], int]] = {layer: {} for layer in LAYERS}

    for class_element in ElementTree.parse(report).iter("class"):
        filename = str(PurePosixPath(class_element.get("filename", "").replace("\\", "/")))
        layer = next((name for name, fragment in LAYERS.items() if fragment in filename), None)
        if layer is None:
            continue

        for line in class_element.iter("line"):
            number = int(line.get("number", "0"))
            count = int(line.get("hits", "0"))
            key = (filename, number)
            hits[layer][key] = max(hits[layer].get(key, 0), count)

    return {
        layer: (sum(1 for count in lines.values() if count > 0), len(lines))
        for layer, lines in hits.items()
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", help="path to a Cobertura XML report")
    parser.add_argument("--threshold", type=float, default=70.0, help="minimum line coverage percent")
    arguments = parser.parse_args()

    results = coverage_by_layer(arguments.report)
    failures = []

    for layer, (covered, total) in results.items():
        if total == 0:
            print(f"::error::No {layer} lines found in {arguments.report}. "
                  "The report is empty or the layer paths have moved.")
            failures.append(layer)
            continue

        percent = 100.0 * covered / total
        status = "OK    " if percent >= arguments.threshold else "FAILED"
        print(f"{status} {layer}: {percent:.1f}% ({covered}/{total} lines), threshold {arguments.threshold:.0f}%")
        if percent < arguments.threshold:
            print(f"::error::{layer} line coverage {percent:.1f}% is below the {arguments.threshold:.0f}% gate.")
            failures.append(layer)

    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
