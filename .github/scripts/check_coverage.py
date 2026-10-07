# Adapted from LibSsh2CS at commit 2a5e008d4eb8ed895638bf17e0fc6350eb2c8769.
# Original repository: https://github.com/MM-YJN/LibSsh2CS
# Copyright and BSD-3-Clause terms are retained in .github/LICENSE.

"""Enforce LibGit2CS coverage thresholds using a merged Cobertura report."""

import argparse
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET


def read_coverage(path):
    root = ET.parse(path).getroot()
    if root.tag != "coverage":
        raise ValueError("Expected a Cobertura coverage root.")
    packages = root.findall("./packages/package")
    if len(packages) != 1 or packages[0].get("name") != "LibGit2CS":
        raise ValueError("Coverage must contain exactly one package named LibGit2CS.")
    if not packages[0].findall("./classes/class/lines/line"):
        raise ValueError("LibGit2CS coverage contains no source lines.")

    metrics = {}
    for metric in ("lines", "branches"):
        counts = []
        for suffix in ("covered", "valid"):
            value = root.get(f"{metric}-{suffix}", "")
            if not re.fullmatch(r"[0-9]+", value):
                raise ValueError(f"Missing or invalid {metric}-{suffix} count.")
            counts.append(int(value))
        covered, valid = counts
        if valid == 0 or covered > valid:
            raise ValueError(f"Invalid or empty {metric} coverage: {covered}/{valid}.")
        metrics[metric] = (covered, valid)
    return metrics


def validate_inputs(directory):
    # Validate before ReportGenerator filters or merges anything: an invalid
    # raw report must not be silently dropped from an otherwise passing gate.
    expected = set()
    for platform in ("linux", "windows", "macos"):
        reports = sorted((directory / f"{platform}-test-results").glob("*.cobertura.*.xml"))
        if len(reports) != 2:
            raise ValueError(f"Expected two coverage reports for {platform}; found {len(reports)}.")
        for report in reports:
            read_coverage(report)
            expected.add(report)
    if set(directory.rglob("*.cobertura.*.xml")) != expected:
        raise ValueError("Unexpected coverage inputs outside the three OS result directories.")


def check_coverage(directory, line_threshold, branch_threshold):
    metrics = read_coverage(directory / "Cobertura.xml")
    rows = []
    passed = True
    for metric, threshold in (("lines", line_threshold), ("branches", branch_threshold)):
        covered, valid = metrics[metric]

        # Compare exact integer counts; rounding is only for display.
        if not 0 <= threshold <= 100:
            raise ValueError("Coverage thresholds must be between 0 and 100.")
        meets_threshold = covered * 100 >= valid * threshold
        passed &= meets_threshold
        result = "PASS" if meets_threshold else "FAIL"
        rows.append(
            f"| {metric.capitalize()} | {covered}/{valid} | "
            f"{covered / valid:.2%} | {threshold}% | {result} |"
        )

    summary = "\n".join([
        "## LibGit2CS coverage",
        "",
        "| Metric | Covered/valid | Coverage | Minimum | Result |",
        "| --- | --- | --- | --- | --- |",
        *rows,
        "",
    ])
    return passed, summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path, help="Merged report directory, or downloaded inputs with --validate-inputs")
    parser.add_argument("--validate-inputs", action="store_true", help="Validate both suites from all three OS artifacts before merging")
    parser.add_argument("--line-threshold", type=int)
    parser.add_argument("--branch-threshold", type=int)
    args = parser.parse_args()
    if not args.validate_inputs and (args.line_threshold is None or args.branch_threshold is None):
        parser.error("--line-threshold and --branch-threshold are required when checking merged coverage")
    if args.validate_inputs and (args.line_threshold is not None or args.branch_threshold is not None):
        parser.error("Threshold options cannot be combined with --validate-inputs")
    try:
        if args.validate_inputs:
            validate_inputs(args.directory)
            passed, summary = True, "## LibGit2CS coverage\n\nPASS: All six coverage inputs are valid.\n"
        else:
            passed, summary = check_coverage(args.directory, args.line_threshold, args.branch_threshold)
    except (OSError, ET.ParseError, ValueError) as error:
        passed = False
        summary = f"## LibGit2CS coverage\n\nFAIL: {error}\n"

    print(summary)
    if summary_path := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(summary_path, "a", encoding="utf-8") as output:
            output.write(summary + "\n")
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
