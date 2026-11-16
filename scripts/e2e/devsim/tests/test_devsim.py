# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Tests for the developer-simulation tooling. No network: the host is replaced by a fake client."""
from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

DEVSIM = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(DEVSIM))

import client as c  # noqa: E402
import scenario as s  # noqa: E402


def feed(accumulator: c.SseAccumulator, *lines: str) -> bool:
    done = False
    for line in lines:
        done = accumulator.feed(line) or done
    return done


class SseAccumulatorTests(unittest.TestCase):
    def test_collects_text_tools_and_completes_on_its_own_done(self):
        acc = c.SseAccumulator("m1")
        done = feed(
            acc,
            "id: 7", "event: heartbeat", 'data: {"epoch":"x"}',
            "event: tool_call", 'data: {"name":"ivilson_read"}',
            "event: content", 'data: {"content":"你好，","conversationId":"conv-1"}',
            "event: content", 'data: {"text":"世界"}',
            "event: done", 'data: {"messageId":"m1","success":true}',
        )
        self.assertTrue(done)
        self.assertEqual("你好，世界", acc.result.reply)
        self.assertEqual(["ivilson_read"], acc.result.tools)
        self.assertEqual("conv-1", acc.result.conversation_id)
        self.assertEqual(7, acc.result.last_event_id)
        self.assertIsNone(acc.result.error)

    def test_a_done_for_another_message_does_not_end_this_one(self):
        acc = c.SseAccumulator("mine")
        self.assertFalse(feed(acc, "event: done", 'data: {"messageId":"someone-elses"}'))
        self.assertFalse(acc.result.done)
        self.assertTrue(feed(acc, "event: done", 'data: {"messageId":"mine"}'))

    def test_an_error_event_ends_the_message_and_keeps_the_reason(self):
        acc = c.SseAccumulator("m1")
        self.assertTrue(feed(acc, "event: error", 'data: {"messageId":"m1","detail":"model_stream_failed (boom)"}'))
        self.assertTrue(acc.result.done)
        self.assertIn("boom", acc.result.error)

    def test_tool_events_without_a_name_and_malformed_data_do_not_crash(self):
        acc = c.SseAccumulator(None)
        feed(acc, "event: tool_call", "data: not json", "event: content", "data: [1,2]", "event: done", "data: {}")
        self.assertTrue(acc.result.done)
        self.assertEqual(1, len(acc.result.tools))


class EvaluateTests(unittest.TestCase):
    def chat(self, **kw):
        base = dict(reply="ok", tools=["ivilson_read"], done=True, error=None)
        base.update(kw)
        return c.ChatResult(**base)

    def ok(self, checks):
        return all(check["ok"] for check in checks)

    def test_tool_expectations(self):
        chat = self.chat(tools=["create_session_plan"])
        self.assertTrue(self.ok(s.evaluate({"tools_include": ["create_session_plan"], "tools_exclude": ["spawn_worker"]},
                                           chat=chat, jobs=[], tasklist=None, receipt=None, failure_codes=[])))
        self.assertFalse(self.ok(s.evaluate({"tools_exclude": ["create_session_plan"]},
                                            chat=chat, jobs=[], tasklist=None, receipt=None, failure_codes=[])))

    def test_a_chat_error_fails_unless_it_is_expected(self):
        chat = self.chat(error="boom")
        self.assertFalse(self.ok(s.evaluate({}, chat=chat, jobs=[], tasklist=None, receipt=None, failure_codes=[])))
        self.assertTrue(self.ok(s.evaluate({"chat_error": True}, chat=chat, jobs=[], tasklist=None, receipt=None, failure_codes=[])))

    def test_an_unfinished_chat_fails(self):
        self.assertFalse(self.ok(s.evaluate({}, chat=self.chat(done=False), jobs=[], tasklist=None, receipt=None, failure_codes=[])))

    def test_a_retry_the_host_queued_can_be_required_by_count_and_by_the_code_that_caused_it(self):
        retries = [{"reason": "forge_attempt_changed_nothing", "detailCode": "dependency_contract_unmet"}]
        wanted = {"jobs": {"retried_min": 1, "retry_detail_code": "dependency_contract_unmet"}}
        self.assertTrue(self.ok(s.evaluate(wanted, chat=None, jobs=[], tasklist=None, receipt=None, failure_codes=[], retries=retries)))
        self.assertFalse(self.ok(s.evaluate(wanted, chat=None, jobs=[], tasklist=None, receipt=None, failure_codes=[], retries=[])))
        self.assertFalse(self.ok(s.evaluate(wanted, chat=None, jobs=[], tasklist=None, receipt=None, failure_codes=[],
                                            retries=[{"detailCode": "no_relevant_change"}])))
        self.assertTrue(self.ok(s.evaluate({"jobs": {"retried_max": 0}}, chat=None, jobs=[], tasklist=None, receipt=None, failure_codes=[], retries=[])))

    def test_job_expectations(self):
        jobs = [{"status": "Completed"}, {"status": "Completed"}, {"status": "Failed"}]
        self.assertFalse(self.ok(s.evaluate({"jobs": {"none": True}}, chat=None, jobs=jobs, tasklist=None, receipt=None, failure_codes=[])))
        self.assertTrue(self.ok(s.evaluate({"jobs": {"none": True}}, chat=None, jobs=[], tasklist=None, receipt=None, failure_codes=[])))
        self.assertTrue(self.ok(s.evaluate({"jobs": {"completed_min": 2, "failed_max": 1}}, chat=None, jobs=jobs, tasklist=None,
                                           receipt=None, failure_codes=[])))
        self.assertFalse(self.ok(s.evaluate({"jobs": {"failed_max": 0}}, chat=None, jobs=jobs, tasklist=None, receipt=None, failure_codes=[])))
        self.assertTrue(self.ok(s.evaluate({"jobs": {"failed_detail_code": "x"}}, chat=None, jobs=jobs, tasklist=None, receipt=None,
                                           failure_codes=["x"])))
        self.assertFalse(self.ok(s.evaluate({"jobs": {"failed_detail_code": "x"}}, chat=None, jobs=jobs, tasklist=None, receipt=None,
                                            failure_codes=["y"])))
        # several honest failure codes may be listed; any one of them satisfies the step
        self.assertTrue(self.ok(s.evaluate({"jobs": {"failed_detail_code": ["x", "y"]}}, chat=None, jobs=jobs, tasklist=None, receipt=None,
                                           failure_codes=["y"])))
        self.assertFalse(self.ok(s.evaluate({"jobs": {"failed_detail_code": ["x", "y"]}}, chat=None, jobs=jobs, tasklist=None, receipt=None,
                                            failure_codes=["z"])))

    def test_audit_state_in_accepts_any_listed_state_and_rejects_the_rest(self):
        want = {"verify": {"build_passed": True, "audit_state_in": ["Pass", "NeedsReview"]}}
        for state, ok in ((1, True), (3, True), ("NeedsReview", True), (2, False), (4, False), (0, False)):
            receipt = {"buildPassed": True, "audit": {"state": state}}
            self.assertEqual(ok, self.ok(s.evaluate(want, chat=None, jobs=[], tasklist=None, receipt=receipt, failure_codes=[])), state)

    def test_audit_state_in_with_findings_above_low_is_not_accepted_when_a_ceiling_is_given(self):
        want = {"verify": {"audit_state_in": ["Pass", "NeedsReview"], "max_finding_severity": "Low"}}
        low = {"audit": {"state": 3, "findings": [{"severity": "Low"}]}}
        moderate = {"audit": {"state": 3, "findings": [{"severity": "Low"}, {"severity": "Moderate"}]}}
        self.assertTrue(self.ok(s.evaluate(want, chat=None, jobs=[], tasklist=None, receipt=low, failure_codes=[])))
        self.assertFalse(self.ok(s.evaluate(want, chat=None, jobs=[], tasklist=None, receipt=moderate, failure_codes=[])))
        self.assertTrue(self.ok(s.evaluate(want, chat=None, jobs=[], tasklist=None, receipt={"audit": {"state": 1}}, failure_codes=[])))

    def test_tasklist_bounds(self):
        self.assertTrue(self.ok(s.evaluate({"tasklist_min": 1, "tasklist_max": 3}, chat=None, jobs=[], tasklist=[{}], receipt=None, failure_codes=[])))
        self.assertFalse(self.ok(s.evaluate({"tasklist_min": 1}, chat=None, jobs=[], tasklist=[], receipt=None, failure_codes=[])))

    def test_verify_expectations_accept_numeric_and_named_audit_states(self):
        want = {"verify": {"build_passed": True, "tests_passed": True, "audit_state": "Pass"}}
        for state in (1, "Pass"):
            receipt = {"buildPassed": True, "testsPassed": True, "audit": {"state": state}}
            self.assertTrue(self.ok(s.evaluate(want, chat=None, jobs=[], tasklist=None, receipt=receipt, failure_codes=[])))
        receipt = {"buildPassed": True, "testsPassed": True, "audit": {"state": 3}}
        self.assertFalse(self.ok(s.evaluate(want, chat=None, jobs=[], tasklist=None, receipt=receipt, failure_codes=[])))
        self.assertTrue(self.ok(s.evaluate({"verify": {"failure_code": "ArtifactRestoreFailed"}}, chat=None, jobs=[], tasklist=None,
                                           receipt={"failureCode": "ArtifactRestoreFailed"}, failure_codes=[])))


class ValidationTests(unittest.TestCase):
    base = {"name": "n", "steps": [{"say": "hi"}]}

    def test_typos_are_rejected_instead_of_silently_checking_nothing(self):
        for step in ({"say": "x", "expect": {"tool_exclude": ["a"]}}, {"say": "x", "expect": {"jobs": {"nones": True}}},
                     {"say": "x", "expcet": {}}, {"verify": {}, "expect": {"verify": {"audit": "Pass"}}}, {"name": "empty"}):
            with self.assertRaises(s.ScenarioError, msg=str(step)):
                s.validate_scenario({"name": "n", "steps": [step]})

    def test_a_verify_expectation_needs_a_verify_step(self):
        with self.assertRaises(s.ScenarioError):
            s.validate_scenario({"name": "n", "steps": [{"say": "x", "expect": {"verify": {"build_passed": True}}}]})

    def test_every_shipped_scenario_is_valid_and_named_after_its_file(self):
        directory = DEVSIM / "scenarios"
        files = sorted(directory.glob("*.json"))
        self.assertGreaterEqual(len(files), 5)
        for path in files:
            self.assertEqual(path.stem, s.load_scenario(path)["name"])


class FakeClient:
    def __init__(self, chats=None, jobs=None, tasklist=None, receipt=None, fail_on_say=False):
        self.chats = list(chats or [])
        self._jobs = jobs or []
        self._tasklist = tasklist or []
        self.receipt = receipt or {}
        self.fail_on_say = fail_on_say
        self.said = []

    def login(self): pass
    def open_session(self, name): self.session = name
    def say(self, message, **kw):
        if self.fail_on_say:
            raise c.GatewayError("host down")
        self.said.append((message, kw))
        return self.chats.pop(0)
    def wait_for_jobs(self, **kw): return self._jobs
    def jobs(self): return self._jobs
    def tasklist(self): return self._tasklist
    def job_failure(self, job_id): return {"detailCode": "offline_restore_unavailable"}
    def verify(self, job_id, timeout_minutes=20): return self.receipt


def chat(**kw):
    base = dict(reply="ok", tools=[], done=True, error=None)
    base.update(kw)
    return c.ChatResult(**base)


class RunnerTests(unittest.TestCase):
    scenario = {"name": "demo", "repo": "https://example.invalid/r.git", "steps": [
        {"say": "clone", "repo": True, "expect": {"jobs": {"none": True}}},
        {"say": "ask", "expect": {"tools_exclude": ["spawn_worker"]}},
    ]}

    def run_it(self, client, scenario=None):
        return s.run_scenario(scenario or self.scenario, client, log=lambda _: None)

    def test_a_scenario_whose_steps_all_hold_passes_and_passes_the_repo_only_where_asked(self):
        client = FakeClient(chats=[chat(), chat(tools=["ivilson_read"])])
        result = self.run_it(client)
        self.assertTrue(result["passed"])
        self.assertEqual("https://example.invalid/r.git", client.said[0][1]["repo"])
        self.assertIsNone(client.said[1][1]["repo"])

    def test_the_first_failed_step_stops_the_run_and_later_steps_are_skipped(self):
        client = FakeClient(chats=[chat(), chat()], jobs=[{"status": "Running", "jobId": "j"}])
        result = self.run_it(client)
        self.assertFalse(result["passed"])
        self.assertFalse(result["steps"][0]["passed"])
        self.assertTrue(result["steps"][1]["skipped"])
        self.assertEqual(1, len(client.said))

    def test_a_broken_step_is_a_failed_step_with_a_reason_not_a_crash(self):
        result = self.run_it(FakeClient(fail_on_say=True))
        self.assertFalse(result["passed"])
        self.assertIn("host down", result["steps"][0]["error"])

    def test_verify_picks_the_last_completed_forge_job_and_checks_the_receipt(self):
        jobs = [{"jobType": "ForgeWorker", "status": "Completed", "taskId": "T1", "jobId": "j1"},
                {"jobType": "ForgeWorker", "status": "Completed", "taskId": "T2", "jobId": "j2"},
                {"jobType": "ExploreWorker", "status": "Completed", "taskId": "T0", "jobId": "j0"}]
        scenario = {"name": "v", "steps": [{"verify": {}, "expect": {"verify": {"build_passed": True, "audit_state": "Pass"}}}]}
        client = FakeClient(jobs=jobs, receipt={"buildPassed": True, "audit": {"state": 1}})
        seen = []
        client.verify = lambda job_id, timeout_minutes=20: seen.append(job_id) or {"buildPassed": True, "audit": {"state": 1}}
        self.assertTrue(self.run_it(client, scenario)["passed"])
        self.assertEqual(["j2"], seen)

    def test_verify_targets_the_job_that_finished_last_not_the_one_with_the_alphabetically_last_task_id(self):
        # Plans may run the regression test first and the fix second (TASK_TEST_001 before TASK_FIX_002): verifying the earlier artifact
        # reports "head changed". The chain's head is whoever completed last.
        jobs = [{"jobType": "ForgeWorker", "status": "Completed", "taskId": "TASK_FIX_002", "jobId": "fix", "updatedAtUtc": "2026-11-08T17:47:01Z"},
                {"jobType": "ForgeWorker", "status": "Completed", "taskId": "TASK_TEST_001", "jobId": "test", "updatedAtUtc": "2026-11-08T17:46:01Z"},
                {"jobType": "ForgeWorker", "status": "Failed", "taskId": "TASK_ZZZ", "jobId": "late-failure", "updatedAtUtc": "2026-11-08T17:59:00Z"}]
        client = FakeClient(jobs=jobs)
        seen = []
        client.verify = lambda job_id, timeout_minutes=20: seen.append(job_id) or {"buildPassed": True}
        s.run_scenario({"name": "v", "steps": [{"verify": {}}]}, client, log=lambda _: None)
        self.assertEqual(["fix"], seen)

    def test_verify_without_a_completed_forge_job_fails_clearly(self):
        scenario = {"name": "v", "steps": [{"verify": {}}]}
        result = self.run_it(FakeClient(jobs=[]), scenario)
        self.assertFalse(result["passed"])
        self.assertIn("no", result["steps"][0]["error"].lower())

    def test_failure_codes_are_read_from_failed_jobs(self):
        scenario = {"name": "f", "steps": [{"wait_jobs": {}, "expect": {"jobs": {"failed_detail_code": "offline_restore_unavailable"}}}]}
        result = self.run_it(FakeClient(jobs=[{"status": "Failed", "jobId": "j"}]), scenario)
        self.assertTrue(result["passed"])


class DiagnosticsTests(unittest.TestCase):
    """A failed run must say why: which session, how every job ended (with the failure detail), and what verification reported."""

    def test_the_result_carries_the_session_the_jobs_and_a_condensed_receipt(self):
        jobs = [{"jobType": "ForgeWorker", "status": "Completed", "taskId": "T1", "jobId": "j1"},
                {"jobType": "ForgeWorker", "status": "Failed", "taskId": "T2", "jobId": "j2"}]
        receipt = {"buildPassed": False, "testsPassed": False, "failureCode": "ArtifactRestoreFailed", "message": "m",
                   "audit": {"state": 3, "reasonCode": "ModelFindings",
                             "layers": [{"name": "model", "state": 3, "reasonCode": "ModelFindings", "message": "2 findings"}],
                             "findings": [{"severity": "Low", "location": "A.cs:1-2", "evidence": "x" * 500}]}}
        client = FakeClient(jobs=jobs, receipt=receipt)
        client.session_id = "sess-1"
        scenario = {"name": "d", "steps": [{"verify": {}, "expect": {"verify": {"build_passed": True}}}]}

        result = s.run_scenario(scenario, client, log=lambda _: None)

        self.assertEqual("sess-1", result["session_id"])
        step = result["steps"][0]
        self.assertEqual([("T1", "Completed", None), ("T2", "Failed", "offline_restore_unavailable")],
                         [(j["task"], j["status"], j.get("detail_code")) for j in step["jobs"]])
        self.assertEqual("ArtifactRestoreFailed", step["receipt"]["failure_code"])
        self.assertEqual(3, step["receipt"]["audit"]["state"])
        self.assertEqual("ModelFindings", step["receipt"]["audit"]["layers"][0]["reason"])
        self.assertLessEqual(len(step["receipt"]["audit"]["findings"][0]["evidence"]), 240)

    def test_a_job_failure_that_cannot_be_read_does_not_break_the_run(self):
        client = FakeClient(jobs=[{"jobType": "ForgeWorker", "status": "Failed", "taskId": "T", "jobId": "j"}])
        client.job_failure = lambda job_id: (_ for _ in ()).throw(c.GatewayError("history unavailable"))
        result = s.run_scenario({"name": "d", "steps": [{"wait_jobs": {}}]}, client, log=lambda _: None)
        self.assertIn("status", result["steps"][0]["jobs"][0])


class FixtureScenarioTests(unittest.TestCase):
    scenario = {"name": "fx", "repo": "fixture:discounts", "steps": [{"say": "拉取 {repo}", "repo": True}]}

    def test_a_fixture_repo_is_resolved_and_substituted_into_the_message_and_the_request(self):
        client = FakeClient(chats=[chat()])
        seen = []
        result = s.run_scenario(self.scenario, client, log=lambda _: None, fixture_resolver=lambda name: seen.append(name) or "file:///tmp/x/")
        self.assertTrue(result["passed"])
        self.assertEqual(["discounts"], seen)
        self.assertEqual("拉取 file:///tmp/x/", client.said[0][0])
        self.assertEqual("file:///tmp/x/", client.said[0][1]["repo"])
        self.assertEqual("file:///tmp/x/", result["repo"])

    def test_a_fixture_repo_without_a_resolver_is_a_clear_error(self):
        with self.assertRaises(s.ScenarioError):
            s.run_scenario(self.scenario, FakeClient(chats=[chat()]), log=lambda _: None)

    def test_an_unknown_fixture_is_rejected_when_the_scenario_is_loaded(self):
        with self.assertRaises(s.ScenarioError):
            s.validate_scenario({"name": "n", "repo": "fixture:nope", "steps": [{"say": "x"}]})

    def test_every_scenario_that_names_a_fixture_uses_the_placeholder_not_the_raw_name(self):
        for path in sorted((DEVSIM / "scenarios").glob("*.json")):
            scenario = s.load_scenario(path)
            if scenario.get("repo", "").startswith("fixture:"):
                for step in scenario["steps"]:
                    self.assertNotIn("fixture:", step.get("say", ""), f"{path.name}: use {{repo}} in the message")


class WaitForJobsTests(unittest.TestCase):
    def test_waits_until_jobs_have_been_quiet_for_several_polls(self):
        snapshots = iter([[{"status": "Running"}], [{"status": "Completed"}], [{"status": "Running"}], [{"status": "Completed"}],
                          [{"status": "Completed"}], [{"status": "Completed"}], [{"status": "Completed"}]])
        polls = []
        gateway = c.GatewayClient("http://127.0.0.1:1", password="x")
        gateway.session_id = "s"
        gateway.jobs = lambda: polls.append(1) or next(snapshots)
        now = [0.0]
        result = gateway.wait_for_jobs(timeout_minutes=60, poll_seconds=1, quiet_polls=3, sleep=lambda _: now.__setitem__(0, now[0] + 1),
                                       clock=lambda: now[0])
        self.assertEqual(6, len(polls))  # a task starting between polls resets the quiet count
        self.assertEqual("Completed", result[0]["status"])

    def test_returns_at_once_when_every_task_succeeded_and_no_job_is_active(self):
        polls = []
        gateway = c.GatewayClient("http://127.0.0.1:1", password="x")
        gateway.jobs = lambda: polls.append(1) or [{"status": "Completed"}]
        gateway.tasklist = lambda: [{"id": "T1", "status": "completed"}, {"id": "T2", "status": "CompletedWithWarnings"}]
        now = [0.0]
        gateway.wait_for_jobs(timeout_minutes=60, sleep=lambda seconds: now.__setitem__(0, now[0] + seconds), clock=lambda: now[0])
        self.assertEqual(1, len(polls))
        self.assertEqual(0.0, now[0])  # nothing is left to start, so there is nothing to wait for

    def test_keeps_the_quiet_window_while_a_task_is_unfinished_or_failed(self):
        for statuses in (["completed", "pending"], ["failed"]):
            polls = []
            gateway = c.GatewayClient("http://127.0.0.1:1", password="x")
            gateway.jobs = lambda: polls.append(1) or [{"status": "Completed"}]
            gateway.tasklist = lambda: [{"id": f"T{i}", "status": status} for i, status in enumerate(statuses)]
            now = [0.0]
            gateway.wait_for_jobs(timeout_minutes=60, poll_seconds=1, quiet_polls=3,
                                  sleep=lambda seconds: now.__setitem__(0, now[0] + seconds), clock=lambda: now[0])
            self.assertEqual(3, len(polls), statuses)  # the next task, or a retry, may still be dispatched

    def test_gives_up_at_the_deadline(self):
        gateway = c.GatewayClient("http://127.0.0.1:1", password="x")
        gateway.jobs = lambda: [{"status": "Running"}]
        now = [0.0]
        gateway.wait_for_jobs(timeout_minutes=1, poll_seconds=20, sleep=lambda _: now.__setitem__(0, now[0] + 20), clock=lambda: now[0])
        self.assertGreaterEqual(now[0], 60)


class TaskTimingTests(unittest.TestCase):
    def test_reports_how_long_each_task_really_ran(self):
        timings = s.task_timings([
            {"id": "T1", "status": "completed", "startedAt": "2026-11-10T04:39:58.0456358Z", "finishedAt": "2026-11-10T04:40:37.256122Z"},
            {"id": "T2", "status": "pending", "startedAt": None, "finishedAt": None},
        ])
        self.assertEqual([{"task": "T1", "status": "completed", "duration_s": 39.2}, {"task": "T2", "status": "pending", "duration_s": None}], timings)

    def test_a_wait_step_records_task_timings(self):
        scenario = {"name": "demo", "steps": [{"wait_jobs": {}, "expect": {}}]}
        client = FakeClient(jobs=[{"jobId": "j", "taskId": "T1", "jobType": "ForgeWorker", "status": "Completed"}],
                            tasklist=[{"id": "T1", "status": "completed", "startedAt": "2026-11-10T04:00:00Z", "finishedAt": "2026-11-10T04:00:57Z"}])
        result = s.run_scenario(scenario, client, log=lambda _: None)
        self.assertEqual([{"task": "T1", "status": "completed", "duration_s": 57.0}], result["steps"][0]["tasks"])


class SeedPasswordTests(unittest.TestCase):
    def test_reads_the_seed_password_and_tolerates_a_missing_file(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "SeedData.cs"
            path.write_text('string password = "local-dev-pass";', encoding="utf-8")
            self.assertEqual("local-dev-pass", c.read_seed_password(path))
            self.assertIsNone(c.read_seed_password(Path(directory) / "missing.cs"))


class ApprovalAndProfileTests(unittest.TestCase):
    def test_auto_approve_submits_control_plane_decision_and_records_digest_and_unsafe_warning(self):
        with tempfile.TemporaryDirectory() as directory:
            host_tok = Path(directory) / "host.token"
            ctrl_tok = Path(directory) / "control.token"
            host_tok.write_text("normal-token-12345678", encoding="utf-8")
            ctrl_tok.write_text("control-token-87654321", encoding="utf-8")

            calls = []
            gateway = c.GatewayClient(
                "http://127.0.0.1:1",
                token_file=str(host_tok),
                auto_approve=True,
            )

            def fake_request(method, path, body=None, timeout=60, *, use_control=False):
                calls.append((method, path, body, use_control))
                if method == "GET" and path == "/api/settings/execution":
                    return 200, '{"backend":"Local","permissionMode":"Ask","controlPlaneAvailable":true,"selfApprovalProtection":false}'
                if method == "GET" and path.endswith("/approvals"):
                    return 200, '{"sessionId":"s1","count":1,"controlPlaneAvailable":true,"selfApprovalProtection":false,"items":[{"approvalId":"appr-1","requestDigest":"sha256-abc"}]}'
                if method == "POST" and path == "/api/gateway/approvals/appr-1":
                    return 200, '{"success":true}'
                raise AssertionError(f"unexpected {method} {path}")

            gateway.request = fake_request
            gateway.login()
            gateway.session_id = "s1"
            approved = gateway.drain_pending_approvals()
            self.assertEqual(["sha256-abc"], approved)
            self.assertEqual(["sha256-abc"], gateway.approved_digests)
            self.assertFalse(gateway.self_approval_protection)
            self.assertTrue(any(m == "POST" and p == "/api/gateway/approvals/appr-1" and ctrl for (m, p, _, ctrl) in calls))

            scenario = {"name": "demo", "steps": [{"wait_jobs": {}, "expect": {}}]}
            fake = FakeClient(jobs=[])
            fake.approved_digests = gateway.approved_digests
            fake.self_approval_protection = gateway.self_approval_protection
            out = s.run_scenario(scenario, fake, log=lambda _: None)
            self.assertEqual(["sha256-abc"], out["approved_request_digests"])
            self.assertIs(False, out["self_approval_protection"])
            self.assertIn("防自批保证不成立", out["security_warning"])

    def test_auto_approve_recovers_missed_approval_via_heartbeat_pending_count_and_replay_gap(self):
        with tempfile.TemporaryDirectory() as directory:
            host_tok = Path(directory) / "host.token"
            ctrl_tok = Path(directory) / "control.token"
            host_tok.write_text("normal-token-12345678", encoding="utf-8")
            ctrl_tok.write_text("control-token-87654321", encoding="utf-8")

            gateway = c.GatewayClient(
                "http://127.0.0.1:1",
                token_file=str(host_tok),
                auto_approve=True,
            )
            pending_queue = [
                {"approvalId": "appr-missed-1", "requestDigest": "digest-from-heartbeat"},
                {"approvalId": "appr-missed-2", "requestDigest": "digest-from-replay-gap"},
            ]

            def fake_request(method, path, body=None, timeout=60, *, use_control=False):
                if method == "GET" and path == "/api/settings/execution":
                    return 200, '{"backend":"Local","permissionMode":"Ask","controlPlaneAvailable":true,"selfApprovalProtection":false}'
                if method == "POST" and path == "/api/gateway/chat":
                    return 202, '{"messageId":"msg-1"}'
                if method == "GET" and path == "/api/gateway/session/s-rec/approvals":
                    if pending_queue:
                        nxt = pending_queue[0]
                        return 200, (
                            '{"sessionId":"s-rec","count":1,"controlPlaneAvailable":true,"selfApprovalProtection":false,'
                            f'"items":[{{"approvalId":"{nxt["approvalId"]}","requestDigest":"{nxt["requestDigest"]}"}}]}}'
                        )
                    return 200, '{"sessionId":"s-rec","count":0,"controlPlaneAvailable":true,"selfApprovalProtection":false,"items":[]}'
                if method == "POST" and path.startswith("/api/gateway/approvals/"):
                    assert use_control is True
                    aid = path.rsplit("/", 1)[-1]
                    pending_queue[:] = [p for p in pending_queue if p["approvalId"] != aid]
                    return 200, '{"success":true}'
                raise AssertionError(f"unexpected {method} {path}")

            # Notice: ZERO `tool_approval_request` SSE events are emitted! Recovery happens strictly via
            # heartbeat.pendingApprovals > 0 and replay_gap reconciliation.
            sse_batches = [
                [
                    "id: 1",
                    "event: heartbeat",
                    'data: {"pendingApprovals": 1}',
                    "",
                    "id: 2",
                    "event: replay_gap",
                    'data: {"dropped": 5}',
                    "",
                    "id: 3",
                    "event: done",
                    'data: {"messageId":"msg-1"}',
                    "",
                ]
            ]

            gateway.request = fake_request
            gateway.login()
            gateway.session_id = "s-rec"
            # Clear initial drain on entry so only heartbeat + replay_gap inside stream trigger the two approvals
            initial_pop = pending_queue[:]
            pending_queue.clear()

            def stream_lines(_sid, _last_id):
                pending_queue.extend(initial_pop)
                return sse_batches.pop(0)

            res = gateway.say("run task", line_source=stream_lines)
            self.assertTrue(res.done)
            self.assertEqual(["digest-from-heartbeat", "digest-from-replay-gap"], gateway.approved_digests)


if __name__ == "__main__":
    unittest.main()
