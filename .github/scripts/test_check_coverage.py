# Adapted from LibSsh2CS at commit 2a5e008d4eb8ed895638bf17e0fc6350eb2c8769.
# Original repository: https://github.com/MM-YJN/LibSsh2CS
# Copyright and BSD-3-Clause terms are retained in .github/LICENSE.

"""Regression checks for the CI coverage gate (standard library only)."""

from pathlib import Path
import os
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

from check_coverage import check_coverage, validate_inputs


class CoverageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.report = self.directory / "Cobertura.xml"

    def write_report(self, lines="95", branches="90", valid="100"):
        self.report.write_text(
            f'<coverage lines-covered="{lines}" lines-valid="{valid}" '
            f'branches-covered="{branches}" branches-valid="{valid}">'
            '<packages><package name="LibGit2CS"><classes><class name="Example">'
            '<lines><line number="1" hits="1" /></lines>'
            '</class></classes></package></packages></coverage>',
            encoding="utf-8",
        )

    def test_passing_and_exact_thresholds(self):
        for lines, branches in (("99", "94"), ("95", "90")):
            with self.subTest(lines=lines, branches=branches):
                self.write_report(lines, branches)
                passed, summary = check_coverage(self.directory, 95, 90)
                self.assertTrue(passed)
                self.assertEqual(summary.count("PASS"), 2)

    def test_ci_thresholds(self):
        self.write_report("85", "80")
        self.assertTrue(check_coverage(self.directory, 85, 80)[0])
        for lines, branches in (("849999", "800000"), ("850000", "799999")):
            self.write_report(lines, branches, "1000000")
            self.assertFalse(check_coverage(self.directory, 85, 80)[0])

    def test_either_threshold_can_fail_without_rounding(self):
        for lines, branches in (("949999", "900000"), ("950000", "899999")):
            with self.subTest(lines=lines, branches=branches):
                self.write_report(lines, branches, "1000000")
                passed, summary = check_coverage(self.directory, 95, 90)
                self.assertFalse(passed)
                self.assertIn("FAIL", summary)

    def test_cli_requires_both_thresholds(self):
        self.write_report()
        for arguments in ([], ["--line-threshold", "95"], ["--branch-threshold", "90"]):
            with self.subTest(arguments=arguments):
                result = subprocess.run(
                    [sys.executable, str(Path(__file__).with_name("check_coverage.py")),
                     str(self.directory), *arguments],
                    env={**os.environ, "GITHUB_STEP_SUMMARY": ""},
                    capture_output=True, text=True, check=False,
                )
                self.assertEqual(result.returncode, 2, result.stderr)
                self.assertIn("required", result.stderr)

    def test_invalid_thresholds(self):
        self.write_report()
        for threshold in (-1, 101):
            with self.assertRaises(ValueError):
                check_coverage(self.directory, threshold, 90)
            with self.assertRaises(ValueError):
                check_coverage(self.directory, 95, threshold)

    def test_invalid_counts(self):
        for lines, branches, valid in (
            ("0", "0", "0"), ("101", "90", "100"),
            ("95", "101", "100"), ("-1", "90", "100"),
            ("NaN", "90", "100"), ("95.0", "90", "100"),
            ("", "90", "100"), ("95", "90", ""),
        ):
            with self.subTest(lines=lines, branches=branches, valid=valid):
                self.write_report(lines, branches, valid)
                with self.assertRaises(ValueError):
                    check_coverage(self.directory, 95, 90)

    def test_requires_merged_report(self):
        with self.assertRaises(FileNotFoundError):
            check_coverage(self.directory, 95, 90)
        self.write_report()
        self.report.rename(self.directory / "coverage.cobertura.test.xml")
        with self.assertRaises(FileNotFoundError):
            check_coverage(self.directory, 95, 90)

    def write_inputs(self):
        self.write_report()
        content = self.report.read_text(encoding="utf-8")
        reports = []
        for platform in ("linux", "windows", "macos"):
            directory = self.directory / f"{platform}-test-results"
            directory.mkdir()
            for suite in ("unit", "integration"):
                report = directory / f"{suite}.cobertura.test.xml"
                report.write_text(content, encoding="utf-8")
                reports.append(report)
        return reports

    def test_complete_inputs(self):
        self.write_inputs()
        validate_inputs(self.directory)

    def test_missing_os_or_suite(self):
        reports = self.write_inputs()
        for report in reports:
            content = report.read_bytes()
            report.unlink()
            with self.assertRaises(ValueError):
                validate_inputs(self.directory)
            report.write_bytes(content)
        for report in reports[-2:]:
            report.unlink()
        with self.assertRaises(ValueError):
            validate_inputs(self.directory)

    def test_invalid_or_unexpected_inputs(self):
        reports = self.write_inputs()
        original = reports[0].read_text(encoding="utf-8")
        for content, exception in (
            ("<broken", ET.ParseError),
            (original.replace("LibGit2CS", "Other"), ValueError),
            (original.replace('<line number="1" hits="1" />', ''), ValueError),
            (original.replace('branches-valid="100"', 'branches-valid="0"'), ValueError),
        ):
            with self.subTest(content=content):
                reports[0].write_text(content, encoding="utf-8")
                with self.assertRaises(exception):
                    validate_inputs(self.directory)
        reports[0].write_text(original, encoding="utf-8")
        extra = self.directory / "extra.cobertura.test.xml"
        extra.write_text(original, encoding="utf-8")
        with self.assertRaises(ValueError):
            validate_inputs(self.directory)
        extra.unlink()
        extra = reports[0].with_name("duplicate.cobertura.test.xml")
        extra.write_text(original, encoding="utf-8")
        with self.assertRaises(ValueError):
            validate_inputs(self.directory)

    def test_input_validation_cli(self):
        self.write_inputs()
        arguments = [sys.executable, str(Path(__file__).with_name("check_coverage.py")),
                     str(self.directory), "--validate-inputs"]
        environment = {**os.environ, "GITHUB_STEP_SUMMARY": ""}
        result = subprocess.run(arguments, env=environment, capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("six coverage inputs", result.stdout)
        result = subprocess.run(arguments + ["--line-threshold", "85"], env=environment,
                                capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 2, result.stderr)
        next((self.directory / "macos-test-results").glob("*.xml")).unlink()
        result = subprocess.run(arguments, env=environment, capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("FAIL", result.stdout)

    def test_wrong_scope_and_empty_data(self):
        self.write_report()
        original = self.report.read_text(encoding="utf-8")
        for content in (
            original.replace('name="LibGit2CS"', 'name="Other"'),
            original.replace('</packages>', '<package name="Other" /></packages>'),
            original.replace('<line number="1" hits="1" />', ''),
            original.replace('lines-covered="95"', ''),
            '<coverage />', '<other />',
        ):
            with self.subTest(content=content):
                self.report.write_text(content, encoding="utf-8")
                with self.assertRaises(ValueError):
                    check_coverage(self.directory, 95, 90)

    def test_cli_exit_codes_and_summary(self):
        summary_path = self.directory / "summary.md"
        for content, expected in ((None, 1), ("passing", 0), ("below", 1), ("<broken", 1)):
            with self.subTest(content=content):
                if content == "passing":
                    self.write_report()
                elif content == "below":
                    self.write_report(lines="94")
                elif content is not None:
                    self.report.write_text(content, encoding="utf-8")
                summary_path.write_text("Existing summary\n", encoding="utf-8")
                result = subprocess.run(
                    [sys.executable, str(Path(__file__).with_name("check_coverage.py")),
                     str(self.directory), "--line-threshold", "95", "--branch-threshold", "90"],
                    env={**os.environ, "GITHUB_STEP_SUMMARY": str(summary_path)},
                    capture_output=True, text=True, check=False,
                )
                self.assertEqual(result.returncode, expected, result.stderr)
                summary = summary_path.read_text(encoding="utf-8")
                self.assertTrue(summary.startswith("Existing summary\n"))
                self.assertIn(result.stdout.strip(), summary)
                self.assertIn("PASS" if expected == 0 else "FAIL", summary)


if __name__ == "__main__":
    unittest.main()
