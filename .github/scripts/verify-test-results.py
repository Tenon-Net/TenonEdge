#!/usr/bin/env python3
"""核对 VSTest 生成的 TRX 报告，避免零用例、跳过或失败被当作测试通过。

用法：
    python3 .github/scripts/verify-test-results.py <结果目录> --require <测试程序集名> [...]

任一条件不满足即以非零退出码结束：
  * 结果目录中没有 TRX 报告；
  * 某个必需的测试程序集没有对应报告；
  * 某份报告的用例总数或实际执行数为 0；
  * 存在失败、错误、超时、中止、未执行（含跳过）等非通过结果；
  * 测试运行本身没有正常完成（ResultSummary 的 outcome 不是 Completed，或有运行级错误，
    例如测试进程崩溃、会话超时）。此时各用例的计数可能全部显示通过。
结果汇总同时写入 GITHUB_STEP_SUMMARY（在 GitHub Actions 中运行时）。
"""

from __future__ import annotations

import argparse
import os
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path, PurePath

TRX_NAMESPACE = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

# 这些计数大于 0 时，报告不能算作通过。
NON_PASSING_COUNTERS = (
    "failed",
    "error",
    "timeout",
    "aborted",
    "inconclusive",
    "passedButRunAborted",
    "notRunnable",
    "notExecuted",
    "disconnected",
)


@dataclass
class Report:
    path: Path
    assemblies: list[str]
    counters: dict[str, int]
    outcome: str
    not_passed: list[str]
    run_errors: list[str]


def read_report(path: Path) -> Report:
    root = ET.parse(path).getroot()
    summary = root.find("t:ResultSummary", TRX_NAMESPACE)
    if summary is None:
        raise ValueError(f"{path}: 缺少 ResultSummary")
    counters_element = summary.find("t:Counters", TRX_NAMESPACE)
    if counters_element is None:
        raise ValueError(f"{path}: 缺少 Counters")
    counters = {name: int(value) for name, value in counters_element.attrib.items()}

    assemblies = sorted(
        {
            PurePath(method.attrib["codeBase"].replace("\\", "/")).stem
            for method in root.iterfind("t:TestDefinitions/t:UnitTest/t:TestMethod", TRX_NAMESPACE)
            if "codeBase" in method.attrib
        }
    )
    not_passed = [
        f'{result.attrib.get("outcome", "?")}: {result.attrib.get("testName", "?")}'
        for result in root.iterfind("t:Results/t:UnitTestResult", TRX_NAMESPACE)
        if result.attrib.get("outcome") != "Passed"
    ]
    run_errors = [
        " ".join((run_info.findtext("t:Text", default="", namespaces=TRX_NAMESPACE)).split())
        for run_info in summary.iterfind("t:RunInfos/t:RunInfo", TRX_NAMESPACE)
        if run_info.attrib.get("outcome") == "Error"
    ]
    return Report(path, assemblies, counters, summary.attrib.get("outcome", "?"), not_passed, run_errors)


def not_run_count(report: Report) -> int:
    """发现但没有执行的用例数。xUnit 跳过的用例经 VSTest 计入 total，但不计入 executed，
    TRX 的 notExecuted 计数仍可能为 0，因此按 total - executed 统计。"""
    return max(report.counters.get("total", 0) - report.counters.get("executed", 0), 0)


def problems_of(report: Report) -> list[str]:
    counters = report.counters
    problems = []
    if report.outcome != "Completed":
        problems.append(f"测试运行结果为 {report.outcome}，不是 Completed")
    if report.run_errors:
        problems.append(f"{len(report.run_errors)} 条运行级错误")
    if counters.get("total", 0) == 0 or counters.get("executed", 0) == 0:
        problems.append("没有发现或执行任何用例")
    if not_run_count(report) > 0:
        problems.append(f"{not_run_count(report)} 个用例未执行（跳过或未运行）")
    problems += [f"{name}={counters[name]}" for name in NON_PASSING_COUNTERS if counters.get(name, 0) > 0]
    if counters.get("passed", 0) != counters.get("total", 0):
        problems.append(f"通过数 {counters.get('passed', 0)} 不等于总数 {counters.get('total', 0)}")
    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("results_directory", type=Path)
    parser.add_argument("--require", action="append", default=[], metavar="ASSEMBLY",
                        help="必须有通过报告的测试程序集名，可重复")
    arguments = parser.parse_args()

    trx_files = sorted(arguments.results_directory.rglob("*.trx"))
    lines = ["## 测试结果核对", ""]
    failures: list[str] = []

    if not trx_files:
        failures.append(f"{arguments.results_directory} 中没有 TRX 报告")

    reports = [read_report(path) for path in trx_files]
    if reports:
        lines += [
            "| 测试程序集 | 总数 | 执行 | 通过 | 失败 | 未执行/跳过 | 结论 |",
            "|---|---:|---:|---:|---:|---:|---|",
        ]
    for report in reports:
        problems = problems_of(report)
        counters = report.counters
        name = ", ".join(report.assemblies) or report.path.name
        verdict = "通过" if not problems else "不通过：" + "；".join(problems)
        lines.append(
            f"| {name} | {counters.get('total', 0)} | {counters.get('executed', 0)} | "
            f"{counters.get('passed', 0)} | {counters.get('failed', 0)} | "
            f"{not_run_count(report)} | {verdict} |"
        )
        if problems:
            failures.append(f"{name}（{report.path.name}）：{'；'.join(problems)}")
            failures += [f"    {item}" for item in report.not_passed]
            failures += [f"    运行级错误：{text[:500]}" for text in report.run_errors]

    reported = {assembly for report in reports for assembly in report.assemblies}
    failures += [f"{assembly}：没有对应的测试报告" for assembly in arguments.require if assembly not in reported]

    lines.append("")
    lines.append("结论：" + ("全部通过" if not failures else "不通过"))
    lines += [f"- {failure}" for failure in failures]
    text = "\n".join(lines) + "\n"
    print(text)

    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with open(summary_path, "a", encoding="utf-8") as summary:
            summary.write(text)

    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
