# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

import json
import sys
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.request import Request, urlopen

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import faultproxy  # noqa: E402

FORGE_SYSTEM = {"role": "system", "content": "你被定义为 Ivilson-Forge，项目的高级工程师。"}
TASK = {"role": "user", "content": "任务 ID: TASK-006\n升级 OpenTelemetry"}
# what the worker read before writing: the versions the project has now (these restore offline, and are below the host's targets)
READ = {"role": "tool", "tool_call_id": "read", "content": json.dumps({"output": (
    '9| <ItemGroup>\n10| <PackageReference Include="Snappier" Version="1.0.0" />\n11| <PackageReference Include="MongoDB.Driver" Version="3.6.0" />')})}
BASE = [FORGE_SYSTEM, TASK, READ]
PATCH = ("--- a/src/A/A.csproj\n+++ b/src/A/A.csproj\n@@ -1,3 +1,3 @@\n <ItemGroup>\n"
         '-  <PackageReference Include="Snappier" Version="1.0.0" />\n'
         '+  <PackageReference Include="Snappier" Version="1.3.1" />\n </ItemGroup>')
KNOWN = {"Snappier": "1.0.0", "MongoDB.Driver": "3.6.0", "A": "1.0.0", "B": "3.0.0", "MessagePack": "2.5.187", "Aspire.Hosting.AppHost": "13.1.0"}
REFUSED = "[tool failed: codexflow_tool_business_failed] {\"status\": 2}"
LANDED = "{\"status\": 0}"


def write_call(patch=PATCH, name="apply_patch", call_id="c1"):
    return {"id": call_id, "type": "function", "function": {"name": name, "arguments": json.dumps({"patch": patch}, ensure_ascii=False)}}


def completion(message):
    return {"id": "x", "object": "chat.completion", "model": "m", "choices": [{"index": 0, "message": message, "finish_reason": "tool_calls"}]}


def answer_of(calls):
    return completion({"role": "assistant", "content": None, "tool_calls": calls})


def tool_result(call_id, content=LANDED):
    return {"role": "tool", "tool_call_id": call_id, "content": content}


def arguments_of(answer, index=0):
    return answer["choices"][0]["message"]["tool_calls"][index]["function"]["arguments"]


class Reading(unittest.TestCase):
    def test_only_the_forge_role_is_recognised(self):
        self.assertTrue(faultproxy.is_forge_request({"messages": [FORGE_SYSTEM, TASK]}))
        self.assertFalse(faultproxy.is_forge_request({"messages": [{"role": "system", "content": "你是网关助手"}, TASK]}))
        self.assertFalse(faultproxy.is_forge_request({"messages": [TASK]}))

    def test_a_retry_is_recognised_by_the_hosts_marker_in_the_task_prompt(self):
        retry = {"role": "user", "content": "任务\n\n[上一次尝试没有完成依赖升级]\n- Snappier"}
        self.assertTrue(faultproxy.is_retry_attempt({"messages": [FORGE_SYSTEM, retry]}))
        self.assertFalse(faultproxy.is_retry_attempt({"messages": [FORGE_SYSTEM, TASK]}))

    def test_the_versions_the_worker_was_shown_are_read_out_of_the_tool_results_even_when_their_quotes_are_escaped(self):
        escaped = {"role": "tool", "tool_call_id": "r", "content": '10| <PackageReference Include=\\"MongoDB.Driver\\" Version=\\"3.6.0\\" />'}
        self.assertEqual({"MongoDB.Driver": "3.6.0"}, faultproxy.known_versions({"messages": [escaped]}))
        self.assertEqual("1.0.0", faultproxy.known_versions({"messages": BASE})["Snappier"])


class LoweringVersions(unittest.TestCase):
    """The fault is a package left at the version the project already has: it restores offline (a made-up version would fail the host's restore
    first, with a failure nobody retries) and it is below the host's target, so the host's contract finds it unmet."""

    def test_only_the_added_side_of_a_diff_is_changed_so_the_patch_still_applies(self):
        text, count = faultproxy.lower_versions_in(PATCH, KNOWN)
        self.assertEqual(1, count)
        self.assertIn('-  <PackageReference Include="Snappier" Version="1.0.0" />', text)
        self.assertIn('+  <PackageReference Include="Snappier" Version="1.0.0" Label="fault" />', text)
        self.assertNotIn("1.3.1", text)

    def test_a_kept_package_still_changes_the_file_so_the_attempt_is_not_a_no_op_the_host_would_retry_for_another_reason(self):
        # run 10: the final task's only package was kept at its version, nothing changed on disk, and the host retried the attempt as
        # no_persisted_changes: the one allowed retry went to that, not to the unmet package the scenario is about
        text, count = faultproxy.lower_versions_in('<PackageReference Include="Snappier" Version="1.3.1" />', KNOWN)
        self.assertEqual(1, count)
        self.assertEqual('<PackageReference Include="Snappier" Version="1.0.0" Label="fault" />', text)
        escaped, _ = faultproxy.lower_versions_in('x \\"<PackageReference Include=\\"Snappier\\" Version=\\"1.3.1\\" />', KNOWN)
        self.assertIn('Version=\\"1.0.0\\" Label=\\"fault\\"', escaped)  # quotes escaped the way the surrounding text escapes them

    def test_an_indented_replacement_text_is_not_mistaken_for_a_diff(self):
        # hs_write {oldString, newString} whose text starts with indentation was read as diff context and left alone
        text, count = faultproxy.lower_versions_in(
            '    <PackageReference Include="Aspire.Hosting.AppHost" Version="13.6.5" />\n    <PackageReference Include="MessagePack" Version="2.5.303" />', KNOWN)
        self.assertEqual(2, count)
        self.assertIn('Include="Aspire.Hosting.AppHost" Version="13.1.0"', text)
        self.assertIn('Include="MessagePack" Version="2.5.187"', text)

    def test_a_whole_file_write_has_every_shown_package_put_back(self):
        text, count = faultproxy.lower_versions_in('<Project>\n<PackageReference Include="A" Version="2.0.0" />\n<PackageReference Include="B" Version="3.1.0" />\n</Project>', KNOWN)
        self.assertEqual(2, count)
        self.assertNotIn("2.0.0", text)
        self.assertNotIn("3.1.0", text)

    def test_a_package_the_worker_was_never_shown_is_left_alone_because_there_is_no_version_to_put_back(self):
        text, count = faultproxy.lower_versions_in('<PackageReference Include="Brand.New" Version="1.0.0" />', KNOWN)
        self.assertEqual(0, count)
        self.assertIn('Version="1.0.0"', text)

    def test_a_package_that_already_has_the_shown_version_is_not_a_change(self):
        text, count = faultproxy.lower_versions_in('<PackageReference Include="MongoDB.Driver" Version="3.6.0" />\n<PackageReference Include="Snappier" Version="1.3.1" />', KNOWN)
        self.assertEqual(1, count)
        self.assertIn('Include="MongoDB.Driver" Version="3.6.0"', text)
        self.assertIn('Include="Snappier" Version="1.0.0"', text)

    def test_a_write_without_a_version_is_left_alone(self):
        text, count = faultproxy.lower_versions_in("public class A {}", KNOWN)
        self.assertEqual(0, count)
        self.assertEqual("public class A {}", text)

    def test_the_old_side_of_an_edit_is_not_touched(self):
        old, new = '<PackageReference Include="A" Version="0.9.0" />', '<PackageReference Include="A" Version="1.3.1" />'
        call = {"function": {"name": "edit_file", "arguments": json.dumps({"path": "A.csproj", "old_string": old, "new_string": new})}}
        self.assertEqual(1, faultproxy.lower_write_call(call, KNOWN))
        arguments = json.loads(call["function"]["arguments"])
        self.assertEqual(old, arguments["old_string"])
        self.assertEqual('<PackageReference Include="A" Version="1.0.0" Label="fault" />', arguments["new_string"])

    def test_the_hashline_write_whose_operations_are_a_json_string_is_changed_too(self):
        operations = json.dumps([{"type": "replace_range", "newLines": ['    <PackageReference Include="Snappier" Version="1.3.1" />']}])
        call = {"id": "c1", "type": "function", "function": {"name": "hs_write", "arguments": json.dumps({"filePath": "A.csproj", "operations": operations})}}
        self.assertEqual(1, faultproxy.lower_write_call(call, KNOWN))
        self.assertIn('Version=\\"1.0.0\\"', json.loads(call["function"]["arguments"])["operations"])

    def test_the_first_write_that_changes_a_version_is_the_one_lowered(self):
        message = {"tool_calls": [write_call("public class A {}", call_id="c0"), write_call(PATCH, call_id="c1"), write_call(PATCH, call_id="c2")]}
        self.assertEqual((1, "c1"), faultproxy.lower_first_versioned_write(message, KNOWN))
        arguments = [json.loads(c["function"]["arguments"])["patch"] for c in message["tool_calls"]]
        self.assertEqual("public class A {}", arguments[0])
        self.assertNotIn("1.3.1", arguments[1])
        self.assertIn("1.3.1", arguments[2])


class TheFault(unittest.TestCase):
    def test_the_first_versioned_write_of_a_first_attempt_is_lowered_and_the_attempt_is_cut_once_it_landed(self):
        fault = faultproxy.Fault()
        first = {"messages": BASE}
        self.assertIsNone(fault.before(first))
        answer = answer_of([write_call()])
        fault.after(first, answer)
        self.assertTrue(fault.injected)
        self.assertNotIn("1.3.1", arguments_of(answer))

        next_request = {"messages": BASE + [answer["choices"][0]["message"], tool_result("c1")]}
        # the attempt ends the way a model that is done ends it: it reports the task completed (a bare text answer is a different failure,
        # a worker that never reported, and the host would retry that one for another reason)
        cut = fault.before(next_request)
        report = cut["choices"][0]["message"]["tool_calls"][0]
        self.assertEqual("report_task_status", report["function"]["name"])
        self.assertEqual({"parameters": {"status": "completed"}}, json.loads(report["function"]["arguments"]))
        reported = {"messages": next_request["messages"] + [cut["choices"][0]["message"], tool_result(report["id"])]}
        self.assertNotIn("tool_calls", fault.before(reported)["choices"][0]["message"])

    def test_a_write_the_host_refused_does_not_end_the_attempt(self):
        fault = faultproxy.Fault()
        answer = answer_of([write_call(call_id="bad")])
        fault.after({"messages": BASE}, answer)
        refused = {"messages": BASE + [answer["choices"][0]["message"], tool_result("bad", REFUSED)]}
        self.assertIsNone(fault.before(refused))  # the model is asked again: nothing has landed yet

    def test_a_later_write_that_lands_after_a_refused_one_ends_the_attempt_even_when_the_model_copied_the_changed_call(self):
        # the model sees its own (rewritten) refused call in the conversation and sends the same arguments again: nothing is left to rewrite in
        # it, yet it is the write that carries the fault
        fault = faultproxy.Fault()
        answer = answer_of([write_call(call_id="bad")])
        fault.after({"messages": BASE}, answer)
        refused = {"messages": BASE + [answer["choices"][0]["message"], tool_result("bad", REFUSED)]}
        copied = answer_of([write_call(patch=PATCH.replace("1.3.1", "1.0.0"), call_id="copy")])
        fault.after(refused, copied)
        landed = {"messages": refused["messages"] + [copied["choices"][0]["message"], tool_result("copy")]}
        self.assertEqual("report_task_status", fault.before(landed)["choices"][0]["message"]["tool_calls"][0]["function"]["name"])

    def test_a_write_of_a_different_shape_that_lands_after_the_fault_was_claimed_ends_the_attempt(self):
        # run 6: the model switched tools between tries (apply_patch refused, then edit_file): whatever lands after the claim ends the attempt
        fault = faultproxy.Fault()
        answer = answer_of([write_call(call_id="bad")])
        fault.after({"messages": BASE}, answer)
        refused = {"messages": BASE + [answer["choices"][0]["message"], tool_result("bad", REFUSED)]}
        other = answer_of([{"id": "e1", "type": "function", "function": {"name": "edit_file", "arguments": json.dumps({"path": "A.csproj", "new_string": "x"})}}])
        fault.after(refused, other)
        landed = {"messages": refused["messages"] + [other["choices"][0]["message"], tool_result("e1")]}
        self.assertIsNotNone(fault.before(landed))

    def test_a_write_that_landed_before_the_fault_does_not_end_the_attempt(self):
        fault = faultproxy.Fault()
        earlier = answer_of([write_call("public class A {}", call_id="early")])
        fault.after({"messages": BASE}, earlier)
        self.assertFalse(fault.injected)
        history = BASE + [earlier["choices"][0]["message"], tool_result("early")]
        answer = answer_of([write_call(call_id="bad")])
        fault.after({"messages": history}, answer)
        refused = {"messages": history + [answer["choices"][0]["message"], tool_result("bad", REFUSED)]}
        self.assertIsNone(fault.before(refused))

    def test_the_retry_is_never_touched(self):
        fault = faultproxy.Fault()
        retry = {"messages": [FORGE_SYSTEM, {"role": "user", "content": TASK["content"] + "\n\n[上一次尝试没有完成依赖升级]"}, READ]}
        answer = answer_of([write_call()])
        fault.after(retry, answer)
        self.assertFalse(fault.injected)
        self.assertIn("1.3.1", arguments_of(answer))
        self.assertIsNone(fault.before({"messages": retry["messages"] + [answer["choices"][0]["message"], tool_result("c1")]}))

    def test_every_first_attempt_falls_short_on_its_own_so_that_no_later_task_can_put_the_package_right(self):
        # run 7: only the first attempt of the run was faulted, a later task fixed that package and the host had nothing to retry
        fault = faultproxy.Fault()
        first = answer_of([write_call()])
        fault.after({"messages": BASE}, first)
        first_landed = {"messages": BASE + [first["choices"][0]["message"], tool_result("c1")]}
        self.assertIsNotNone(fault.before(first_landed))

        other_base = [FORGE_SYSTEM, {"role": "user", "content": "任务 ID: TASK-007\n别的任务"}, READ]
        again = answer_of([write_call(call_id="o1")])
        fault.after({"messages": other_base}, again)
        self.assertNotIn("1.3.1", arguments_of(again))
        landed = {"messages": other_base + [again["choices"][0]["message"], tool_result("o1")]}
        self.assertEqual("report_task_status", fault.before(landed)["choices"][0]["message"]["tool_calls"][0]["function"]["name"])
        # and the first attempt's own conversation is not confused with the second one
        self.assertIsNone(fault.before({"messages": other_base}))

    def test_an_attempt_that_was_cut_is_not_rewritten_again(self):
        fault = faultproxy.Fault()
        first = answer_of([write_call()])
        fault.after({"messages": BASE}, first)
        landed = {"messages": BASE + [first["choices"][0]["message"], tool_result("c1")]}
        fault.before(landed)
        later = answer_of([write_call(call_id="late")])
        fault.after(landed, later)
        self.assertIn("1.3.1", arguments_of(later))

    def test_other_workers_are_never_touched(self):
        fault = faultproxy.Fault()
        gateway = {"messages": [{"role": "system", "content": "你是网关助手"}, TASK, READ]}
        answer = answer_of([write_call()])
        fault.after(gateway, answer)
        self.assertFalse(fault.injected)

    def test_a_forge_answer_without_a_versioned_write_does_not_use_up_the_fault(self):
        fault = faultproxy.Fault()
        fault.after({"messages": BASE}, answer_of([write_call("public class A {}")]))
        self.assertFalse(fault.injected)


class EventStreams(unittest.TestCase):
    def test_a_completion_survives_the_round_trip_through_an_event_stream(self):
        original = answer_of([write_call()])
        folded = faultproxy.completion_from_event_stream(faultproxy.as_event_stream(original))
        calls = folded["choices"][0]["message"]["tool_calls"]
        self.assertEqual("apply_patch", calls[0]["function"]["name"])
        self.assertEqual(arguments_of(original), calls[0]["function"]["arguments"])
        self.assertEqual("c1", calls[0]["id"])

    def test_deltas_of_one_tool_call_are_joined(self):
        raw = ('data: {"id":"1","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c1","type":"function","function":{"name":"apply_","arguments":"{\\"pat"}}]}}]}\n\n'
               'data: {"id":"1","model":"m","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"patch","arguments":"ch\\":\\"x\\"}"}}]}}]}\n\n'
               'data: {"id":"1","model":"m","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}\n\ndata: [DONE]\n\n').encode()
        call = faultproxy.completion_from_event_stream(raw)["choices"][0]["message"]["tool_calls"][0]
        self.assertEqual("apply_patch", call["function"]["name"])
        self.assertEqual({"patch": "x"}, json.loads(call["function"]["arguments"]))


class Serving(unittest.TestCase):
    """The proxy in front of a fake model: what reaches the host, and what reaches the model."""

    def setUp(self):
        self.seen = []
        seen = self.seen
        scripted = self.scripted = []

        class Upstream(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                seen.append({"body": body, "auth": self.headers.get("Authorization")})
                data = json.dumps(scripted.pop(0)).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

        self.upstream = ThreadingHTTPServer(("127.0.0.1", 0), Upstream)
        threading.Thread(target=self.upstream.serve_forever, daemon=True).start()
        self.fault = faultproxy.Fault()
        handler = faultproxy.make_handler(f"http://127.0.0.1:{self.upstream.server_port}", "lowerversion", None, self.fault)
        self.proxy = ThreadingHTTPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=self.proxy.serve_forever, daemon=True).start()

    def tearDown(self):
        self.proxy.shutdown()
        self.proxy.server_close()
        self.upstream.shutdown()
        self.upstream.server_close()

    def post(self, body):
        request = Request(f"http://127.0.0.1:{self.proxy.server_port}/v1/chat/completions", json.dumps(body).encode(),
                          {"Content-Type": "application/json", "Authorization": "Bearer secret-for-the-model"})
        with urlopen(request, timeout=10) as response:
            return json.loads(response.read())

    def test_the_host_sees_the_lowered_write_the_credentials_reach_the_model_and_the_cut_costs_no_model_call(self):
        self.scripted.append(answer_of([write_call()]))
        first = self.post({"model": "m", "messages": BASE})
        self.assertNotIn("1.3.1", arguments_of(first))
        self.assertEqual("Bearer secret-for-the-model", self.seen[0]["auth"])

        second = self.post({"model": "m", "messages": BASE + [first["choices"][0]["message"], tool_result("c1")]})
        self.assertEqual("report_task_status", second["choices"][0]["message"]["tool_calls"][0]["function"]["name"])
        self.assertEqual(1, len(self.seen))  # the cut answer never reached the model

        retry = {"model": "m", "messages": [FORGE_SYSTEM, {"role": "user", "content": TASK["content"] + "\n\n[上一次尝试没有完成依赖升级]"}]}
        self.scripted.append(completion({"role": "assistant", "content": "ok"}))
        self.post(retry)
        self.assertEqual(2, len(self.seen))  # the retry went to the model as it was
        self.assertEqual(retry, self.seen[1]["body"])


if __name__ == "__main__":
    unittest.main()
