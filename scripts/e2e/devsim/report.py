# Copyright (c) 2026 iwaitu
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Aggregates the result files of many developer-simulation runs into a trend report.

A run directory (``<root>/<timestamp>/``) holds one ``<scenario>.json`` per scenario, as written by ``devsim run --out``. This module reads a
root full of them and answers: what is the pass rate, which scenarios are flaky, why do runs fail, where does the time go, and which way is it
heading. Pure functions over plain dicts, so it is tested without a host.
"""
from __future__ import annotations

import json
import math
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any


def load_runs(root: Path, last: int | None = None) -> list[dict[str, Any]]:
    """Run directories under ``root`` in name order (names start with a sortable timestamp); unreadable files are skipped, not fatal."""
    runs = []
    for directory in sorted(path for path in Path(root).iterdir() if path.is_dir()) if Path(root).is_dir() else []:
        results = []
        for file in sorted(directory.glob("*.json")):
            try:
                data = json.loads(file.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            if isinstance(data, dict) and "scenario" in data and "steps" in data:
                results.append(data)
        if results:
            runs.append({"name": directory.name, "results": results})
    return runs[-last:] if last else runs


def failure_reason(result: dict[str, Any]) -> str:
    """One short, stable label for why a scenario failed, so reasons can be counted. Prefers what the system itself reported."""
    for step in result.get("steps", []):
        if step.get("passed") is not False:
            continue
        if step.get("error"):
            return "step error: " + str(step["error"]).split(":")[0][:60]
        receipt = step.get("receipt") or {}
        if receipt.get("failure_code"):
            return "verification: " + str(receipt["failure_code"])
        for job in step.get("jobs", []):
            if job.get("status") in ("Failed", "FailedRecoveryNeeded"):
                return "job failed: " + str(job.get("detail_code") or job.get("task") or "unknown")
        audit = (receipt.get("audit") or {})
        for layer in audit.get("layers", []):
            if layer.get("state") not in (1, "Pass", None):
                return f"audit {layer.get('name')}: {layer.get('reason')}"
        bad = [check["name"] for check in step.get("checks", []) if not check.get("ok")]
        if bad:
            return "expectation: " + bad[0][:70]
    return "unknown"


def percentile(values: list[float], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = max(0, min(len(ordered) - 1, math.ceil(fraction * len(ordered)) - 1))
    return ordered[index]


def aggregate(runs: list[dict[str, Any]]) -> dict[str, Any]:
    per_scenario: dict[str, dict[str, Any]] = {}
    reasons: Counter[str] = Counter()
    step_durations: dict[str, list[float]] = defaultdict(list)
    trend = []

    for run in runs:
        passed = failed = 0
        for result in run["results"]:
            name = result["scenario"]
            entry = per_scenario.setdefault(name, {"runs": 0, "passes": 0, "durations": [], "outcomes": [], "last_failure": None})
            entry["runs"] += 1
            entry["durations"].append(float(result.get("duration_s") or 0))
            ok = bool(result.get("passed"))
            entry["outcomes"].append(ok)
            if ok:
                entry["passes"] += 1
                passed += 1
            else:
                failed += 1
                reason = failure_reason(result)
                reasons[reason] += 1
                entry["last_failure"] = {"run": run["name"], "reason": reason, "session_id": result.get("session_id")}
            for step in result.get("steps", []):
                if not step.get("skipped"):
                    step_durations[f"{name} / {step.get('name')}"].append(float(step.get("duration_s") or 0))
        total = passed + failed
        meta = next((result["meta"] for result in run["results"] if result.get("meta")), {})
        trend.append({"run": run["name"], "passed": passed, "total": total, "rate": (passed / total) if total else 0.0,
                      "commit": meta.get("commit", ""), "label": meta.get("label", "")})

    scenarios = {}
    for name, entry in sorted(per_scenario.items()):
        outcomes = entry["outcomes"]
        scenarios[name] = {
            "runs": entry["runs"], "passes": entry["passes"], "rate": entry["passes"] / entry["runs"],
            "flaky": 0 < entry["passes"] < entry["runs"], "latest_passed": outcomes[-1],
            "avg_s": round(sum(entry["durations"]) / len(entry["durations"]), 1), "p95_s": round(percentile(entry["durations"], 0.95), 1),
            "last_failure": entry["last_failure"],
        }
    total_runs = sum(entry["runs"] for entry in per_scenario.values())
    total_passes = sum(entry["passes"] for entry in per_scenario.values())
    slowest = sorted(((name, round(sum(values) / len(values), 1)) for name, values in step_durations.items()), key=lambda pair: -pair[1])[:8]
    return {
        "runs": len(runs), "scenario_runs": total_runs, "overall_rate": (total_passes / total_runs) if total_runs else 0.0,
        "latest_rate": trend[-1]["rate"] if trend else 0.0, "scenarios": scenarios, "failure_reasons": reasons.most_common(),
        "slowest_steps": slowest, "trend": trend,
    }


def _bar(rate: float, width: int = 10) -> str:
    filled = round(rate * width)
    return "█" * filled + "░" * (width - filled)


def render_markdown(aggregated: dict[str, Any]) -> str:
    if not aggregated["runs"]:
        return "# Developer simulation trend\n\nNo runs found.\n"
    lines = ["# Developer simulation trend", "",
             f"{aggregated['runs']} run(s), {aggregated['scenario_runs']} scenario execution(s). "
             f"Overall pass rate **{aggregated['overall_rate']:.0%}**, latest run **{aggregated['latest_rate']:.0%}**.", "",
             "## Scenarios", "", "| Scenario | Pass rate | Runs | Avg | P95 | Latest | Note |", "|---|---|---|---|---|---|---|"]
    for name, row in aggregated["scenarios"].items():
        note = "flaky" if row["flaky"] else ""
        if not row["latest_passed"] and row["last_failure"]:
            note = (note + "; " if note else "") + row["last_failure"]["reason"]
        lines.append(f"| {name} | {row['rate']:.0%} ({row['passes']}/{row['runs']}) | {row['runs']} | {row['avg_s']}s | {row['p95_s']}s | "
                     f"{'PASS' if row['latest_passed'] else 'FAIL'} | {note} |")
    lines += ["", "## Why runs fail", ""]
    if aggregated["failure_reasons"]:
        lines += ["| Reason | Count |", "|---|---|"] + [f"| {reason} | {count} |" for reason, count in aggregated["failure_reasons"]]
    else:
        lines.append("No failures.")
    lines += ["", "## Where the time goes (average per step)", "", "| Step | Avg |", "|---|---|"]
    lines += [f"| {name} | {seconds}s |" for name, seconds in aggregated["slowest_steps"]]
    lines += ["", "## Trend (pass rate per run)", "",
              "Runs of different builds are not comparable: the commit is the checkout devsim ran from, the label is whatever the operator named the host build.", "",
              "| Run | Rate | Passed | Commit | Label |", "|---|---|---|---|---|"]
    lines += [f"| {point['run']} | {_bar(point['rate'])} {point['rate']:.0%} | {point['passed']}/{point['total']} | {point.get('commit') or '-'} | {point.get('label') or '-'} |"
              for point in aggregated["trend"]]
    return "\n".join(lines) + "\n"
