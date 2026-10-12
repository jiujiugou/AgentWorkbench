"""Dump facts about a tau2 domain that the bridge/adapter/eval driver depend on.

Read-only introspection helper. Run it with the tau2-bench virtualenv:

    uv run --project D:\\Code\\tau2-bench python tests/benchmark/tau2/dump_domain.py --domain retail

What it prints:

* the tool schemas the domain exposes to the assistant (name + parameter keys);
* whether the golden ``actions`` of the evaluated tasks are assistant-side or
  user-side tool calls (the bridge can only proxy assistant-side calls);
* the reward basis / communicate-info / env-assertion shape of those tasks.

It exists because the integration makes three assumptions that must stay true
for a domain to be usable here:

1. every golden action is an assistant-side tool call;
2. the assistant tool set is static for the whole run (no per-task drift);
3. scoring only needs ``ENV`` + ``COMMUNICATE`` (no LLM judge).
"""

from __future__ import annotations

import argparse
import json
from collections import Counter

from tau2.registry import registry


def _tool_schemas(env) -> list[dict]:
    schemas = []
    for tool in env.get_tools():
        schema = tool.openai_schema["function"]
        params = schema.get("parameters") or {}
        schemas.append(
            {
                "name": schema["name"],
                "description_len": len(schema.get("description") or ""),
                "parameters": params,
            }
        )
    return sorted(schemas, key=lambda item: item["name"])


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--domain", default="retail")
    parser.add_argument("--split", default="base")
    parser.add_argument("--limit", type=int, default=0, help="0 = all tasks")
    parser.add_argument("--json-out", default="")
    args = parser.parse_args()

    env = registry.get_env_constructor(args.domain)()
    tools = _tool_schemas(env)

    loader = registry.get_tasks_loader(args.domain)
    tasks = loader(args.split)
    if args.limit:
        tasks = tasks[: args.limit]

    action_requestors: Counter[str] = Counter()
    action_names: Counter[str] = Counter()
    reward_basis: Counter[tuple] = Counter()
    tasks_with_actions = 0
    communicate_counts = []

    task_summaries = []
    for task in tasks:
        criteria = task.evaluation_criteria
        actions = list(criteria.actions or [])
        if actions:
            tasks_with_actions += 1
        for action in actions:
            action_requestors[action.requestor] += 1
            action_names[action.name] += 1
        communicate_counts.append(len(criteria.communicate_info or []))
        reward_basis[tuple(criteria.reward_basis or [])] += 1
        task_summaries.append(
            {
                "id": task.id,
                "n_actions": len(actions),
                "n_communicate_info": len(criteria.communicate_info or []),
                "n_env_assertions": len(criteria.env_assertions or []),
                "n_nl_assertions": len(criteria.nl_assertions or []),
                "reward_basis": list(criteria.reward_basis or []),
                "has_initial_state": task.initial_state is not None,
            }
        )

    report = {
        "domain": env.get_domain_name(),
        "split": args.split,
        "policy_chars": len(env.get_policy()),
        "tools": tools,
        "n_tasks": len(tasks),
        "tasks_with_golden_actions": tasks_with_actions,
        "golden_action_requestors": dict(action_requestors),
        "golden_action_names": dict(action_names.most_common()),
        "reward_basis_counts": {"+".join(k) or "<empty>": v for k, v in reward_basis.items()},
        "communicate_info_total": sum(communicate_counts),
        "tasks": task_summaries,
    }

    print(json.dumps(report, ensure_ascii=False, indent=2))
    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8") as handle:
            json.dump(report, handle, ensure_ascii=False, indent=2)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
