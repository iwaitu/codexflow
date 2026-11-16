# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Gateway client used by the developer-simulation scenarios (stdlib only).

It talks to a running CodexFlow host the way a person's UI does: log in, open a session, send chat messages, follow the
server-sent events until the answer is complete, read jobs and the task list, and call artifact verification.
"""
from __future__ import annotations

import http.client
import json
import os
import re
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Iterable
from urllib.parse import quote, urlparse

REPO_ROOT = Path(__file__).resolve().parents[3]
SEED_DATA = REPO_ROOT / "CodexFlow.Infrastructure" / "SeedData.cs"
TERMINAL_JOB_STATES = {"Completed", "Failed", "FailedRecoveryNeeded", "Cancelled", "Canceled"}
ACTIVE_JOB_STATES = {"Queued", "Pending", "Running", "WaitingUser"}
SUCCEEDED_TASK_STATES = {"completed", "success", "completedwithwarnings"}


class GatewayError(RuntimeError):
    """The host answered with something the scenario cannot continue from."""


@dataclass
class ChatResult:
    """What one chat message produced, as far as a user could see it."""
    reply: str = ""
    tools: list[str] = field(default_factory=list)
    approval_requests: list[dict[str, Any]] = field(default_factory=list)
    needs_approval_reconcile: bool = False
    done: bool = False
    error: str | None = None
    conversation_id: str | None = None
    last_event_id: int = 0


class SseAccumulator:
    """Folds server-sent-event lines into a :class:`ChatResult`. Pure, so it is tested without a network."""

    TEXT_KEYS = ("content", "text", "delta", "message")

    def __init__(self, message_id: str | None):
        self.message_id = message_id
        self.result = ChatResult()
        self._event = ""
        self._parts: list[str] = []

    def feed(self, line: str) -> bool:
        """Consumes one line (without the line break). Returns True once the answer to this message is complete."""
        if line.startswith("id:"):
            try:
                self.result.last_event_id = max(self.result.last_event_id, int(line[3:].strip()))
            except ValueError:
                pass
            return False
        if line.startswith("event:"):
            self._event = line[6:].strip()
            return False
        if not line.startswith("data:"):
            return False

        payload = line[5:].strip()
        try:
            data = json.loads(payload)
        except ValueError:
            data = {}
        if not isinstance(data, dict):
            data = {}

        match = re.search(r'"conversationId"\s*:\s*"([^"]+)"', payload)
        if match:
            self.result.conversation_id = match.group(1)

        if self._event == "tool_call":
            self.result.tools.append(str(data.get("name") or data.get("toolName") or payload[:80]))
        elif self._event == "tool_approval_request":
            if data.get("approvalId") and data.get("requestDigest"):
                self.result.approval_requests.append(data)
            self.result.needs_approval_reconcile = True
        elif self._event == "replay_gap":
            self.result.needs_approval_reconcile = True
        elif self._event == "heartbeat":
            if int(data.get("pendingApprovals") or 0) > 0:
                self.result.needs_approval_reconcile = True
        elif self._event in ("done", "error"):
            if self.message_id and data.get("messageId") not in (None, self.message_id):
                return False
            if self._event == "error":
                self.result.error = str(data.get("detail") or data.get("error") or payload)[:1200]
            self.result.done = True
            self.result.reply = "".join(self._parts)
            return True
        elif self._event not in ("heartbeat", "tool_call", ""):
            for key in self.TEXT_KEYS:
                value = data.get(key)
                if isinstance(value, str):
                    self._parts.append(value)
                    break
        return False


def read_seed_password(seed_file: Path = SEED_DATA) -> str | None:
    """The local development admin password lives in SeedData.cs; used only when no environment value is given."""
    try:
        match = re.search(r'string password = "([^"\r\n]+)"', seed_file.read_text(encoding="utf-8-sig"))
    except OSError:
        return None
    return match.group(1) if match else None


class GatewayClient:
    def __init__(self, base_url: str = "http://127.0.0.1:5205", email: str | None = None, password: str | None = None,
                 token_file: str | None = None, control_token_file: str | None = None,
                 execution_profile: str | None = None, auto_approve: bool = False):
        parsed = urlparse(base_url)
        self.host = parsed.hostname or "127.0.0.1"
        self.port = parsed.port or (443 if parsed.scheme == "https" else 80)
        self.https = parsed.scheme == "https"
        self.email = email or os.environ.get("CODEX_TEST_EMAIL") or "admin@ivilson.com"
        self.password = password or os.environ.get("CODEX_TEST_PASSWORD") or read_seed_password()
        # A local host has no accounts: it writes an access token next to its database, and that file is the login.
        self.token_file = token_file or os.environ.get("CODEX_TEST_TOKEN_FILE")
        self.control_token_file = control_token_file or os.environ.get("CODEX_TEST_CONTROL_TOKEN_FILE")
        self.execution_profile = execution_profile
        self.auto_approve = bool(auto_approve)
        self.token: str | None = None
        self.control_token: str | None = None
        self.self_approval_protection: bool = True
        self.approved_digests: list[str] = []
        self._seen_approval_ids: set[str] = set()
        self.session_id: str | None = None
        self.conversation_id: str | None = None
        self.last_event_id = 0

    # -- transport ------------------------------------------------------------------------------------------------
    def _connection(self, timeout: float):
        factory = http.client.HTTPSConnection if self.https else http.client.HTTPConnection
        return factory(self.host, self.port, timeout=timeout)

    def _headers(self, extra: dict[str, str] | None = None, *, use_control: bool = False) -> dict[str, str]:
        headers = {"Content-Type": "application/json"}
        bearer = (self.control_token or self.token) if use_control else self.token
        if bearer:
            headers["Authorization"] = "Bearer " + bearer
        headers.update(extra or {})
        return headers

    def request(self, method: str, path: str, body: Any = None, timeout: float = 60, *, use_control: bool = False) -> tuple[int, str]:
        connection = self._connection(timeout)
        try:
            connection.request(method, path, json.dumps(body) if body is not None else None,
                               self._headers(use_control=use_control))
            response = connection.getresponse()
            return response.status, response.read().decode("utf-8", "replace")
        finally:
            connection.close()

    def get_json(self, path: str, timeout: float = 60) -> Any:
        status, text = self.request("GET", path, timeout=timeout)
        if status != 200:
            raise GatewayError(f"GET {path} -> {status}: {text[:300]}")
        return json.loads(text)

    # -- session --------------------------------------------------------------------------------------------------
    def login(self) -> None:
        if self.token_file:
            token_path = Path(self.token_file)
            self.token = token_path.read_text(encoding="utf-8").strip()
            if not self.token:
                raise GatewayError(f"The token file {self.token_file} is empty.")
            ctrl_candidate = Path(self.control_token_file) if self.control_token_file else (token_path.parent / "control.token")
            if ctrl_candidate.is_file():
                self.control_token = ctrl_candidate.read_text(encoding="utf-8").strip() or None
            elif self.control_token_file:
                raise GatewayError(f"The control token file {self.control_token_file} does not exist.")
            self._configure_execution_if_needed()
            return
        if not self.password:
            raise GatewayError("No test password: set CODEX_TEST_PASSWORD (or run from a checkout that has SeedData.cs).")
        status, text = self.request("POST", "/api/Auth/login", {"Email": self.email, "Password": self.password})
        if status != 200:
            raise GatewayError(f"Login failed: {status} {text[:200]}")
        self.token = json.loads(text)["token"]
        self.control_token = self.token
        self._configure_execution_if_needed()

    def _configure_execution_if_needed(self) -> None:
        if not self.execution_profile and not self.auto_approve:
            return
        status, text = self.request("GET", "/api/settings/execution")
        current = json.loads(text) if status == 200 else {}
        self.self_approval_protection = bool(current.get("selfApprovalProtection", True))

        profile_targets = {
            "docker-auto": ("Docker", "FullAuto"),
            "local-auto": ("Local", "FullAuto"),
        }
        if self.execution_profile in profile_targets:
            target_backend, target_mode = profile_targets[self.execution_profile]
            backend = str(current.get("backend") or "")
            mode = str(current.get("permissionMode") or "")
            if backend.lower() != target_backend.lower() or mode.lower() != target_mode.lower():
                if not self.control_token:
                    raise GatewayError(
                        f"--execution-profile {self.execution_profile} requires host configured with Execution:Backend={target_backend} and "
                        f"PermissionMode={target_mode} (or pass --control-token-file to switch it via control plane)."
                    )
                put_status, put_text = self.request(
                    "PUT",
                    "/api/settings/execution",
                    {"backend": target_backend, "permissionMode": target_mode},
                    use_control=True,
                )
                if put_status != 200:
                    raise GatewayError(f"Applying execution profile {self.execution_profile} failed: {put_status} {put_text[:300]}")
                current = json.loads(put_text)
                self.self_approval_protection = bool(current.get("selfApprovalProtection", True))

        if self.auto_approve:
            if not current.get("controlPlaneAvailable", True) or not self.control_token:
                raise GatewayError(
                    "--auto-approve requires an active control plane and control token. When running standalone against "
                    "Execution:Backend=Local, enable Local:UnsafeFileControlToken=true in user settings and pass --token-file."
                )

    def drain_pending_approvals(self) -> list[str]:
        """Approves any pending execution requests for the current session via the control plane and records their digests."""
        if not self.auto_approve or not self.session_id or not self.control_token:
            return []
        status, text = self.request("GET", f"/api/gateway/session/{self.session_id}/approvals")
        if status != 200:
            return []
        payload = json.loads(text)
        self.self_approval_protection = bool(payload.get("selfApprovalProtection", self.self_approval_protection))
        newly_approved: list[str] = []
        for item in payload.get("items") or []:
            approval_id = item.get("approvalId")
            digest = item.get("requestDigest")
            if not approval_id or not digest or approval_id in self._seen_approval_ids:
                continue
            post_status, post_text = self.request(
                "POST",
                f"/api/gateway/approvals/{approval_id}",
                {"requestDigest": digest, "decision": "allow_once", "note": "devsim --auto-approve"},
                use_control=True,
            )
            if post_status == 200:
                self._seen_approval_ids.add(approval_id)
                self.approved_digests.append(str(digest))
                newly_approved.append(str(digest))
            elif post_status not in (404, 409):
                raise GatewayError(f"Auto-approving {approval_id} failed: {post_status} {post_text[:300]}")
        return newly_approved

    def open_session(self, name: str) -> str:
        status, text = self.request("POST", "/api/gateway/session?name=" + quote(name))
        if status not in (200, 201):
            raise GatewayError(f"Creating the session failed: {status} {text[:300]}")
        self.session_id = json.loads(text)["sessionId"]
        self.conversation_id = None
        self.last_event_id = 0
        return self.session_id

    # -- chat -----------------------------------------------------------------------------------------------------
    def say(self, message: str, *, repo: str | None = None, accept_debt: bool = False,
            approve_upgrades: Iterable[str] | None = None, timeout_minutes: float = 10,
            line_source: Callable[[str, int], Iterable[str]] | None = None) -> ChatResult:
        """Sends one message and follows the event stream until that message is answered or the time is up.

        ``accept_debt`` / ``approve_upgrades`` ride on a structured approve action, exactly like the approval screen;
        the message text never carries them.
        """
        if not self.session_id:
            raise GatewayError("open_session first")
        body: dict[str, Any] = {
            "message": message, "sessionId": self.session_id, "conversationId": self.conversation_id,
            "conversationMode": "continue-current" if self.conversation_id else "reuse-master",
        }
        if repo:
            body["gitRepoUrl"] = repo
        upgrades = list(approve_upgrades or [])
        needs_control = False
        if accept_debt or upgrades:
            action: dict[str, Any] = {"action": "approve", "acceptExistingDependencyDebt": bool(accept_debt)}
            if upgrades:
                action["approveDependencyUpgrades"] = upgrades
            body["structuredAction"] = action
            needs_control = True

        status, text = self.request("POST", "/api/gateway/chat", body, use_control=needs_control)
        if status != 202:
            raise GatewayError(f"Sending the message failed: {status} {text[:400]}")
        try:
            message_id = json.loads(text).get("messageId")
        except ValueError:
            message_id = None

        accumulator = SseAccumulator(message_id)
        accumulator.result.last_event_id = self.last_event_id
        deadline = time.time() + timeout_minutes * 60
        source = line_source or self._sse_lines
        processed_approvals = 0
        while not accumulator.result.done and time.time() < deadline:
            if self.auto_approve:
                self.drain_pending_approvals()
            try:
                for line in source(self.session_id, accumulator.result.last_event_id):
                    done_now = accumulator.feed(line)
                    if self.auto_approve and (
                        len(accumulator.result.approval_requests) > processed_approvals
                        or accumulator.result.needs_approval_reconcile
                    ):
                        processed_approvals = len(accumulator.result.approval_requests)
                        accumulator.result.needs_approval_reconcile = False
                        self.drain_pending_approvals()
                    if done_now:
                        break
            except (OSError, http.client.HTTPException):
                time.sleep(2)  # the stream dropped; reconnect from the last event id
                if self.auto_approve:
                    self.drain_pending_approvals()
        self.last_event_id = accumulator.result.last_event_id
        if accumulator.result.conversation_id:
            self.conversation_id = accumulator.result.conversation_id
        return accumulator.result

    def _sse_lines(self, session_id: str, last_event_id: int) -> Iterable[str]:
        connection = self._connection(120)
        try:
            headers = self._headers({"Accept": "text/event-stream"})
            if last_event_id:
                headers["Last-Event-ID"] = str(last_event_id)
            connection.request("GET", f"/api/gateway/sse?sessionId={session_id}&lastEventId={last_event_id}", headers=headers)
            response = connection.getresponse()
            while True:
                raw = response.readline()
                if not raw:
                    return
                yield raw.decode("utf-8", "replace").rstrip("\r\n")
        finally:
            connection.close()

    # -- reads ----------------------------------------------------------------------------------------------------
    def jobs(self) -> list[dict[str, Any]]:
        return self.get_json(f"/api/jobs/session/{self.session_id}")

    def tasklist(self) -> list[dict[str, Any]]:
        return self.get_json(f"/api/gateway/session/{self.session_id}/tasklist").get("items", [])

    def job_failure(self, job_id: str) -> dict[str, Any]:
        """The payload of the job's terminal failure event (detail code, message, result text)."""
        history = self.get_json(f"/api/jobs/{job_id}/history")
        events = history if isinstance(history, list) else history.get("events", [])
        for event in reversed(events):
            if event.get("eventType") in ("JobFailed", "JobRecoveryNeeded"):
                try:
                    return json.loads(event.get("payloadJson") or "{}")
                except ValueError:
                    return {}
        return {}

    def job_retries(self, job_id: str) -> list[dict[str, Any]]:
        """The retries the host queued for the job (reason, detail code of the attempt that ended, attempt number)."""
        history = self.get_json(f"/api/jobs/{job_id}/history")
        events = history if isinstance(history, list) else history.get("events", [])
        retries = []
        for event in events:
            if event.get("eventType") == "JobRetried":
                try:
                    retries.append(json.loads(event.get("payloadJson") or "{}"))
                except ValueError:
                    retries.append({})
        return retries

    def wait_for_jobs(self, timeout_minutes: float = 40, poll_seconds: float = 5, quiet_polls: int = 16,
                      sleep: Callable[[float], None] = time.sleep, clock: Callable[[], float] = time.time) -> list[dict[str, Any]]:
        """Waits until the plan has nothing left to run.

        Returns at once when no job is active and every task of the plan succeeded: nothing can start after that. In every other
        case (a task still pending, a failed task that may be retried, no plan at all) it waits until no job has been active for
        ``quiet_polls`` consecutive polls, because the next job is dispatched between polls.
        """
        deadline = clock() + timeout_minutes * 60
        quiet = 0
        jobs: list[dict[str, Any]] = []
        while clock() < deadline:
            if self.auto_approve:
                self.drain_pending_approvals()
            jobs = self.jobs()
            quiet = 0 if any(job.get("status") in ACTIVE_JOB_STATES for job in jobs) else quiet + 1
            if quiet >= quiet_polls or (quiet and jobs and self._plan_succeeded()):
                break
            sleep(poll_seconds)
        return jobs

    def _plan_succeeded(self) -> bool:
        """True when the session has a plan and every task in it ended successfully (best effort: unreadable means unknown)."""
        try:
            tasks = self.tasklist()
        except Exception:  # noqa: BLE001 - the quiet window still ends the wait
            return False
        return bool(tasks) and all(str(task.get("status") or "").lower() in SUCCEEDED_TASK_STATES for task in tasks)

    def verify(self, job_id: str, timeout_minutes: float = 20) -> dict[str, Any]:
        status, text = self.request("POST", "/api/gateway/artifacts/verify",
                                    {"sessionId": self.session_id, "jobId": job_id}, timeout=timeout_minutes * 60)
        if status != 200:
            raise GatewayError(f"Verification call failed: {status} {text[:300]}")
        return json.loads(text)
