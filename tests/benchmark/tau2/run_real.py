#!/usr/bin/env python3
"""τ²-bench ⇄ AgentWorkbench 真实评测运行器（真模型，非 fixture）。

与 run_smoke.py 的区别
----------------------
run_smoke.py 用 MockLlm 逐轮回放**写死**的 fixture，证明的是「链路通了、判分有牙」。
本脚本把两端都换成**真模型**，跑出来的是**真实的模型能力数字**：

    agent 侧：HarnessAgent  →  POST /api/model/invoke  →  ModelInvocation(.NET)  →  SiliconFlow
    user  侧：τ² 官方 user_simulator  →  litellm  →  SiliconFlow
    判分       ：τ² 自己（最终 DB 状态对比，EvaluationType.ENV）

没有任何 fixture、没有脚本化用户。这是"真实工程"，不是 demo。

判分的边界
----------
只用 `EvaluationType.ENV`（DB 状态对比）。因此**只挑 `nl_assertions` 为空的任务**——
带 nl_assertions 的任务需要 LLM-as-judge，撞《测试标准》§1.1 红线，本脚本主动跳过。

成本
----
默认两端都用 SiliconFlow 的**免费**模型（Xing4.0-29B），零成本（仅受平台限流约束）。
可用环境变量覆盖：AGENT_MODEL / USER_MODEL / MODELINV_URL。

用法
----
    python run_real.py --task 73            # 单任务（先跑通这一个）
    python run_real.py --limit 3            # 自动挑 3 个可纯确定性判分的任务
    python run_real.py --list               # 只列出候选任务，不跑
"""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
except Exception:  # pragma: no cover
    pass

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
TAU2_REPO = Path(os.environ.get("TAU2_REPO", r"D:\Code\tau2-bench"))

MODELINV_DLL = REPO / "tests/AgentWorkbench.ModelInvocation/bin/Debug/net10.0/AgentWorkbench.ModelInvocation.dll"
# 端口必须与 run_smoke.py 的 ModelInvocation（5000）错开：
# 两者都叫 ModelInvocation，但一个指向 MockLlm、一个指向真供应商。
# 共用端口 + 服务残留会让 real 复用 smoke 留下的实例，真模型请求被 MockLlm 接走
# （实测症状：HTTP 500 fixture_exhausted）。故 real 固定走 5001。
MODELINV_URL = os.environ.get("MODELINV_URL", "http://localhost:5001").rstrip("/")

AGENT_MODEL = os.environ.get("AGENT_MODEL", "XingChenAGI/Xing4.0-29B")
USER_MODEL = os.environ.get("USER_MODEL", "XingChenAGI/Xing4.0-29B")

MAX_STEPS = int(os.environ.get("MAX_STEPS", "20"))
MAX_ERRORS = int(os.environ.get("MAX_ERRORS", "8"))
SEED = int(os.environ.get("SEED", "42"))

LOG_DIR = HERE / ".run"

# litellm 的默认请求超时极长（600s），一旦某次调用没回就会干等 10 分钟。
# 这里强制一个明确的超时 + 有限重试，让失败快速显形而不是静默挂起。
# 注：免费推理模型（Xing4.0-29B）实测约 1/3 请求会挂死 >120s，60s 太紧会误杀正常的慢响应。
LLM_TIMEOUT = float(os.environ.get("LLM_TIMEOUT", "180"))
LLM_RETRIES = int(os.environ.get("LLM_RETRIES", "2"))

# agent 侧 HTTP 超时（agent → ModelInvocation）。同样放宽到 180s。
AGENT_TIMEOUT = float(os.environ.get("AGENT_TIMEOUT", "180"))


# --------------------------------------------------------------------------- #
# Provider 凭据（从运行库读取，不落盘、不打印）
# --------------------------------------------------------------------------- #

def load_provider() -> tuple[str, str]:
    db = REPO / "src/AgentWorkbench.Webapi/data/agentworkbench.db"
    if not db.exists():
        raise SystemExit(f"找不到运行库：{db}")
    con = sqlite3.connect(str(db))
    row = con.execute(
        "select base_url, api_key from providers where is_active=1"
    ).fetchone()
    con.close()
    if not row or not row[0] or not row[1]:
        raise SystemExit("运行库里没有活跃的 provider（base_url / api_key 为空）")
    return row[0].rstrip("/"), row[1]


# --------------------------------------------------------------------------- #
# ModelInvocation 服务（真模型端点）
# --------------------------------------------------------------------------- #

def _health(url: str) -> bool:
    try:
        with urllib.request.urlopen(f"{url}/health", timeout=2.0) as r:
            return r.status == 200
    except Exception:
        return False


def wait_health(url: str, timeout: float = 60.0) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        if _health(url):
            return True
        time.sleep(0.5)
    return False


def start_model_invocation(base_url: str, api_key: str, model: str) -> subprocess.Popen:
    if not MODELINV_DLL.exists():
        raise SystemExit(f"缺少构建产物：{MODELINV_DLL}\n先运行：dotnet build {MODELINV_DLL.parents[4]}")
    LOG_DIR.mkdir(exist_ok=True)
    log = open(LOG_DIR / "modelinvocation.real.log", "w", encoding="utf-8")
    env = dict(os.environ)
    env["ASPNETCORE_URLS"] = MODELINV_URL
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["Provider__BaseUrl"] = f"{base_url}/v1" if not base_url.endswith("/v1") else base_url
    env["Provider__ApiKey"] = api_key
    env["Provider__Model"] = model
    return subprocess.Popen(
        ["dotnet", str(MODELINV_DLL), "--urls", MODELINV_URL],
        cwd=str(MODELINV_DLL.parent),
        env=env,
        stdout=log,
        stderr=subprocess.STDOUT,
    )


# --------------------------------------------------------------------------- #
# 任务选择（只挑可纯确定性判分的）
# --------------------------------------------------------------------------- #

def eligible_tasks():
    from tau2.registry import registry

    tasks = registry.get_tasks_loader("retail")("base")
    out = []
    for t in tasks:
        ec = t.evaluation_criteria
        if ec.nl_assertions:
            continue  # 需要 LLM judge → 跳过（红线）
        basis = [getattr(b, "value", str(b)) for b in (ec.reward_basis or [])]
        out.append((t, basis))
    return out


# --------------------------------------------------------------------------- #
# 单任务真跑
# --------------------------------------------------------------------------- #

def run_one(task, base_url: str, api_key: str) -> dict:
    from tau2.registry import registry
    from tau2.runner.build import build_user
    from tau2.orchestrator.orchestrator import Orchestrator
    from tau2.evaluator.evaluator import EvaluationType, evaluate_simulation

    from adapter import create_harness_agent

    env = registry.get_env_constructor("retail")()          # 每任务全新环境（V2 教训）

    # 注意：适配器内部会自行拼 `/api/model/invoke`，此处只传 base URL。
    agent = create_harness_agent(
        env.get_tools(), env.get_policy(),
        endpoint=MODELINV_URL,
        model=AGENT_MODEL,
        timeout=AGENT_TIMEOUT,
    )
    user = build_user(
        "user_simulator", env, task,
        llm=f"openai/{USER_MODEL}",
        llm_args={
            "api_base": base_url,
            "api_key": api_key,
            "temperature": 0.0,
            "timeout": LLM_TIMEOUT,
            "num_retries": LLM_RETRIES,
        },
    )

    orch = Orchestrator(
        domain="retail", agent=agent, user=user, environment=env, task=task,
        max_steps=MAX_STEPS, max_errors=MAX_ERRORS, seed=SEED,
        validate_communication=False,
    )
    sim = orch.run()

    reward_info = evaluate_simulation(
        simulation=sim, task=task, evaluation_type=EvaluationType.ENV,
        solo_mode=False, domain="retail", strict_replay=False,
    )
    db = getattr(reward_info, "db_check", None)
    messages = list(getattr(sim, "messages", None) or orch.get_trajectory())
    from tau2.data_model.message import AssistantMessage, ToolMessage

    return {
        "task_id": str(task.id),
        "reward": float(reward_info.reward),
        "db_match": getattr(db, "db_match", None) if db is not None else None,
        "termination": str(getattr(sim, "termination_reason", "?")),
        "turns": len(messages),
        "tool_calls": sum(1 for m in messages if isinstance(m, AssistantMessage) and m.tool_calls),
        "tool_results": sum(1 for m in messages if isinstance(m, ToolMessage)),
    }


# --------------------------------------------------------------------------- #
# main
# --------------------------------------------------------------------------- #

def main() -> int:
    # 必须在 import litellm 之前设置：否则 litellm 启动时会联网拉取模型价格表并超时。
    os.environ.setdefault("LITELLM_LOCAL_MODEL_COST_MAP", "True")

    ap = argparse.ArgumentParser(description="τ²-bench ⇄ AgentWorkbench 真实评测（真模型）")
    ap.add_argument("--task", help="跑单个任务 id")
    ap.add_argument("--limit", type=int, default=0, help="自动挑 N 个可确定性判分的任务")
    ap.add_argument("--list", action="store_true", help="列出候选任务后退出")
    ap.add_argument("--no-start", action="store_true", help="假定 ModelInvocation 已在运行")
    ap.add_argument("--out", help="显式指定输出目录（默认 .run/real-<stamp>）")
    args = ap.parse_args()

    sys.path.insert(0, str(HERE))
    from adapter import register_all

    base_url, api_key = load_provider()
    print("τ²-bench ⇄ AgentWorkbench 真实评测（真模型）")
    print(f"  provider   = {base_url}")
    print(f"  agent 模型  = {AGENT_MODEL}")
    print(f"  user  模型  = {USER_MODEL}")
    print(f"  max_steps  = {MAX_STEPS}   max_errors = {MAX_ERRORS}")

    cand = eligible_tasks()
    if args.list:
        print(f"\n可纯确定性判分的任务（共 {len(cand)} 个）：")
        for t, basis in cand[:40]:
            print(f"  task {t.id:>4}  reward_basis={basis}")
        return 0

    if args.task:
        sel = [t for t, _ in cand if str(t.id) == str(args.task)]
        if not sel:
            raise SystemExit(f"task {args.task} 不在可确定性判分集合里（可能有 nl_assertions）")
    elif args.limit:
        sel = [t for t, _ in cand[: args.limit]]
    else:
        raise SystemExit("请给 --task <id> 或 --limit <n>（或 --list）")

    print(f"\n选中 {len(sel)} 个任务：{[str(t.id) for t in sel]}")

    proc = None
    out_dir: Path | None = None
    meta: dict = {}
    try:
        if not args.no_start:
            if _health(MODELINV_URL):
                print(f"\nModelInvocation 已在 {MODELINV_URL} 运行（复用）")
            else:
                print(f"\n启动 ModelInvocation → {base_url} ({AGENT_MODEL})…")
                proc = start_model_invocation(base_url, api_key, AGENT_MODEL)
            if not wait_health(MODELINV_URL):
                print("ModelInvocation 未就绪，终止。（日志见 .run/modelinvocation.real.log）")
                return 2

        added = register_all()
        print(f"registry 注册：{added or '（已注册）'}")

        stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        out_dir = Path(args.out) if args.out else LOG_DIR / f"real-{stamp}"
        out_dir.mkdir(parents=True, exist_ok=True)
        results: list[dict] = []
        meta = {
            "mode": "real",
            "status": "running",
            "started_at": datetime.now().isoformat(timespec="seconds"),
            "tasks": [str(t.id) for t in sel],
            "agent_model": AGENT_MODEL,
            "user_model": USER_MODEL,
            "max_steps": MAX_STEPS,
            "passed": 0,
            "total": len(sel),
            "pass_at_1": 0.0,
        }
        (out_dir / "meta.json").write_text(
            json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")

        for i, task in enumerate(sel, 1):
            print(f"\n--- [{i}/{len(sel)}] task {task.id} ---")
            t0 = time.time()
            try:
                r = run_one(task, base_url, api_key)
            except Exception as ex:
                r = {"task_id": str(task.id), "reward": None, "error": f"{type(ex).__name__}: {ex}"}
            r["elapsed_s"] = round(time.time() - t0, 1)
            results.append(r)
            if r.get("reward") is None:
                print(f"    ERROR  {r['error']}   ({r['elapsed_s']}s)")
            else:
                print(f"    reward = {r['reward']}   db_match = {r['db_match']}   "
                      f"termination = {r['termination']}   ({r['elapsed_s']}s)")

            # 增量落盘：中途失败也不丢已有结果
            (out_dir / "results.json").write_text(
                json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")

        ok = [r for r in results if r.get("reward") == 1.0]
        total = len(results)
        meta.update({
            "status": "done",
            "finished_at": datetime.now().isoformat(timespec="seconds"),
            "passed": len(ok),
            "total": total,
            "pass_at_1": round(len(ok) / total, 3) if total else 0.0,
        })
        (out_dir / "meta.json").write_text(
            json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
        print("\n" + "=" * 64)
        print(f"结果：{len(ok)}/{total} 通过   (pass@1 = {len(ok)/total:.3f})" if total else "无结果")
        print(f"落盘：{out_dir / 'results.json'}")
        return 0
    except Exception as ex:
        if out_dir is not None:
            try:
                meta.update({
                    "status": "failed",
                    "error": f"{type(ex).__name__}: {ex}",
                    "finished_at": datetime.now().isoformat(timespec="seconds"),
                })
                (out_dir / "meta.json").write_text(
                    json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
            except Exception:
                pass
        raise
    finally:
        if proc is not None:
            print("关闭 ModelInvocation…")
            proc.terminate()
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proc.kill()


if __name__ == "__main__":
    raise SystemExit(main())
