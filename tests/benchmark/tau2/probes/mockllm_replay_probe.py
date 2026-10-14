#!/usr/bin/env python3
"""
M1 验收探针：MockLlm 的 fixture 回放能力。

跑之前先起 MockLlm：
    dotnet tests/AgentWorkbench.MockLlm/bin/Debug/net10.0/AgentWorkbench.MockLlm.dll --urls http://localhost:5099
    C:\\Users\\jiuji\\.workbuddy\\binaries\\python\\versions\\3.13.12\\python.exe tests/benchmark/tau2/probes/mockllm_replay_probe.py

验证点（全部为断言，任一条不成立即非零退出）：
  1. JSONL fixture 能被加载，按轮次回放（第 1 轮 tool_calls，第 2 轮文本）。
  2. {"turns":[...]} 对象形式同样可用。
  3. fixture 耗尽 -> HTTP 500 + code=fixture_exhausted（不静默绕回）。
  4. /control/reset 把轮次游标复位，fixture 保留。
  5. 请求 DTO 能解析 assistant 消息里的 tool_calls 与 tool 消息的 tool_call_id（/control/last-request）。
  6. 无 fixture 时切 replay 被拒绝（400）。
  7. 多 tool_calls（一轮两个）逐条返回，id/name/arguments 保真。
"""

from __future__ import annotations

import json
import sys
import urllib.error
import urllib.request

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5099"
FRESH_ONLY = "--fresh-only" in sys.argv
FAILURES: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    mark = "PASS" if ok else "FAIL"
    print(f"  [{mark}] {name}" + (f" — {detail}" if detail else ""))
    if not ok:
        FAILURES.append(name)


def post(path: str, body: str | None = None, content_type: str = "application/json"):
    data = body.encode("utf-8") if body is not None else b""
    req = urllib.request.Request(BASE + path, data=data, method="POST")
    req.add_header("Content-Type", content_type)
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            return resp.status, json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8")
        try:
            return e.code, json.loads(raw)
        except json.JSONDecodeError:
            return e.code, {"raw": raw}


def get(path: str):
    with urllib.request.urlopen(BASE + path, timeout=10) as resp:
        return resp.status, json.loads(resp.read().decode("utf-8"))


def complete(messages: list[dict], tools: list[dict] | None = None):
    body = json.dumps({"model": "mock", "messages": messages, "tools": tools or []})
    return post("/v1/chat/completions", body)


def assistant_of(status: int, payload: dict):
    return payload["choices"][0]["message"]


def main() -> int:
    print("M1 · MockLlm fixture 回放探针")
    print(f"  target = {BASE}")

    status, health = get("/health")
    check("健康检查可达", status == 200 and health.get("role") == "mock-llm", str(health))
    if status != 200:
        print("\nMockLlm 未启动，终止。")
        return 2

    # ---------- 6b. 全新进程（未加载任何 fixture）：切 replay 必须被拒 ----------
    # 这条必须用「刚起的干净实例」跑，同进程里 fixture 已加载过就测不出。
    if FRESH_ONLY:
        print("\n[6b] 全新实例 · 无 fixture 切 replay")
        status, state = get("/control/state")
        check("全新实例 fixtureTurns == 0", state.get("fixtureTurns") == 0, str(state))
        status, res = post("/control/scenario?name=replay")
        check("无 fixture 时切 replay 被拒 400", status == 400, f"status={status} {res}")
        print()
        if FAILURES:
            print(f"FAILED -> {FAILURES}")
            return 1
        print("ALL PASS")
        return 0

    # ---------- 1. JSONL fixture 按轮次回放 ----------
    print("\n[1] JSONL fixture 回放")
    jsonl = "\n".join([
        "# task 73 golden action（注释行应被忽略）",
        json.dumps({"tool_calls": [{
            "id": "call_golden",
            "name": "return_delivered_order_items",
            "arguments": {"order_id": "#W5272531", "item_ids": ["7228247242"], "payment_method_id": "credit_card_6824399"},
        }]}),
        json.dumps({"content": "###STOP###"}),
    ])
    status, loaded = post("/control/fixture", jsonl)
    check("fixture 加载成功（忽略注释行）", status == 200 and loaded.get("fixtureTurns") == 2, str(loaded))

    status, payload = complete([{"role": "system", "content": "policy"}, {"role": "user", "content": "我要退货"}])
    msg = assistant_of(status, payload)
    calls = msg.get("tool_calls") or []
    check("第 1 轮返回 tool_calls", status == 200 and len(calls) == 1, json.dumps(msg, ensure_ascii=False))
    if calls:
        fn = calls[0]["function"]
        args = json.loads(fn["arguments"])
        check("工具名保真", fn["name"] == "return_delivered_order_items", fn["name"])
        check("arguments 为 JSON 字符串且可解析", args.get("order_id") == "#W5272531", json.dumps(args, ensure_ascii=False))
        check("id 保真", calls[0].get("id") == "call_golden", str(calls[0].get("id")))

    status, payload = complete([
        {"role": "system", "content": "policy"},
        {"role": "user", "content": "我要退货"},
        {"role": "assistant", "content": None, "tool_calls": [
            {"id": "call_golden", "type": "function",
             "function": {"name": "return_delivered_order_items", "arguments": "{\"order_id\":\"#W5272531\"}"}}]},
        {"role": "tool", "tool_call_id": "call_golden", "content": "{\"ok\":true}"},
    ])
    msg = assistant_of(status, payload)
    check("第 2 轮返回文本 ###STOP###", status == 200 and msg.get("content") == "###STOP###", json.dumps(msg, ensure_ascii=False))

    # ---------- 3. 耗尽 -> 500（fail-loud） ----------
    print("\n[3] fixture 耗尽")
    status, payload = complete([{"role": "user", "content": "再来一次"}])
    err = payload.get("error", {})
    check("耗尽返回 500", status == 500, f"status={status}")
    check("错误码为 fixture_exhausted", err.get("code") == "fixture_exhausted", str(err))

    # ---------- 4. reset 复位游标 ----------
    print("\n[4] /control/reset")
    status, state = get("/control/state")
    before = state.get("turn")
    status, res = post("/control/reset")
    check("reset 后 turn 归零", res.get("turn") == 0, f"before={before} after={res.get('turn')}")
    status, state = get("/control/state")
    check("fixture 保留未被清空", state.get("fixtureTurns") == 2, str(state))
    status, payload = complete([{"role": "user", "content": "复位后重跑"}])
    check("复位后从第 1 轮重新开始", (assistant_of(status, payload).get("tool_calls") or []) != [], str(payload)[:120])

    # ---------- 5. 请求 DTO 解析 tool_calls / tool_call_id ----------
    print("\n[5] 请求 DTO round-trip（/control/last-request）")
    post("/control/reset")
    complete([
        {"role": "system", "content": "policy"},
        {"role": "user", "content": "退货"},
        {"role": "assistant", "content": None, "tool_calls": [
            {"id": "call_x", "type": "function", "function": {"name": "t1", "arguments": "{}"}},
            {"id": "call_y", "type": "function", "function": {"name": "t2", "arguments": "{}"}}]},
        {"role": "tool", "tool_call_id": "call_x", "content": "r1"},
        {"role": "tool", "tool_call_id": "call_y", "content": "r2"},
    ])
    status, seen = get("/control/last-request")
    msgs = seen.get("messages") or []
    asst = next((m for m in msgs if m.get("role") == "assistant"), None)
    tools_msgs = [m for m in msgs if m.get("role") == "tool"]
    check("assistant 消息的 tool_calls 被解析（2 个）", asst is not None and asst.get("toolCalls") == 2, json.dumps(asst, ensure_ascii=False))
    check("tool 消息的 tool_call_id 被解析", len(tools_msgs) == 2 and {m.get("toolCallId") for m in tools_msgs} == {"call_x", "call_y"}, json.dumps(tools_msgs, ensure_ascii=False))
    check("messages 未被丢字段（共 5 条）", len(msgs) == 5, f"len={len(msgs)}")

    # ---------- 6. 无 fixture 时切 replay 被拒 ----------
    print("\n[6] 无 fixture 切 replay")
    # 先切到 normal，再清空 fixture？没有清空端点，改用新进程语义：直接验证「已加载过 fixture」的反面不可行，
    # 改为验证未知场景被拒 + replay 在已加载状态下可切。
    status, res = post("/control/scenario?name=no-such-scenario")
    check("未知场景被拒 400", status == 400, str(res))
    status, res = post("/control/scenario?name=replay")
    check("已加载 fixture 时切 replay 成功", status == 200 and res.get("scenario") == "replay", str(res))

    # ---------- 7. 一轮多个 tool_calls ----------
    print("\n[7] 一轮多个 tool_calls")
    multi = json.dumps({"turns": [
        {"tool_calls": [
            {"id": "c1", "name": "read_order", "arguments": {"order_id": "#A"}},
            {"id": "c2", "name": "read_user", "arguments": {"user_id": "u1"}},
        ]},
        {"content": "done"},
    ]})
    status, res = post("/control/fixture", multi)
    check("对象形式 {\"turns\":[...]} 加载成功", status == 200 and res.get("fixtureTurns") == 2, str(res))
    status, payload = complete([{"role": "user", "content": "两个都要"}])
    calls = assistant_of(status, payload).get("tool_calls") or []
    names = [c["function"]["name"] for c in calls]
    ids = [c.get("id") for c in calls]
    check("一轮返回两个 tool_calls", len(calls) == 2, json.dumps(names, ensure_ascii=False))
    check("多调用 id/name 逐条保真", names == ["read_order", "read_user"] and ids == ["c1", "c2"], f"{names} {ids}")

    # ---------- 收尾 ----------
    print("\n[8] 兼容性：旧场景仍在")
    for name in ("normal", "harness-tool", "harness-loop", "reject-tools"):
        post("/control/reset")
        status, res = post(f"/control/scenario?name={name}")
        check(f"场景 {name} 可切换", status == 200, str(res))

    print()
    if FAILURES:
        print(f"FAILED: {len(FAILURES)} 条未通过 -> {FAILURES}")
        return 1
    print("ALL PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
