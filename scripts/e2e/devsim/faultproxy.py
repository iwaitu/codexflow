# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""A pass-through proxy between the host and its model that makes every FIRST forge attempt fall short, on purpose.

Why it exists: the quality retry (a forge attempt whose result the host judges unmet, queued once more with feedback) is hit by chance in about one
live run in ten. A scenario that has to prove the retry works cannot wait for chance, and the host must not get a test-only branch. So the fault
is injected where a real model's mistake would arrive: in the model's answer.

Everything is forwarded to the real model untouched, except for the first attempt of each forge task. In it:
  1. the first write that changes a package version is rewritten so the package KEEPS the version the project has (it restores offline and is
     below the host's target, so the host's contract finds it unmet) and carries a harmless ``Label="fault"`` so that the write still changes the
     file (a no-op would be retried by the host as no_persisted_changes, a different path);
  2. once a write has succeeded in the host, the next answer is replaced by ``report_task_status(completed)`` and then a final text, so the
     attempt ends there with the package left below its target.
Every first attempt, because the host checks the contract once, on the last code task, over all packages: a package left alone by an early task
can be put right by a later one, and then there is nothing to retry. The retry prompt carries ``[上一次尝试`` and is forwarded as it is, so the
real model repairs the change from the host's feedback. What this found is in docs/personal-edition-plan.md (the retry scope defect).

Run: ``python faultproxy.py --upstream http://your-model-host:8001 --port 8099 [--capture DIR] [--mode passthrough|lowerversion]`` and point the host's
``VllmAgent__ApiUrl`` at ``http://127.0.0.1:8099/v1/{1}``. The proxy never reads or stores credentials: headers are forwarded as they come.
"""
from __future__ import annotations

import argparse
import http.client
import json
import re
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit

FORGE_MARKER = "Ivilson-Forge"
RETRY_MARKER = "[上一次尝试"
WRITE_TOOLS = frozenset({"apply_patch", "hs_write", "ivilson_smart_patch", "write_file", "edit_file"})
REPORT_TOOL = "report_task_status"
# Include="Package" Version="1.2.3" (the quotes may arrive escaped once or twice: a JSON string inside a JSON string)
_PACKAGE_REFERENCE = re.compile(r'(Include\s*=\s*(?:\\+)?")([^"\\]+)((?:\\+)?"\s+Version\s*=\s*(?:\\+)?")([^"\\]*)((?:\\+)?")')
KEPT_MARK = "fault"
_OLD_SIDE_KEYS = ("old", "search", "before")


# --------------------------------------------------------------------------------------------------------------------------------------
# Reading the request
# --------------------------------------------------------------------------------------------------------------------------------------
def message_text(message: dict[str, Any]) -> str:
    content = message.get("content")
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "".join(part.get("text", "") for part in content if isinstance(part, dict))
    return ""


def is_forge_request(body: dict[str, Any]) -> bool:
    """A forge worker's request: its system prompt names the role."""
    for message in body.get("messages") or []:
        if message.get("role") == "system":
            return FORGE_MARKER in message_text(message)[:400]
    return False


def is_retry_attempt(body: dict[str, Any]) -> bool:
    """The host appends ``[上一次尝试…]`` to the task prompt of a quality retry."""
    return any(message.get("role") == "user" and RETRY_MARKER in message_text(message) for message in body.get("messages") or [])


def write_calls(message: dict[str, Any]) -> list[dict[str, Any]]:
    return [call for call in message.get("tool_calls") or [] if (call.get("function") or {}).get("name") in WRITE_TOOLS]


# --------------------------------------------------------------------------------------------------------------------------------------
# Rewriting the answer
# --------------------------------------------------------------------------------------------------------------------------------------
def known_versions(body: dict[str, Any]) -> dict[str, str]:
    """package -> the version the worker was last shown for it (file reads, earlier results): what the project has now."""
    seen: dict[str, str] = {}
    for message in body.get("messages") or []:
        if message.get("role") == "tool":
            for match in _PACKAGE_REFERENCE.finditer(message_text(message)):
                seen[match.group(2)] = match.group(4)
    return seen


def lower_versions_in(text: str, known: dict[str, str]) -> tuple[str, int]:
    """Puts every ``PackageReference`` that this text CHANGES back to the version the worker was shown for it, i.e. the package is not upgraded.
    That version restores offline (a made-up one would fail the host's restore first, with a failure nobody retries) and is below the host's
    target, so the host's contract finds it unmet. A package the worker was never shown has no version to put back and is left alone. In a diff
    only ``+`` lines are touched, so the context and the removed side stay as they are and the patch still applies."""
    count = 0

    def replace(match: re.Match[str]) -> str:
        nonlocal count
        shown = known.get(match.group(2))
        if shown is None or shown == match.group(4):
            return match.group(0)
        count += 1
        # A metadata attribute rides along so that the write still changes the file: a package kept at the version it has would otherwise be a
        # no-op, the host would end the attempt as "no_persisted_changes" and retry it for that, not for the package left below its target.
        quote = match.group(5)
        return f"{match.group(1)}{match.group(2)}{match.group(3)}{shown}{quote} Label={quote}{KEPT_MARK}{quote}"

    def lower(line: str) -> str:
        return _PACKAGE_REFERENCE.sub(replace, line)

    if "\n" not in text:
        return lower(text), count
    lines = text.split("\n")
    is_diff = any(line.startswith("@@") or line.startswith("+++ ") or line.startswith("--- ") for line in lines)
    if not is_diff:
        return "\n".join(lower(line) for line in lines), count  # replacement text or a whole file: every line is new, indentation included
    for index, line in enumerate(lines):
        if line.startswith("+") and not line.startswith("+++"):
            lines[index] = lower(line)
    return "\n".join(lines), count


def _lower_in_value(value: Any, known: dict[str, str], key: str = "") -> tuple[Any, int]:
    if isinstance(value, str):
        if key.lower().startswith(_OLD_SIDE_KEYS):
            return value, 0
        return lower_versions_in(value, known)
    if isinstance(value, list):
        total = 0
        items = []
        for item in value:
            new, n = _lower_in_value(item, known, key)
            items.append(new)
            total += n
        return items, total
    if isinstance(value, dict):
        total = 0
        result = {}
        for name, item in value.items():
            new, n = _lower_in_value(item, known, name)
            result[name] = new
            total += n
        return result, total
    return value, 0


def lower_write_call(call: dict[str, Any], known: dict[str, str]) -> int:
    """Rewrites the arguments of one write call in place; returns how many version attributes were lowered."""
    function = call.get("function") or {}
    raw = function.get("arguments")
    if not isinstance(raw, str):
        return 0
    try:
        arguments = json.loads(raw)
    except ValueError:
        return 0
    lowered, count = _lower_in_value(arguments, known)
    if count:
        function["arguments"] = json.dumps(lowered, ensure_ascii=False)
    return count


def lower_first_versioned_write(message: dict[str, Any], known: dict[str, str]) -> tuple[int, str | None]:
    """Lowers the versions of the first write call in the answer that changes one; returns (how many, that call's id)."""
    for call in write_calls(message):
        count = lower_write_call(call, known)
        if count:
            return count, call.get("id")
    return 0, None


def has_reported(body: dict[str, Any]) -> bool:
    return any((call.get("function") or {}).get("name") == REPORT_TOOL
               for message in body.get("messages") or [] if message.get("role") == "assistant" for call in message.get("tool_calls") or [])


def report_answer(model: str) -> dict[str, Any]:
    """The answer of a worker that considers itself done: it reports the task completed. (A bare text answer would make the host retry for a
    different reason, a worker that never reported, and the retry under test is the one for a package left below the host's version.)"""
    call = {"id": f"fault-report-{int(time.time() * 1000)}", "type": "function",
            "function": {"name": REPORT_TOOL, "arguments": json.dumps({"parameters": {"status": "completed"}})}}
    return {"id": f"fault-{int(time.time() * 1000)}", "object": "chat.completion", "created": int(time.time()), "model": model,
            "choices": [{"index": 0, "finish_reason": "tool_calls", "message": {"role": "assistant", "content": None, "tool_calls": [call]}}],
            "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0}}


def final_answer(model: str) -> dict[str, Any]:
    """A completion that ends the attempt: text only, no tool calls."""
    return {"id": f"fault-{int(time.time() * 1000)}", "object": "chat.completion", "created": int(time.time()), "model": model,
            "choices": [{"index": 0, "finish_reason": "stop",
                         "message": {"role": "assistant", "content": "依赖版本已修改，本任务到此完成。"}}],
            "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0}}


def as_event_stream(completion: dict[str, Any]) -> bytes:
    """The same completion as a chat-completions event stream: one delta carrying the whole message, a finish chunk, ``[DONE]``."""
    choice = completion["choices"][0]
    message = choice["message"]
    delta: dict[str, Any] = {"role": "assistant"}
    if message.get("content"):
        delta["content"] = message["content"]
    if message.get("tool_calls"):
        delta["tool_calls"] = [{"index": i, **call} for i, call in enumerate(message["tool_calls"])]
    base = {"id": completion.get("id"), "object": "chat.completion.chunk", "created": completion.get("created"), "model": completion.get("model")}
    chunks = [{**base, "choices": [{"index": 0, "delta": delta, "finish_reason": None}]},
              {**base, "choices": [{"index": 0, "delta": {}, "finish_reason": choice.get("finish_reason") or "stop"}]}]
    if completion.get("usage"):
        chunks.append({**base, "choices": [], "usage": completion["usage"]})
    return ("".join(f"data: {json.dumps(chunk, ensure_ascii=False)}\n\n" for chunk in chunks) + "data: [DONE]\n\n").encode("utf-8")


def completion_from_event_stream(raw: bytes) -> dict[str, Any]:
    """Folds a chat-completions event stream back into one completion (content and tool calls; reasoning is dropped)."""
    content: list[str] = []
    calls: dict[int, dict[str, Any]] = {}
    finish = None
    model = ""
    completion_id = ""
    for line in raw.decode("utf-8", errors="replace").splitlines():
        if not line.startswith("data:"):
            continue
        data = line[5:].strip()
        if not data or data == "[DONE]":
            continue
        try:
            chunk = json.loads(data)
        except ValueError:
            continue
        model = chunk.get("model") or model
        completion_id = chunk.get("id") or completion_id
        for choice in chunk.get("choices") or []:
            delta = choice.get("delta") or {}
            if delta.get("content"):
                content.append(delta["content"])
            for part in delta.get("tool_calls") or []:
                slot = calls.setdefault(part.get("index", 0), {"id": "", "type": "function", "function": {"name": "", "arguments": ""}})
                slot["id"] = part.get("id") or slot["id"]
                function = part.get("function") or {}
                slot["function"]["name"] += function.get("name") or ""
                slot["function"]["arguments"] += function.get("arguments") or ""
            finish = choice.get("finish_reason") or finish
    message: dict[str, Any] = {"role": "assistant", "content": "".join(content) or None}
    if calls:
        message["tool_calls"] = [calls[i] for i in sorted(calls)]
    return {"id": completion_id, "object": "chat.completion", "created": int(time.time()), "model": model,
            "choices": [{"index": 0, "message": message, "finish_reason": finish or "stop"}]}


# --------------------------------------------------------------------------------------------------------------------------------------
# The decision
# --------------------------------------------------------------------------------------------------------------------------------------
class Fault:
    """Makes every first forge attempt fall short and remembers that it did (so a run can be checked afterwards).

    In each first attempt the answer that first carries a write that CHANGES a package version is changed so that the package keeps the version
    the project has. The attempt is cut once a write has succeeded in the host from that call on. A refused write (a malformed hashline request,
    the model's own mistake) does not count: the model is asked again, and what it sends next is changed the same way. The model sees its own
    changed call in the conversation and may copy it or switch tools, so "the write that landed" is not tied to the id of the call that was changed.

    Every first attempt, not one of them: the host's contract is checked once, on the last code task, over all packages; a package left alone by
    an early task can be put right by a later one, and then there is nothing to retry. When no task can put a package right, whichever task is the
    last code task finds it unmet."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._attempts: dict[str, dict[str, Any]] = {}  # one entry per first-attempt conversation that was changed
        self.log: list[str] = []

    @property
    def injected(self) -> bool:
        return bool(self._attempts)

    @property
    def landed(self) -> bool:
        return any(state["landed"] for state in self._attempts.values())

    @staticmethod
    def _first_attempt(body: dict[str, Any]) -> bool:
        return is_forge_request(body) and not is_retry_attempt(body)

    def wants(self, body: dict[str, Any]) -> bool:
        """True when this request's answer may still need rewriting: a first forge attempt that is not cut yet."""
        with self._lock:
            state = self._attempts.get(self._tag(body))
            return self._first_attempt(body) and not (state and state["landed"])

    def _has_landed(self, body: dict[str, Any], lowered: set[str]) -> bool:
        """True when a write at or after the first changed call has succeeded in the host."""
        messages = body.get("messages") or []
        results = {m.get("tool_call_id"): message_text(m) for m in messages if m.get("role") == "tool"}
        started = False
        for message in messages:
            if message.get("role") != "assistant":
                continue
            calls = message.get("tool_calls") or []
            started = started or any(call.get("id") in lowered for call in calls)
            if started and any(call["id"] in results and not results[call["id"]].lstrip().startswith("[tool failed") for call in write_calls(message)):
                return True
        return False

    @staticmethod
    def _tag(body: dict[str, Any]) -> str:  # noqa: D401
        """The first user message identifies one attempt's conversation (it is the same in every request of that attempt)."""
        for message in body.get("messages") or []:
            if message.get("role") == "user":
                return message_text(message)[:600]
        return ""

    def before(self, body: dict[str, Any]) -> dict[str, Any] | None:
        """A completion to return INSTEAD of asking the model, or None to ask it. Called for every request."""
        if not self._first_attempt(body):
            return None
        with self._lock:
            state = self._attempts.get(self._tag(body))
            if state is None or not self._has_landed(body, state["lowered"]):
                return None
            model = str(body.get("model") or "")
            if has_reported(body):
                self.log.append("attempt ended after its report")
                return final_answer(model)
            state["landed"] = True
            self.log.append("attempt cut after the changed write landed: reports completed")
            return report_answer(model)

    def after(self, body: dict[str, Any], completion: dict[str, Any]) -> dict[str, Any]:
        """The model's answer, possibly rewritten. Called for every answer."""
        if not self._first_attempt(body):
            return completion
        with self._lock:
            tag = self._tag(body)
            state = self._attempts.get(tag)
            if state is not None and state["landed"]:
                return completion
            known = known_versions(body)
            for choice in completion.get("choices") or []:
                count, call_id = lower_first_versioned_write(choice.get("message") or {}, known)
                if count and call_id:
                    state = self._attempts.setdefault(tag, {"lowered": set(), "landed": False})
                    state["lowered"].add(call_id)
                    self.log.append(f"kept {count} package(s) at the version the project has in a write")
                    break
        return completion


# --------------------------------------------------------------------------------------------------------------------------------------
# The server
# --------------------------------------------------------------------------------------------------------------------------------------
def make_handler(upstream: str, mode: str, capture: Path | None, fault: Fault):
    target = urlsplit(upstream)
    counter = {"n": 0}
    counter_lock = threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.0"  # the body ends when the connection closes: no length to know in advance, streams pass through as they are

        def log_message(self, fmt: str, *args: Any) -> None:  # noqa: D401 - quiet
            return

        def _forward(self, payload: bytes) -> http.client.HTTPResponse:
            connection_class = http.client.HTTPSConnection if target.scheme == "https" else http.client.HTTPConnection
            connection = connection_class(target.netloc, timeout=1800)
            headers = {k: v for k, v in self.headers.items() if k.lower() not in ("host", "content-length", "connection", "accept-encoding")}
            headers["Content-Length"] = str(len(payload))
            connection.request(self.command, target.path.rstrip("/") + self.path, body=payload, headers=headers)
            return connection.getresponse()

        def _record(self, n: int, request: bytes, response: bytes, note: str) -> None:
            if capture is None:
                return
            capture.mkdir(parents=True, exist_ok=True)
            (capture / f"{n:05d}.request.json").write_bytes(request)
            (capture / f"{n:05d}.response.txt").write_bytes(response)
            if note:
                (capture / f"{n:05d}.note.txt").write_text(note, encoding="utf-8")

        def do_POST(self) -> None:  # noqa: N802
            length = int(self.headers.get("Content-Length") or 0)
            payload = self.rfile.read(length)
            with counter_lock:
                counter["n"] += 1
                n = counter["n"]
            try:
                body = json.loads(payload)
            except ValueError:
                body = None
            wants_stream = bool(body and body.get("stream"))

            if mode == "lowerversion" and body is not None and self.path.endswith("/chat/completions"):
                replacement = fault.before(body)
                if replacement is not None:
                    data = as_event_stream(replacement) if wants_stream else json.dumps(replacement, ensure_ascii=False).encode("utf-8")
                    self._reply(200, "text/event-stream" if wants_stream else "application/json", data)
                    self._record(n, payload, data, "answered by the proxy")
                    return
                if fault.wants(body):
                    # The answer is read whole so that its write call can be rewritten, then given back in the shape the host asked for.
                    ask = dict(body, stream=False)
                    ask.pop("stream_options", None)
                    upstream_response = self._forward(json.dumps(ask, ensure_ascii=False).encode("utf-8"))
                    raw = upstream_response.read()
                    if upstream_response.status == 200:
                        completion = fault.after(body, json.loads(raw))
                        data = as_event_stream(completion) if wants_stream else json.dumps(completion, ensure_ascii=False).encode("utf-8")
                        self._reply(200, "text/event-stream" if wants_stream else "application/json", data)
                        self._record(n, payload, data, "; ".join(fault.log))
                    else:
                        self._reply(upstream_response.status, upstream_response.getheader("Content-Type") or "application/json", raw)
                        self._record(n, payload, raw, f"upstream status {upstream_response.status}")
                    return

            upstream_response = self._forward(payload)
            self.send_response(upstream_response.status)
            self.send_header("Content-Type", upstream_response.getheader("Content-Type") or "application/json")
            self.end_headers()
            collected = bytearray()
            while True:
                chunk = upstream_response.read1(8192) if hasattr(upstream_response, "read1") else upstream_response.read(8192)
                if not chunk:
                    break
                collected.extend(chunk)
                try:
                    self.wfile.write(chunk)
                    self.wfile.flush()
                except (BrokenPipeError, ConnectionResetError):
                    break
            self._record(n, payload, bytes(collected), "")

        def do_GET(self) -> None:  # noqa: N802
            if self.path == "/_fault":
                data = json.dumps({"injected": fault.injected, "log": fault.log}).encode("utf-8")
                self._reply(200, "application/json", data)
                return
            upstream_response = self._forward(b"")
            self._reply(upstream_response.status, upstream_response.getheader("Content-Type") or "application/json", upstream_response.read())

        def _reply(self, status: int, content_type: str, data: bytes) -> None:
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.end_headers()
            self.wfile.write(data)

    return Handler


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--upstream", required=True, help="the real model's base URL, e.g. http://your-model-host:8001")
    parser.add_argument("--port", type=int, default=8099)
    parser.add_argument("--mode", choices=["passthrough", "lowerversion"], default="lowerversion")
    parser.add_argument("--capture", type=Path, default=None, help="write every request and response here (they contain prompts; keep it out of git)")
    args = parser.parse_args(argv)
    fault = Fault()
    server = ThreadingHTTPServer(("127.0.0.1", args.port), make_handler(args.upstream, args.mode, args.capture, fault))
    print(f"fault proxy on 127.0.0.1:{args.port} -> {args.upstream} (mode {args.mode})", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
