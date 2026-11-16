# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Scenario model and runner for the developer simulation.

A scenario is a short conversation a developer could have with CodexFlow, plus what must be true afterwards. Expectations are
structural (which tools ran, which jobs exist, how they ended, what verification said), not wording of the model's reply: the
point is to catch the system misbehaving, not the model phrasing things differently.
"""
from __future__ import annotations

import json
import re
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable

from fixtures import known_fixture_names

AUDIT_STATES = {0: "NotRun", 1: "Pass", 2: "Fail", 3: "NeedsReview", 4: "Error"}
STEP_KEYS = {"name", "say", "repo", "accept_debt", "approve_upgrades", "timeout_min", "wait_jobs", "verify", "expect"}
EXPECT_KEYS = {"reply_not_empty", "reply_contains_any", "reply_not_contains", "tools_include", "tools_exclude", "chat_error",
               "jobs", "tasklist_min", "tasklist_max", "verify"}
JOB_EXPECT_KEYS = {"none", "completed_min", "failed_max", "failed_detail_code", "retried_min", "retried_max", "retry_detail_code"}
VERIFY_EXPECT_KEYS = {"build_passed", "tests_passed", "audit_state", "audit_state_in", "max_finding_severity", "failure_code"}
SEVERITY_RANK = {"Low": 1, "Moderate": 2, "High": 3, "Critical": 4}


class ScenarioError(ValueError):
    """The scenario file itself is wrong (caught before anything runs)."""


def validate_scenario(scenario: dict[str, Any]) -> None:
    """Rejects typos in keys: a misspelt expectation would otherwise silently check nothing."""
    for key in ("name", "steps"):
        if key not in scenario:
            raise ScenarioError(f"scenario is missing '{key}'")
    repo = scenario.get("repo", "")
    if repo.startswith("fixture:") and repo[len("fixture:"):] not in known_fixture_names():
        raise ScenarioError(f"{scenario['name']}: unknown fixture '{repo[len('fixture:'):]}' (known: {known_fixture_names()})")
    if not scenario["steps"]:
        raise ScenarioError(f"{scenario['name']}: no steps")
    for index, step in enumerate(scenario["steps"], 1):
        where = f"{scenario['name']} step {index}"
        unknown = set(step) - STEP_KEYS
        if unknown:
            raise ScenarioError(f"{where}: unknown keys {sorted(unknown)}")
        if not any(key in step for key in ("say", "wait_jobs", "verify")):
            raise ScenarioError(f"{where}: needs say, wait_jobs or verify")
        expect = step.get("expect", {})
        bad = set(expect) - EXPECT_KEYS
        if bad:
            raise ScenarioError(f"{where}: unknown expect keys {sorted(bad)}")
        bad = set(expect.get("jobs", {})) - JOB_EXPECT_KEYS
        if bad:
            raise ScenarioError(f"{where}: unknown jobs expect keys {sorted(bad)}")
        bad = set(expect.get("verify", {})) - VERIFY_EXPECT_KEYS
        if bad:
            raise ScenarioError(f"{where}: unknown verify expect keys {sorted(bad)}")
        if "verify" in expect and "verify" not in step:
            raise ScenarioError(f"{where}: expects a verify result but does not verify")


def load_scenario(path: Path) -> dict[str, Any]:
    scenario = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    validate_scenario(scenario)
    return scenario


def load_all(directory: Path) -> list[dict[str, Any]]:
    return [load_scenario(path) for path in sorted(Path(directory).glob("*.json"))]


def _check(checks: list[dict[str, Any]], name: str, ok: bool, detail: str = "") -> None:
    checks.append({"name": name, "ok": bool(ok), "detail": detail})


def _audit_state(receipt: dict[str, Any]) -> str | None:
    audit = receipt.get("audit") or receipt.get("Audit") or {}
    state = audit.get("state", audit.get("State"))
    return AUDIT_STATES.get(state) if isinstance(state, int) else state


def evaluate(expect: dict[str, Any], *, chat: Any, jobs: list[dict[str, Any]], tasklist: list[dict[str, Any]] | None,
             receipt: dict[str, Any] | None, failure_codes: list[str],
             retries: list[dict[str, Any]] | None = None) -> list[dict[str, Any]]:
    """Turns an ``expect`` block into a list of checks. Pure: everything it needs is passed in."""
    checks: list[dict[str, Any]] = []

    if chat is not None:
        expects_error = expect.get("chat_error", False)
        _check(checks, "chat finished", chat.done, "no done event before the time limit" if not chat.done else "")
        _check(checks, "chat error" if expects_error else "no chat error", bool(chat.error) == bool(expects_error),
               (chat.error or "")[:300])
        if expect.get("reply_not_empty"):
            _check(checks, "reply is not empty", bool(chat.reply.strip()))
        any_of = expect.get("reply_contains_any")
        if any_of:
            _check(checks, "reply mentions one of " + ", ".join(any_of), any(text in chat.reply for text in any_of))
        for text in expect.get("reply_not_contains", []):
            _check(checks, f"reply does not contain '{text}'", text not in chat.reply)
        for tool in expect.get("tools_include", []):
            _check(checks, f"tool '{tool}' was called", tool in chat.tools, "called: " + ", ".join(chat.tools))
        for tool in expect.get("tools_exclude", []):
            _check(checks, f"tool '{tool}' was not called", tool not in chat.tools, "called: " + ", ".join(chat.tools))

    wanted = expect.get("jobs")
    if wanted is not None:
        statuses = [job.get("status") for job in jobs]
        if wanted.get("none"):
            _check(checks, "no jobs were started", not jobs, f"{len(jobs)} job(s): {statuses}")
        if "completed_min" in wanted:
            done = sum(1 for status in statuses if status == "Completed")
            _check(checks, f"at least {wanted['completed_min']} job(s) completed", done >= wanted["completed_min"], f"statuses: {statuses}")
        if "failed_max" in wanted:
            failed = sum(1 for status in statuses if status in ("Failed", "FailedRecoveryNeeded"))
            _check(checks, f"at most {wanted['failed_max']} job(s) failed", failed <= wanted["failed_max"], f"statuses: {statuses}")
        if "failed_detail_code" in wanted:
            accepted = wanted["failed_detail_code"]
            accepted = [accepted] if isinstance(accepted, str) else list(accepted)
            _check(checks, f"a job failed with one of {accepted}", any(code in failure_codes for code in accepted),
                   f"failure codes: {failure_codes}")

    if wanted is not None and any(key in wanted for key in ("retried_min", "retried_max", "retry_detail_code")):
        queued = retries or []
        codes = [str(retry.get("detailCode")) for retry in queued]
        if "retried_min" in wanted:
            _check(checks, f"the host queued at least {wanted['retried_min']} retry(ies)", len(queued) >= wanted["retried_min"], f"retries: {codes}")
        if "retried_max" in wanted:
            _check(checks, f"the host queued at most {wanted['retried_max']} retry(ies)", len(queued) <= wanted["retried_max"], f"retries: {codes}")
        if "retry_detail_code" in wanted:
            accepted = wanted["retry_detail_code"]
            accepted = [accepted] if isinstance(accepted, str) else list(accepted)
            _check(checks, f"a retry was queued for one of {accepted}", any(code in accepted for code in codes), f"retries: {codes}")

    if "tasklist_min" in expect or "tasklist_max" in expect:
        count = len(tasklist or [])
        if "tasklist_min" in expect:
            _check(checks, f"task list has at least {expect['tasklist_min']} task(s)", count >= expect["tasklist_min"], f"{count} task(s)")
        if "tasklist_max" in expect:
            _check(checks, f"task list has at most {expect['tasklist_max']} task(s)", count <= expect["tasklist_max"], f"{count} task(s)")

    wanted_verify = expect.get("verify")
    if wanted_verify is not None:
        receipt = receipt or {}
        mapping = {"build_passed": "buildPassed", "tests_passed": "testsPassed", "failure_code": "failureCode"}
        for key, field in mapping.items():
            if key in wanted_verify:
                actual = receipt.get(field)
                _check(checks, f"verify {key} == {wanted_verify[key]}", actual == wanted_verify[key], f"actual: {actual}")
        if "audit_state" in wanted_verify:
            actual = _audit_state(receipt)
            _check(checks, f"audit state == {wanted_verify['audit_state']}", actual == wanted_verify["audit_state"], f"actual: {actual}")
        if "audit_state_in" in wanted_verify:
            actual = _audit_state(receipt)
            _check(checks, "audit state is one of " + ", ".join(wanted_verify["audit_state_in"]), actual in wanted_verify["audit_state_in"], f"actual: {actual}")
        if "max_finding_severity" in wanted_verify:
            ceiling = SEVERITY_RANK[wanted_verify["max_finding_severity"]]
            audit = receipt.get("audit") or receipt.get("Audit") or {}
            worst = max((SEVERITY_RANK.get(str(f.get("severity") or f.get("Severity")), 99) for f in audit.get("findings") or audit.get("Findings") or []), default=0)
            _check(checks, f"no finding above {wanted_verify['max_finding_severity']}", worst <= ceiling, f"worst rank: {worst}")
    return checks


def _job_summaries(client: Any, jobs: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """How every job ended, with the failure detail of failed ones (best effort: a history that cannot be read is not a failed step)."""
    summaries = []
    for job in jobs:
        entry: dict[str, Any] = {"task": job.get("taskId"), "type": job.get("jobType"), "status": job.get("status")}
        if job.get("status") in ("Failed", "FailedRecoveryNeeded"):
            try:
                payload = client.job_failure(job["jobId"])
                entry["detail_code"] = payload.get("detailCode")
                entry["message"] = str(payload.get("message") or "")[:400]
            except Exception as error:  # noqa: BLE001
                entry["detail_unavailable"] = str(error)[:120]
        summaries.append(entry)
    return summaries


def _parse_utc(value: Any) -> datetime | None:
    """Parses the host's ISO timestamps, which carry seven fractional digits (one more than ``fromisoformat`` accepts)."""
    match = re.fullmatch(r"(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d+))?(?:Z|\+00:00)?", str(value or ""))
    if not match:
        return None
    seconds = datetime.strptime(match.group(1), "%Y-%m-%dT%H:%M:%S").replace(tzinfo=timezone.utc)
    return seconds + timedelta(seconds=float(f"0.{match.group(2)}")) if match.group(2) else seconds


def task_timings(tasklist: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """How long each task of the plan really ran, from the host's own timestamps (the step duration also contains the waiting)."""
    timings = []
    for task in tasklist:
        started, finished = _parse_utc(task.get("startedAt")), _parse_utc(task.get("finishedAt"))
        duration = round((finished - started).total_seconds(), 1) if started and finished else None
        timings.append({"task": task.get("id"), "status": task.get("status"), "duration_s": duration})
    return timings


def _condense_receipt(receipt: dict[str, Any]) -> dict[str, Any]:
    audit = receipt.get("audit") or receipt.get("Audit") or {}
    return {
        "failure_code": receipt.get("failureCode"), "build_passed": receipt.get("buildPassed"), "tests_passed": receipt.get("testsPassed"),
        "build_exit_code": receipt.get("buildExitCode"), "message": str(receipt.get("message") or "")[:300],
        "audit": {
            "state": audit.get("state", audit.get("State")), "reason": audit.get("reasonCode", audit.get("ReasonCode")),
            "layers": [{"name": layer.get("name"), "state": layer.get("state"), "reason": layer.get("reasonCode"),
                        "message": str(layer.get("message") or "")[:300]} for layer in audit.get("layers") or []],
            "findings": [{"severity": finding.get("severity"), "location": finding.get("location"),
                          "evidence": str(finding.get("evidence") or "")[:240]} for finding in (audit.get("findings") or [])[:10]],
        },
    }


def _last_forge_job(jobs: list[dict[str, Any]]) -> dict[str, Any] | None:
    forge = [job for job in jobs if job.get("jobType") == "ForgeWorker" and job.get("status") == "Completed"]
    # The chain's head is the artifact that completed last; task ids say nothing about order (a plan may run its test task before its fix).
    return max(forge, key=lambda job: (str(job.get("updatedAtUtc") or ""), str(job.get("taskId")))) if forge else None


def run_scenario(scenario: dict[str, Any], client: Any, *, repo: str | None = None, log: Callable[[str], None] = print,
                 clock: Callable[[], float] = time.time, fixture_resolver: Callable[[str], str] | None = None) -> dict[str, Any]:
    """Runs the steps in order and stops at the first failed step (later steps depend on it).

    A repository written ``fixture:<name>`` is generated on demand by ``fixture_resolver`` (name -> repository URL).
    """
    validate_scenario(scenario)
    repo = repo or scenario.get("repo")
    if repo and repo.startswith("fixture:"):
        if fixture_resolver is None:
            raise ScenarioError(f"{scenario['name']}: '{repo}' needs a fixture resolver (run through the CLI)")
        repo = fixture_resolver(repo[len("fixture:"):])
    started = clock()
    results: list[dict[str, Any]] = []
    failed = False
    client.login()
    client.open_session(f"devsim-{scenario['name']}-{int(started)}")

    for index, step in enumerate(scenario["steps"], 1):
        name = step.get("name") or (step["say"][:40] if "say" in step else ("wait_jobs" if "wait_jobs" in step else "verify"))
        if failed:
            results.append({"index": index, "name": name, "passed": None, "skipped": True, "duration_s": 0, "checks": []})
            continue
        step_started = clock()
        record: dict[str, Any] = {"index": index, "name": name, "passed": False, "skipped": False, "checks": []}
        try:
            chat = None
            if "say" in step:
                log(f"[{scenario['name']}] step {index}: say")
                chat = client.say(step["say"].replace("{repo}", repo or ""), repo=repo if step.get("repo") else None, accept_debt=bool(step.get("accept_debt")),
                                  approve_upgrades=step.get("approve_upgrades"), timeout_minutes=step.get("timeout_min", 10))
            if "wait_jobs" in step:
                log(f"[{scenario['name']}] step {index}: waiting for jobs")
                client.wait_for_jobs(timeout_minutes=step["wait_jobs"].get("timeout_min", 40))
            expect = step.get("expect", {})
            jobs = client.jobs()
            receipt = None
            if "verify" in step:
                target = _last_forge_job(jobs)
                if target is None:
                    raise RuntimeError("verify needs a completed forge job, none exists")
                log(f"[{scenario['name']}] step {index}: verifying {target.get('taskId')}")
                receipt = client.verify(target["jobId"], timeout_minutes=step["verify"].get("timeout_min", 20)
                                        if isinstance(step["verify"], dict) else 20)
            failure_codes: list[str] = []
            if "failed_detail_code" in expect.get("jobs", {}):
                for job in jobs:
                    if job.get("status") in ("Failed", "FailedRecoveryNeeded"):
                        code = client.job_failure(job["jobId"]).get("detailCode")
                        if code:
                            failure_codes.append(str(code))
            tasklist = client.tasklist() if ("tasklist_min" in expect or "tasklist_max" in expect) else None
            retries: list[dict[str, Any]] | None = None
            if any(key in expect.get("jobs", {}) for key in ("retried_min", "retried_max", "retry_detail_code")):
                retries = [retry for job in jobs for retry in client.job_retries(job["jobId"])]
                record["retries"] = retries
            record["checks"] = evaluate(expect, chat=chat, jobs=jobs, tasklist=tasklist, receipt=receipt, failure_codes=failure_codes, retries=retries)
            if chat is not None:
                record["reply_excerpt"] = chat.reply[:400]
                record["tools"] = chat.tools
            record["jobs"] = _job_summaries(client, jobs)
            if "wait_jobs" in step:
                try:
                    record["tasks"] = task_timings(tasklist if tasklist is not None else client.tasklist())
                except Exception as error:  # noqa: BLE001 - timings are diagnostics, not a reason to fail the step
                    record["tasks_unavailable"] = str(error)[:120]
            if receipt is not None:
                record["receipt"] = _condense_receipt(receipt)
            record["passed"] = all(check["ok"] for check in record["checks"])
        except Exception as error:  # a broken step is a failed step with a reason, never a crashed run
            record["error"] = f"{type(error).__name__}: {error}"[:600]
        record["duration_s"] = round(clock() - step_started, 1)
        results.append(record)
        failed = not record["passed"]

    out: dict[str, Any] = {
        "scenario": scenario["name"], "description": scenario.get("description", ""), "repo": repo,
        "session_id": getattr(client, "session_id", None),
        "passed": all(step["passed"] for step in results if not step["skipped"]) and not any(step["skipped"] for step in results),
        "duration_s": round(clock() - started, 1), "steps": results,
    }
    approved_digests = list(getattr(client, "approved_digests", None) or [])
    if approved_digests:
        out["approved_request_digests"] = approved_digests
    if getattr(client, "self_approval_protection", True) is False:
        out["self_approval_protection"] = False
        out["security_warning"] = "防自批保证不成立 (Local:UnsafeFileControlToken=true)"
    return out
