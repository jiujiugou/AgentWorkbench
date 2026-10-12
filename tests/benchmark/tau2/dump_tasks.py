"""Dump complete tau2 tasks (not summaries) for building replay fixtures.

``dump_domain.py`` answers "what shape is this domain"; this script answers
"what exactly does task N expect". The agent replay fixtures used by
``run_smoke.py`` are derived from this output, so it must be lossless:
golden ``actions`` (with arguments) plus ``user_scenario`` are both included.

Run it with the tau2-bench virtualenv:

    uv run --project D:\\Code\\tau2-bench python tests/benchmark/tau2/dump_tasks.py `
        --domain retail --split base --json-out tests/benchmark/tau2/_retail_tasks.json
"""

from __future__ import annotations

import argparse
import json

from tau2.registry import registry


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--domain", default="retail")
    parser.add_argument("--split", default="base")
    parser.add_argument(
        "--ids",
        default="",
        help="逗号分隔的 task id 白名单；留空表示全部",
    )
    parser.add_argument("--limit", type=int, default=0, help="0 = 全部")
    parser.add_argument("--json-out", default="")
    args = parser.parse_args()

    loader = registry.get_tasks_loader(args.domain)
    tasks = loader(args.split)

    wanted = {item.strip() for item in args.ids.split(",") if item.strip()}
    if wanted:
        tasks = [task for task in tasks if str(task.id) in wanted]
    if args.limit:
        tasks = tasks[: args.limit]

    payload = {
        "domain": args.domain,
        "split": args.split,
        "n_tasks": len(tasks),
        "tasks": [task.model_dump(mode="json") for task in tasks],
    }

    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, ensure_ascii=False, indent=2)
        print(f"wrote {len(tasks)} tasks -> {args.json_out}")
    else:
        print(json.dumps(payload, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
