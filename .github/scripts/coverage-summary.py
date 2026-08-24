#!/usr/bin/env python3
"""Render a Markdown coverage summary from coverlet's Cobertura reports.

Usage: coverage-summary.py <report.xml> [<report.xml> ...]

Reads one Cobertura file per target framework, prints a Markdown table with the
same line/branch/method percentages coverlet prints to the console, plus a
collapsed list of the files with the most uncovered lines. Written against the
Python standard library only, so CI needs no third-party action, tool install,
or secret to display coverage.

Exits non-zero only when it cannot read a report -- it never judges the numbers,
because this repo deliberately has no build-failing coverage threshold (#641).
"""

import os
import sys
import xml.etree.ElementTree as ET

# How many of the least-covered files to list under the summary table.
WORST_FILE_COUNT = 10


class Report:
    """The numbers for a single Cobertura file (i.e. one target framework)."""

    def __init__(self, label, path):
        self.label = label
        self.path = path
        root = ET.parse(path).getroot()

        self.lines_covered = int(root.get("lines-covered", 0))
        self.lines_valid = int(root.get("lines-valid", 0))
        self.branches_covered = int(root.get("branches-covered", 0))
        self.branches_valid = int(root.get("branches-valid", 0))

        # Cobertura has no method totals, so derive them the way coverlet's own
        # console table does: a method counts as covered if any of its lines ran.
        self.methods_covered = 0
        self.methods_valid = 0
        # Multiple <class> entries can share a filename (partial/nested types),
        # so accumulate per file rather than per class.
        self.by_file = {}

        for cls in root.iter("class"):
            filename = cls.get("filename") or "(unknown)"
            covered, valid = self.by_file.get(filename, (0, 0))

            for method in cls.iter("method"):
                self.methods_valid += 1
                if any(int(line.get("hits", 0)) > 0 for line in method.iter("line")):
                    self.methods_covered += 1

            lines = cls.find("lines")
            if lines is not None:
                for line in lines:
                    valid += 1
                    if int(line.get("hits", 0)) > 0:
                        covered += 1

            self.by_file[filename] = (covered, valid)


def percent(covered, valid):
    """Format a coverage ratio, tolerating the zero-denominator case."""
    if valid == 0:
        return "n/a"
    return f"{covered * 100.0 / valid:.2f}%"


def framework_label(path):
    """Recover the TFM from coverlet's `coverage.<tfm>.cobertura.xml` naming."""
    name = os.path.basename(path)
    prefix, suffix = "coverage.", ".cobertura.xml"
    if name.startswith(prefix) and name.endswith(suffix) and len(name) > len(prefix) + len(suffix):
        return name[len(prefix):-len(suffix)]
    return name


def render(reports):
    out = ["## Code coverage", ""]
    out.append("| Target framework | Line | Branch | Method |")
    out.append("| --- | ---: | ---: | ---: |")
    for r in reports:
        out.append(
            f"| {r.label} "
            f"| {percent(r.lines_covered, r.lines_valid)} "
            f"({r.lines_covered}/{r.lines_valid}) "
            f"| {percent(r.branches_covered, r.branches_valid)} "
            f"({r.branches_covered}/{r.branches_valid}) "
            f"| {percent(r.methods_covered, r.methods_valid)} "
            f"({r.methods_covered}/{r.methods_valid}) |"
        )
    out.append("")

    # One "least covered" list is enough; the TFMs cover the same source.
    worst = sorted(
        ((name, cov, val) for name, (cov, val) in reports[0].by_file.items() if val > cov),
        key=lambda item: (item[2] - item[1], item[0]),
        reverse=True,
    )[:WORST_FILE_COUNT]

    if worst:
        out.append(f"<details><summary>Files with the most uncovered lines ({reports[0].label})</summary>")
        out.append("")
        out.append("| File | Line coverage | Uncovered lines |")
        out.append("| --- | ---: | ---: |")
        for name, covered, valid in worst:
            out.append(f"| `{name}` | {percent(covered, valid)} | {valid - covered} |")
        out.append("")
        out.append("</details>")
        out.append("")

    out.append("_No coverage threshold is enforced; this is reporting only._")
    out.append("")
    return "\n".join(out)


def main(argv):
    paths = argv[1:]
    if not paths:
        print("usage: coverage-summary.py <report.xml> [<report.xml> ...]", file=sys.stderr)
        return 2

    reports = [Report(framework_label(p), p) for p in sorted(paths)]
    markdown = render(reports)

    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as handle:
            handle.write(markdown)
    else:
        sys.stdout.write(markdown)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
