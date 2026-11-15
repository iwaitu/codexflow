# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Print what the model saw and said in one recorded QRE run (see Runtime:QueryRuntime:AuditDirectory).

The host records every run in private diagnostic mode under <AuditDirectory>/.qre/v2/private/runs/<run>/. `qre replay latest` summarises a
run; this reader shows the content in order, UTF-8 safe, so a failed worker can be read: the prompt, the reasoning, the text, the tool calls
and their results.

    python replay_run.py <AuditDirectory> --session <sessionId>            # list the runs of a session (from the host log lines)
    python replay_run.py <AuditDirectory> --run <runIdSuffixOrFullName>     # show one run
    python replay_run.py <AuditDirectory> --last [N]                        # show the newest N runs

Options: --max N (characters per text block, default 3000), --no-request (skip the request side).
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def _runs_root(directory: str) -> Path:
    return Path(directory) / ".qre" / "v2" / "private" / "runs"


def _read_blob(run_dir: Path, blob: dict) -> dict:
    return json.loads((run_dir / blob["path"]).read_text(encoding="utf-8"))


def _clip(text: str, limit: int) -> str:
    text = text.replace("\r\n", "\n")
    return text if len(text) <= limit else text[:limit] + f"\n  ... [{len(text) - limit} more characters]"


def _items(message: dict) -> list[dict]:
    return message.get("items") or message.get("content") or []


def _print_item(prefix: str, item: dict, limit: int) -> None:
    kind = item.get("kind") or item.get("type") or "?"
    text = item.get("text")
    if text is None:
        text = json.dumps({k: v for k, v in item.items() if k != "kind"}, ensure_ascii=False)
    print(f"  [{prefix} {kind}] {_clip(str(text), limit)}")


def show_run(run_dir: Path, limit: int, with_request: bool) -> None:
    manifest = json.loads((run_dir / "manifest.json").read_text(encoding="utf-8")) if (run_dir / "manifest.json").exists() else {}
    print(f"===== {run_dir.name}  status={manifest.get('status') or manifest.get('terminalStatus') or '?'}")
    for line in (run_dir / "audit.v1.jsonl").read_text(encoding="utf-8").splitlines():
        event = json.loads(line)
        kind = event.get("kind")
        payload = event.get("payload")
        if payload is None and event.get("payloadBlob"):
            payload = _read_blob(run_dir, event["payloadBlob"])
        payload = payload or {}
        print(f"-- #{event.get('sequence')} {kind}")
        if kind == "ModelRequestPrepared" and with_request:
            for message in (payload.get("request") or {}).get("messages", []):
                for item in _items(message):
                    _print_item(f"request {message.get('role')}", item, limit)
        elif kind == "ModelResponseCommitted":
            output = payload.get("output") or payload
            for item in output.get("items") or output.get("content") or []:
                _print_item("model", item, limit)
            if output.get("stopReason") or output.get("finishReason"):
                print(f"  stop: {output.get('stopReason') or output.get('finishReason')}")
        elif kind and "Tool" in kind:
            call = payload.get("call") or {}
            result = payload.get("result") or {}
            if call:
                print(f"  call: {call.get('name') or call.get('toolName')} {_clip(json.dumps(call.get('arguments') or call.get('input') or {}, ensure_ascii=False), 600)}")
            if result:
                print(f"  result: {_clip(json.dumps(result, ensure_ascii=False), limit)}")
        elif kind == "TurnTerminal":
            print(f"  {_clip(json.dumps({k: v for k, v in payload.items() if k != 'finalText'}, ensure_ascii=False), 800)}")
            if payload.get("finalText"):
                print(f"  final: {_clip(payload['finalText'], limit)}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("directory")
    parser.add_argument("--run")
    parser.add_argument("--last", nargs="?", const=1, type=int)
    parser.add_argument("--max", type=int, default=3000)
    parser.add_argument("--no-request", action="store_true")
    args = parser.parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    root = _runs_root(args.directory)
    runs = sorted((p for p in root.iterdir() if p.is_dir()), key=lambda p: p.stat().st_mtime)
    if args.run:
        chosen = [p for p in runs if p.name == args.run or p.name.endswith(args.run)]
    elif args.last:
        chosen = runs[-args.last:]
    else:
        for p in runs:
            print(p.name)
        return 0
    if not chosen:
        print("no matching run", file=sys.stderr)
        return 1
    for run_dir in chosen:
        show_run(run_dir, args.max, not args.no_request)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
