# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Developer simulation: run scenarios against a live CodexFlow host, and report on how they trend.

    python scripts/e2e/devsim list
    python scripts/e2e/devsim run dependency-upgrade-compliant --out .tmp/devsim
    python scripts/e2e/devsim run --all --repeat 3 --out .tmp/devsim --fixtures-dir D:/fixtures
    python scripts/e2e/devsim report .tmp/devsim --last 14 --write .tmp/devsim/trend.md
    python scripts/e2e/devsim nightly --root .tmp/devsim --fixtures-dir D:/fixtures --min-pass-rate 0.8

Exit code 0 on success, 1 on failures (scenarios failed / pass rate under the threshold), 2 for usage errors.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import report  # noqa: E402
from client import GatewayClient, GatewayError  # noqa: E402
from fixtures import file_url, generate  # noqa: E402
from scenario import ScenarioError, load_all, run_scenario  # noqa: E402

SCENARIO_DIR = Path(__file__).resolve().parent / "scenarios"


def render_summary(results: list[dict]) -> str:
    lines = ["# Developer simulation", ""]
    if any(r.get("self_approval_protection") is False for r in results):
        lines += ["> **WARNING: 防自批保证不成立 (`selfApprovalProtection: false` / `Local:UnsafeFileControlToken=true`)**", ""]
    lines += ["| Scenario | Result | Duration | First failure |", "|---|---|---|---|"]
    for result in results:
        failure = ""
        for step in result["steps"]:
            if step.get("passed") is False:
                bad = [check for check in step["checks"] if not check["ok"]]
                failure = f"step {step['index']}: " + (step.get("error") or "; ".join(f"{c['name']} ({c['detail']})".strip() for c in bad))
                break
        lines.append(f"| {result['scenario']} | {'PASS' if result['passed'] else 'FAIL'} | {result['duration_s']}s | {failure[:300]} |")
    passed = sum(1 for result in results if result["passed"])
    lines += ["", f"{passed}/{len(results)} scenarios passed."]
    digests = [d for r in results for d in (r.get("approved_request_digests") or [])]
    if digests:
        lines += ["", f"Auto-approved `requestDigest` count: {len(digests)} (`{', '.join(digests[:10])}`)."]
    return "\n".join(lines) + "\n"


def run_metadata(label: str | None) -> dict:
    """Which build a result belongs to: the checkout devsim ran from (best effort) and an operator-chosen label for the host build."""
    try:
        commit = subprocess.run(["git", "-C", str(Path(__file__).resolve().parent), "rev-parse", "--short", "HEAD"], capture_output=True,
                                text=True, timeout=10, check=True).stdout.strip() or "unknown"
    except (OSError, subprocess.SubprocessError):
        commit = "unknown"
    return {"commit": commit, "label": label or ""}


def run_directory_name(epoch: float, index: int, total: int) -> str:
    """Sortable by time; repeats within one invocation get an ``-r<n>`` suffix so they can never collide."""
    stamp = time.strftime("%Y%m%d-%H%M%S", time.localtime(epoch))
    return stamp if total == 1 else f"{stamp}-r{index + 1}"


def _failed_setup(scenario: dict, repo: str | None, error: Exception) -> dict:
    return {"scenario": scenario["name"], "description": scenario.get("description", ""), "repo": repo, "passed": False, "duration_s": 0,
            "steps": [{"index": 0, "name": "setup", "passed": False, "skipped": False, "checks": [], "error": f"{type(error).__name__}: {error}"[:600],
                       "duration_s": 0}]}


def execute(selected: list[dict], args: argparse.Namespace, out_dir: Path | None) -> list[dict]:
    """Runs the scenarios once. Each result is written as soon as it exists, so an interrupted batch keeps what it finished."""

    def resolve_fixture(name: str) -> str:
        """A fresh repository per scenario run, so earlier runs can never leak into this one."""
        base = Path(args.fixtures_dir).resolve()
        existing = len(list(base.glob(name + "-*"))) if base.exists() else 0
        return file_url(generate(name, base / f"{name}-{time.strftime('%Y%m%d-%H%M%S')}-{existing}"))

    results = []
    meta = run_metadata(getattr(args, "label", None))
    if out_dir:
        out_dir.mkdir(parents=True, exist_ok=True)
    for scenario in selected:
        client = GatewayClient(
            args.api,
            token_file=getattr(args, "token_file", None),
            control_token_file=getattr(args, "control_token_file", None),
            execution_profile=getattr(args, "execution_profile", None),
            auto_approve=getattr(args, "auto_approve", False),
        )
        try:
            result = run_scenario(scenario, client, repo=args.repo, log=lambda text: print(text, flush=True), fixture_resolver=resolve_fixture)
        except (GatewayError, ScenarioError) as error:
            result = _failed_setup(scenario, args.repo, error)
        result["meta"] = meta
        results.append(result)
        print(f"{'PASS' if result['passed'] else 'FAIL'}  {result['scenario']}  ({result['duration_s']}s)", flush=True)
        if out_dir:
            (out_dir / f"{result['scenario']}.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    if out_dir:
        (out_dir / "summary.md").write_text(render_summary(results), encoding="utf-8")
        print(f"results written to {out_dir}")
    return results


def report_command(root: Path, last: int | None, write: Path | None, min_pass_rate: float | None, *, use_latest: bool = False) -> int:
    aggregated = report.aggregate(report.load_runs(root, last))
    text = report.render_markdown(aggregated)
    print(text)
    if write:
        Path(write).parent.mkdir(parents=True, exist_ok=True)
        Path(write).write_text(text, encoding="utf-8")
        Path(write).with_suffix(".json").write_text(json.dumps(aggregated, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    if min_pass_rate is None:
        return 0
    if not aggregated["runs"]:
        print("no runs found: a threshold check cannot pass on an empty history", file=sys.stderr)
        return 1
    rate = aggregated["latest_rate"] if use_latest else aggregated["overall_rate"]
    ok = rate >= min_pass_rate
    print(f"pass rate {rate:.0%} ({'latest run' if use_latest else 'window'}) {'meets' if ok else 'is below'} the {min_pass_rate:.0%} threshold")
    return 0 if ok else 1


def main(argv: list[str] | None = None) -> int:
    # Replies and failure reasons are Chinese; a Windows console defaulting to a legacy code page would print them as noise.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(prog="devsim", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("list", help="list the shipped scenarios")

    def host_options(p: argparse.ArgumentParser) -> None:
        p.add_argument("--api", default="http://127.0.0.1:5205", help="host base URL")
        p.add_argument("--token-file", default=None,
                       help="access token file of a local host (.codexflow/host.token); replaces the account login")
        p.add_argument("--control-token-file", default=None,
                       help="control-plane token file (.codexflow/control.token); defaults to sibling control.token next to --token-file")
        p.add_argument("--execution-profile", choices=["docker-auto"], default=None,
                       help="preferred headless execution profile: verifies/configures Docker backend with AutoInWorkspace/FullAuto")
        p.add_argument("--auto-approve", action="store_true",
                       help="automatically approve interactive execution requests via control-plane API and record requestDigests "
                            "(on Local backend requires Local:UnsafeFileControlToken=true and marks selfApprovalProtection=false)")
        p.add_argument("--repo", default=None, help="repository URL overriding the scenario's own")
        p.add_argument("--label", default=None, help="name of the host build under test (recorded in the results and shown in the trend)")
        p.add_argument("--fixtures-dir", default=".tmp/devsim-fixtures",
                       help="where fixture repositories are generated; the host must allow local repositories under it "
                            "(Workspace:AllowedLocalRepositoryRoots)")

    run = sub.add_parser("run", help="run scenarios")
    run.add_argument("names", nargs="*", help="scenario names (default: none; use --all)")
    run.add_argument("--all", action="store_true", help="run every shipped scenario")
    run.add_argument("--repeat", type=int, default=1, help="run the selection this many times (each run gets its own result directory)")
    run.add_argument("--out", default=None, help="directory for result files (default: not written)")
    host_options(run)

    rep = sub.add_parser("report", help="trend report over the run directories under a results root")
    rep.add_argument("root", help="the --out directory of earlier runs")
    rep.add_argument("--last", type=int, default=None, help="only the most recent N runs")
    rep.add_argument("--write", default=None, help="also write the markdown (and a .json next to it) here")
    rep.add_argument("--min-pass-rate", type=float, default=None, help="exit 1 when the pass rate over the window is below this (0-1)")

    night = sub.add_parser("nightly", help="run every scenario, add the run to the history, write the trend, judge the run")
    night.add_argument("--root", required=True, help="results root; keeps the history and the trend files")
    night.add_argument("--repeat", type=int, default=1)
    night.add_argument("--last", type=int, default=14, help="runs the trend covers")
    night.add_argument("--min-pass-rate", type=float, default=0.8, help="exit 1 when the run just finished passes less than this (0-1)")
    host_options(night)
    args = parser.parse_args(argv)

    if args.command == "report":
        return report_command(Path(args.root), args.last, Path(args.write) if args.write else None, args.min_pass_rate)

    try:
        scenarios = load_all(SCENARIO_DIR)
    except ScenarioError as error:
        print(f"invalid scenario: {error}", file=sys.stderr)
        return 2

    if args.command == "list":
        for scenario in scenarios:
            print(f"{scenario['name']:<40} {scenario.get('description', '')}")
        return 0

    if args.repeat < 1:
        print("--repeat must be at least 1", file=sys.stderr)
        return 2
    by_name = {scenario["name"]: scenario for scenario in scenarios}
    names = getattr(args, "names", [])
    unknown = [name for name in names if name not in by_name]
    take_all = args.command == "nightly" or args.all
    if unknown or not (names or take_all):
        print("choose scenarios by name or pass --all" + (f"; unknown: {unknown}" if unknown else ""), file=sys.stderr)
        return 2
    selected = scenarios if take_all else [by_name[name] for name in names]
    out_root = Path(args.root if args.command == "nightly" else args.out) if (args.command == "nightly" or args.out) else None

    started = time.time()
    all_results: list[list[dict]] = []
    for index in range(args.repeat):
        out_dir = out_root / run_directory_name(started, index, args.repeat) if out_root else None
        results = execute(selected, args, out_dir)
        all_results.append(results)
        print(render_summary(results))

    if args.command == "nightly":
        return report_command(out_root, args.last, out_root / "trend.md", args.min_pass_rate, use_latest=True)
    return 0 if all(result["passed"] for results in all_results for result in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
