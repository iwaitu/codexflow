# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Tests for the trend report and the batch/report CLI commands. No host needed: results are synthetic files."""
from __future__ import annotations

import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

DEVSIM = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(DEVSIM))

import report as r  # noqa: E402


def result(name, passed=True, duration=100.0, steps=None, session="sess"):
    return {"scenario": name, "passed": passed, "duration_s": duration, "session_id": session,
            "steps": steps if steps is not None else [{"index": 1, "name": "plan", "passed": passed, "skipped": False, "duration_s": duration, "checks": []}]}


def write_run(root: Path, run: str, *results):
    directory = root / run
    directory.mkdir(parents=True)
    for item in results:
        (directory / f"{item['scenario']}.json").write_text(json.dumps(item), encoding="utf-8")
    (directory / "summary.md").write_text("# not a result", encoding="utf-8")


class LoadRunsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def test_runs_come_in_name_order_and_junk_is_skipped(self):
        write_run(self.root, "20261109-010000", result("a"))
        write_run(self.root, "20261108-010000", result("a"))
        (self.root / "20261109-020000").mkdir()                                            # empty directory
        broken = self.root / "20261109-030000"
        broken.mkdir()
        (broken / "x.json").write_text("{not json", encoding="utf-8")                      # unreadable
        (broken / "y.json").write_text(json.dumps({"hello": 1}), encoding="utf-8")         # not a result
        runs = r.load_runs(self.root)
        self.assertEqual(["20261108-010000", "20261109-010000"], [run["name"] for run in runs])

    def test_last_keeps_only_the_most_recent_runs(self):
        for index in range(5):
            write_run(self.root, f"2026110{index}-000000", result("a"))
        self.assertEqual(["20261103-000000", "20261104-000000"], [run["name"] for run in r.load_runs(self.root, last=2)])

    def test_a_missing_root_is_an_empty_history(self):
        self.assertEqual([], r.load_runs(self.root / "nope"))


class FailureReasonTests(unittest.TestCase):
    def failing(self, **step):
        base = {"index": 1, "name": "s", "passed": False, "skipped": False, "checks": []}
        base.update(step)
        return {"scenario": "x", "passed": False, "steps": [base]}

    def test_the_most_specific_system_reported_reason_wins(self):
        self.assertEqual("step error: GatewayError", r.failure_reason(self.failing(error="GatewayError: host down")))
        self.assertEqual("verification: ArtifactRestoreFailed",
                         r.failure_reason(self.failing(receipt={"failure_code": "ArtifactRestoreFailed"}, jobs=[{"status": "Failed", "detail_code": "x"}])))
        self.assertEqual("job failed: offline_restore_unavailable",
                         r.failure_reason(self.failing(jobs=[{"status": "Completed"}, {"status": "Failed", "detail_code": "offline_restore_unavailable"}])))
        self.assertEqual("audit model: ModelFindings",
                         r.failure_reason(self.failing(receipt={"audit": {"layers": [{"name": "secrets", "state": 1}, {"name": "model", "state": 3, "reason": "ModelFindings"}]}})))
        self.assertEqual("expectation: no jobs were started",
                         r.failure_reason(self.failing(checks=[{"name": "no jobs were started", "ok": False}])))

    def test_a_failure_with_no_trace_is_labelled_unknown(self):
        self.assertEqual("unknown", r.failure_reason({"scenario": "x", "steps": []}))

    def test_only_the_first_failed_step_is_considered_and_skipped_steps_are_ignored(self):
        item = {"scenario": "x", "steps": [{"passed": True, "checks": []}, {"passed": False, "error": "A: first", "checks": []},
                                           {"passed": None, "skipped": True, "checks": []}]}
        self.assertEqual("step error: A", r.failure_reason(item))


class AggregateTests(unittest.TestCase):
    def runs(self):
        return [
            {"name": "r1", "results": [result("stable", True, 50), result("flaky", True, 100), result("broken", False, 30)]},
            {"name": "r2", "results": [result("stable", True, 70), result("flaky", False, 120), result("broken", False, 40)]},
            {"name": "r3", "results": [result("stable", True, 60), result("flaky", True, 110), result("broken", False, 35)]},
        ]

    def test_rates_flakiness_and_the_latest_outcome(self):
        a = r.aggregate(self.runs())
        self.assertEqual(3, a["runs"])
        self.assertEqual(9, a["scenario_runs"])
        self.assertAlmostEqual(5 / 9, a["overall_rate"])
        self.assertEqual(1.0, a["scenarios"]["stable"]["rate"])
        self.assertFalse(a["scenarios"]["stable"]["flaky"])
        self.assertTrue(a["scenarios"]["flaky"]["flaky"])
        self.assertFalse(a["scenarios"]["broken"]["flaky"])      # always failing is broken, not flaky
        self.assertTrue(a["scenarios"]["flaky"]["latest_passed"])
        self.assertFalse(a["scenarios"]["broken"]["latest_passed"])

    def test_durations_and_trend(self):
        a = r.aggregate(self.runs())
        self.assertEqual(60.0, a["scenarios"]["stable"]["avg_s"])
        self.assertEqual(70.0, a["scenarios"]["stable"]["p95_s"])
        self.assertEqual([2 / 3, 1 / 3, 2 / 3], [round(point["rate"] * 3) / 3 for point in a["trend"]])
        self.assertEqual("r3", a["trend"][-1]["run"])
        self.assertAlmostEqual(2 / 3, a["latest_rate"])

    def test_failure_reasons_are_counted_across_runs(self):
        runs = self.runs()
        for run in runs:
            for item in run["results"]:
                if not item["passed"]:
                    item["steps"][0]["error"] = "GatewayError: down"
        a = r.aggregate(runs)
        self.assertEqual(("step error: GatewayError", 4), a["failure_reasons"][0])
        self.assertEqual("r2", a["scenarios"]["flaky"]["last_failure"]["run"])

    def test_the_slowest_steps_are_listed_by_average_duration(self):
        a = r.aggregate(self.runs())
        self.assertEqual("flaky / plan", a["slowest_steps"][0][0])

    def test_percentile_edges(self):
        self.assertEqual(0.0, r.percentile([], 0.95))
        self.assertEqual(5.0, r.percentile([5.0], 0.95))
        self.assertEqual(10.0, r.percentile([1.0, 10.0], 0.95))


class RenderTests(unittest.TestCase):
    def test_the_markdown_names_scenarios_outcomes_flakiness_and_reasons(self):
        run = {"name": "r1", "results": [result("alpha", True), result("beta", False)]}
        run["results"][1]["steps"][0]["error"] = "GatewayError: down"
        text = r.render_markdown(r.aggregate([run, {"name": "r2", "results": [result("alpha", False), result("beta", False)]}]))
        self.assertIn("| alpha |", text)
        self.assertIn("flaky", text)
        self.assertIn("FAIL", text)
        self.assertIn("Why runs fail", text)
        self.assertIn("| r2 |", text)

    def test_the_trend_names_the_build_each_run_came_from_so_versions_are_not_mixed_up(self):
        first = result("a", False)
        first["meta"] = {"commit": "abc1234", "label": "snapshot-33"}
        second = result("a", True)
        second["meta"] = {"commit": "def5678", "label": ""}
        text = r.render_markdown(r.aggregate([{"name": "r1", "results": [first]}, {"name": "r2", "results": [second]}]))
        self.assertIn("abc1234", text)
        self.assertIn("snapshot-33", text)
        self.assertIn("def5678", text)

    def test_runs_without_meta_still_render(self):
        self.assertIn("| r1 |", r.render_markdown(r.aggregate([{"name": "r1", "results": [result("a")]}])))

    def test_no_runs_says_so(self):
        self.assertIn("No runs found", r.render_markdown(r.aggregate([])))


class CliTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        import importlib.util  # noqa: PLC0415
        spec = importlib.util.spec_from_file_location("devsim_cli", DEVSIM / "__main__.py")
        self.m = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.m)

    def run_cli(self, *argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = self.m.main(list(argv))
        return code, out.getvalue()

    def test_report_prints_the_trend_and_writes_a_file(self):
        write_run(self.root, "20261109-000000", result("a", True), result("b", False))
        target = self.root / "trend.md"
        code, output = self.run_cli("report", str(self.root), "--write", str(target))
        self.assertEqual(0, code)
        self.assertIn("| a |", output)
        self.assertIn("| a |", target.read_text(encoding="utf-8"))

    def test_min_pass_rate_turns_a_bad_window_into_a_failing_exit_code(self):
        write_run(self.root, "20261109-000000", result("a", True), result("b", False))
        self.assertEqual(1, self.run_cli("report", str(self.root), "--min-pass-rate", "0.9")[0])
        self.assertEqual(0, self.run_cli("report", str(self.root), "--min-pass-rate", "0.5")[0])

    def test_an_empty_history_fails_a_threshold_check_rather_than_passing_vacuously(self):
        self.assertEqual(1, self.run_cli("report", str(self.root), "--min-pass-rate", "0.5")[0])

    def test_run_metadata_records_the_checkout_commit_and_the_label(self):
        meta = self.m.run_metadata("host-snapshot-34")
        self.assertEqual("host-snapshot-34", meta["label"])
        self.assertRegex(meta["commit"], r"^([0-9a-f]{7,40}|unknown)$")

    def test_run_directory_names_sort_by_time_and_never_collide_for_repeats(self):
        names = [self.m.run_directory_name(1_700_000_000, index, 3) for index in range(3)]
        self.assertEqual(sorted(names), names)
        self.assertEqual(3, len(set(names)))
        self.assertEqual(1, len({name[:15] for name in names}))


if __name__ == "__main__":
    unittest.main()
