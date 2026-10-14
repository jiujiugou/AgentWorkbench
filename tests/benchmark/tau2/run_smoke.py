#!/usr/bin/env python3
"""τ²-bench ⇄ AgentWorkbench 端到端冒烟运行器（里程碑 M2）。

它跑什么
--------
在**不调用任何真实模型**（全仓无 API key）的前提下，把完整链路跑通并判分：

    τ² Orchestrator (Python)
      → HarnessAgent.generate_next_message            (adapter/harness_agent.py)
        → POST {ModelInvocation}/api/model/invoke     (.NET 单步模型调用服务)
          → OpenAI 兼容 POST /chat/completions
            → {MockLlm}/v1/chat/completions           (.NET 确定性回放服务)
              → fixture[turn] 原样返回

确定性的两个来源：
    * 模型侧 —— MockLlm 的 replay fixture（逐轮回放，不采样、不联网）；
    * 用户侧 —— ScriptedUser（脚本化，不依赖 τ² 的 LLM user simulator，也不需要 key）。

判分完全交给 τ²：用**最终数据库状态**对比 golden 状态（EvaluationType.ENV）。
不做文本比对、不调 LLM judge（task 73 的 nl_assertions 为空 → NL 维度短路）。

两条验收（缺一不可）
--------------------
1. 正控：回放 golden 动作          → reward == 1.0
2. 负控：回放错误 order_id         → reward == 0.0

只有两条同时成立，才说明「链路通了**而且**判分有牙」。正控单独绿没有意义。

诚实边界
--------
本脚本只证明**接入链路**可用：
    - 测到的是 .NET 侧「能否作为一个合格的 agent 说话」，不是 harness 的编排质量
      （工具执行权与判分权都在 τ² 的 orchestrator 手里）；
    - 用户侧是脚本化的，不代表真实用户行为分布；
    - 不覆盖带 nl_assertions 的 40 个 LLM-judge 任务（撞《标准》§1.1 红线）。
详见 README.md。
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

# Windows 控制台默认可能是 GBK，脚本要打印中文，强制 UTF-8（失败也不致命）。
try:
    sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
except Exception:  # pragma: no cover
    pass

HERE = Path(__file__).resolve().parent               # tests/benchmark/tau2
REPO = HERE.parents[2]                               # 仓库根

TAU2_REPO = Path(os.environ.get("TAU2_REPO", r"D:\Code\tau2-bench"))

MOCKLLM_DLL = REPO / "tests/AgentWorkbench.MockLlm/bin/Debug/net10.0/AgentWorkbench.MockLlm.dll"
MODELINV_DLL = REPO / "tests/AgentWorkbench.ModelInvocation/bin/Debug/net10.0/AgentWorkbench.ModelInvocation.dll"

MOCKLLM_URL = os.environ.get("MOCKLLM_URL", "http://localhost:5099").rstrip("/")
MODELINV_URL = os.environ.get("MODELINV_URL", "http://localhost:5000").rstrip("/")

DEFAULT_TASK = "73"
MAX_STEPS = 10
MAX_ERRORS = 5

LOG_DIR = HERE / ".run"

FAILURES: list[str] = []


# --------------------------------------------------------------------------- #
# HTTP 小工具（stdlib，不引第三方依赖）
# --------------------------------------------------------------------------- #

def _get(url: str, timeout: float = 5.0):
    with urllib.request.urlopen(url, timeout=timeout) as resp:
        return resp.status, json.loads(resp.read().decode("utf-8"))


def _post(url: str, body: str, timeout: float = 15.0):
    req = urllib.request.Request(url, data=body.encode("utf-8"), method="POST")
    req.add_header("Content-Type", "application/json; charset=utf-8")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.status, resp.read().decode("utf-8")


def health(url: str) -> bool:
    try:
        status, _ = _get(f"{url}/health", timeout=2.0)
        return status == 200
    except Exception:
        return False


def wait_health(url: str, timeout: float = 60.0) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if health(url):
            return True
        time.sleep(0.5)
    return False


def check(label: str, ok: bool, detail: str = "") -> bool:
    mark = "PASS" if ok else "FAIL"
    print(f"  [{mark}] {label}" + (f"  ({detail})" if detail else ""))
    if not ok:
        FAILURES.append(label)
    return ok


# --------------------------------------------------------------------------- #
# 服务生命周期
# --------------------------------------------------------------------------- #

def start_dotnet(dll: Path, urls: str, log_name: str, extra_env: dict | None = None) -> subprocess.Popen:
    if not dll.exists():
        raise SystemExit(f"缺少构建产物：{dll}\n先运行：dotnet build {dll.parents[4]}")
    LOG_DIR.mkdir(exist_ok=True)
    log = open(LOG_DIR / f"{log_name}.log", "w", encoding="utf-8")
    env = dict(os.environ)
    env["ASPNETCORE_URLS"] = urls
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    if extra_env:
        env.update(extra_env)
    return subprocess.Popen(
        ["dotnet", str(dll), "--urls", urls],
        cwd=str(dll.parent),
        env=env,
        stdout=log,
        stderr=subprocess.STDOUT,
    )


# --------------------------------------------------------------------------- #
# τ² 侧
# --------------------------------------------------------------------------- #

def build_task(task_id: str):
    from tau2.registry import registry

    # get_tasks_loader 返回可调用对象；"base" 是零售域的基准任务集。
    tasks = registry.get_tasks_loader("retail")("base")
    task = next((t for t in tasks if str(t.id) == task_id), None)
    if task is None:
        raise SystemExit(f"零售域任务集里找不到 task {task_id}")
    return task


def run_scenario(
    name: str,
    fixture: Path,
    task,
    expect_reward: float,
    agent_cls,
    user_cls,
) -> float:
    print(f"\n--- {name} ---")

    # 1) 装载 fixture：MockLlm 会自动切到 replay 并把轮次游标归零。
    body = fixture.read_text(encoding="utf-8")
    status, resp = _post(f"{MOCKLLM_URL}/control/fixture", body)
    check(f"{name} · fixture 已装载", status == 200, resp[:160])
    state = _get(f"{MOCKLLM_URL}/control/state")[1]
    check(f"{name} · 场景切到 replay", state.get("scenario") == "replay", json.dumps(state))

    # 2) 全新环境实例（V2 实测：共用 env 会互相污染）。
    from tau2.registry import registry

    env = registry.get_env_constructor("retail")()

    # 3) 适配器 agent + 脚本化 user。
    agent = agent_cls(env.get_tools(), env.get_policy(), endpoint=MODELINV_URL)
    user = user_cls()

    # 4) 编排器。工具执行与判分都在 τ² 侧。
    from tau2.orchestrator.orchestrator import Orchestrator

    orch = Orchestrator(
        domain="retail",
        agent=agent,
        user=user,
        environment=env,
        task=task,
        max_steps=MAX_STEPS,
        max_errors=MAX_ERRORS,
        seed=42,
        validate_communication=False,
    )
    sim = orch.run()

    # 5) 轨迹结构：tool_call 与 ToolMessage 必须成对，且以 AGENT_STOP 收口。
    from tau2.data_model.message import AssistantMessage, ToolMessage

    messages = list(getattr(sim, "messages", None) or orch.get_trajectory())
    calls = [m for m in messages if isinstance(m, AssistantMessage) and m.tool_calls]
    results = [m for m in messages if isinstance(m, ToolMessage)]
    term = str(getattr(sim, "termination_reason", "?"))
    check(f"{name} · 轨迹含 assistant tool_call", len(calls) >= 1, f"{len(calls)} 条")
    check(f"{name} · tool_call 有对应 ToolMessage", len(results) >= 1, f"{len(results)} 条")
    check(f"{name} · 以 AGENT_STOP 收口", "AGENT_STOP" in term, term)

    # 6) 回放游标必须与逻辑轮次一致（防止 ModelRuntime 重试导致 fixture 错位）。
    after = _get(f"{MOCKLLM_URL}/control/state")[1]
    check(
        f"{name} · MockLlm 恰好消费 {len(calls) + 1} 轮回放",
        after.get("turn") == len(calls) + 1,
        json.dumps(after),
    )

    # 7) 判分：只看最终 DB 状态（NL 维度短路）。
    from tau2.evaluator.evaluator import EvaluationType, evaluate_simulation

    reward_info = evaluate_simulation(
        simulation=sim,
        task=task,
        evaluation_type=EvaluationType.ENV,
        solo_mode=False,
        domain="retail",
        strict_replay=False,
    )
    reward = float(reward_info.reward)
    db = getattr(reward_info, "db_check", None)
    db_match = getattr(db, "db_match", None) if db is not None else None
    print(f"    reward = {reward}   (期望 {expect_reward})   db_match = {db_match}")
    check(f"{name} · reward == {expect_reward}", abs(reward - expect_reward) < 1e-9, f"实得 {reward}")
    return reward


def main() -> int:
    ap = argparse.ArgumentParser(description="τ²-bench ⇄ AgentWorkbench 端到端冒烟运行器")
    ap.add_argument("--task", default=DEFAULT_TASK, help="零售域任务 id（默认 73）")
    ap.add_argument("--no-start", action="store_true", help="假定两个 .NET 服务已在运行，不自行启动")
    ap.add_argument("--keep-services", action="store_true", help="结束后保留自启的服务进程")
    args = ap.parse_args()

    print("τ²-bench ⇄ AgentWorkbench 端到端冒烟")
    print(f"  repo          = {REPO}")
    print(f"  tau2          = {TAU2_REPO}")
    print(f"  mockllm       = {MOCKLLM_URL}")
    print(f"  modelinvoke   = {MODELINV_URL}")
    print(f"  task          = {args.task}")

    started: list[subprocess.Popen] = []
    try:
        if not args.no_start:
            if not health(MOCKLLM_URL):
                print("\n启动 MockLlm…")
                started.append(start_dotnet(MOCKLLM_DLL, MOCKLLM_URL, "mockllm"))
            if not health(MODELINV_URL):
                print("启动 ModelInvocation（模型端点 → MockLlm）…")
                started.append(
                    start_dotnet(
                        MODELINV_DLL,
                        MODELINV_URL,
                        "modelinvocation",
                        extra_env={
                            "Provider__BaseUrl": f"{MOCKLLM_URL}/v1",
                            "Provider__ApiKey": "mock-not-a-real-key",
                            "Provider__Model": "mock",
                        },
                    )
                )

        print("\n等待服务就绪…")
        check("MockLlm 存活", wait_health(MOCKLLM_URL), MOCKLLM_URL)
        check("ModelInvocation 存活", wait_health(MODELINV_URL), MODELINV_URL)
        if FAILURES:
            print("\n服务未就绪，终止。（日志见 .run/）")
            return 2

        # registry 接入面自检：证明「运行时注册、不改 τ² 源码」这条路是通的。
        sys.path.insert(0, str(HERE))
        from adapter import HarnessAgent, ScriptedUser, register_all

        added = register_all()
        print(f"\nτ² registry 本次新增注册：{added or '（已注册）'}")
        from tau2.registry import registry

        check("registry 含 agentworkbench", "agentworkbench" in registry.get_agents(), str(registry.get_agents()))
        check("registry 含 scripted_user", "scripted_user" in registry.get_users(), str(registry.get_users()))

        task = build_task(args.task)
        basis = [getattr(b, "value", str(b)) for b in (task.evaluation_criteria.reward_basis or [])]
        print(f"\n载入 task {task.id}   reward_basis={basis}   nl_assertions={task.evaluation_criteria.nl_assertions}")

        run_scenario(
            "正控(golden)",
            HERE / "fixtures" / f"task{args.task}_golden.jsonl",
            task,
            1.0,
            HarnessAgent,
            ScriptedUser,
        )
        run_scenario(
            "负控(bad_order)",
            HERE / "fixtures" / f"task{args.task}_bad_order.jsonl",
            task,
            0.0,
            HarnessAgent,
            ScriptedUser,
        )
    finally:
        if started and not args.keep_services:
            print("\n关闭自启的服务…")
            for p in started:
                p.terminate()
            for p in started:
                try:
                    p.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    p.kill()

    print("\n" + "=" * 64)
    if FAILURES:
        print(f"结果：FAIL（{len(FAILURES)} 项未通过）")
        for f in FAILURES:
            print(f"  - {f}")
        return 1
    print("结果：PASS —— 正控 reward=1.0 且负控 reward=0.0，全链跑通")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
