#!/usr/bin/env python3
"""校验 docs/core 里的引用是否还能解析到真实源码/测试符号。

像 link checker：文档点名的每个 Sxx_NN、每个 .cs 文件，
必须能在仓库里找到。改名或删掉后文档还写着旧名字，这里就红。

只读、零依赖、非零退出。不读实现逻辑、不跑测试、不评分。

防的是「引用漂移」——文档点名的东西还在不在。
防不了「文档说的判据根本没测试」——那是语义，只能人读。
防不了「测试是假绿」——符号存在不等于断言有效。

刻意不校验「文件:行号」：行号是校准时的快照，代码一动就漂，
验它只会制造噪音；定位时用方法名搜更可靠。
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs" / "core"
FLOW = ROOT / "tests" / "AgentWorkbench.ApplicationTests" / "Flow"

REF = re.compile(r"S(\d{2})\s*的\s*([\d\s、,，\-–—]+)")
CS = re.compile(r"`([^`\s]+\.cs)\b(?!proj)")
SRC = ROOT / "src"
TESTS = ROOT / "tests"


def expand(spec: str) -> list[int]:
    """'03、05-07' -> [3, 5, 6, 7]。范围与列举混用。"""
    nums: list[int] = []
    for part in re.split(r"[、,，\s]+", spec.strip()):
        part = part.strip()
        if not part:
            continue
        if re.search(r"[-–—]", part):
            a, b = re.split(r"[-–—]", part, maxsplit=1)
            try:
                nums.extend(range(int(a), int(b) + 1))
            except ValueError:
                continue
        else:
            try:
                nums.append(int(part))
            except ValueError:
                continue
    return sorted(set(nums))


def method_exists(s: str, n: int) -> bool:
    path = FLOW / f"S{s}ContractTests.cs"
    if not path.exists():
        return False
    text = path.read_text(encoding="utf-8")
    return re.search(rf"S{s}_{n:02d}_", text) is not None


def find_cs(ref: str) -> list[Path]:
    # 文档用 P/ 当路径别名、Core/... 当相对 src/ 的写法；只认文件名，
    # 不解析路径前缀——校验的是「这个文件还在不在」，不是「路径写得对不对」。
    name = ref.rsplit("/", 1)[-1]
    hits = list(SRC.rglob(name)) + list(TESTS.rglob(name))
    return [p for p in hits if "obj" not in p.parts and "bin" not in p.parts]


def main() -> int:
    if not DOCS.is_dir():
        print(f"目录不存在：{DOCS}")
        return 1

    total_refs = 0
    misses: list[str] = []

    for md in sorted(DOCS.glob("*.md")):
        lines = md.read_text(encoding="utf-8").splitlines()
        for lineno, line in enumerate(lines, 1):
            # S 编号引用
            for m in REF.finditer(line):
                s, spec = m.group(1), m.group(2)
                nums = expand(spec)
                if not nums:
                    continue
                total_refs += 1
                failed = [n for n in nums if not method_exists(s, n)]
                status = "OK" if not failed else "MISS"
                print(f"{md.name}:{lineno}  S{s} 的 {spec.strip():<8} -> {len(nums) - len(failed)}/{len(nums)} {status}")
                if failed:
                    shown = ", ".join(f"S{s}_{n:02d}" for n in failed[:8])
                    more = f" …另 {len(failed) - 8} 个" if len(failed) > 8 else ""
                    misses.append(f"{md.name}:{lineno}  找不到方法 {shown}{more}")

            # .cs 文件引用
            for m in CS.finditer(line):
                name = m.group(1)
                total_refs += 1
                hits = find_cs(name)
                status = "OK" if hits else "MISS"
                short = name.rsplit("/", 1)[-1]
                print(f"{md.name}:{lineno}  {short:<30} -> {len(hits)} 处 {status}")
                if not hits:
                    misses.append(f"{md.name}:{lineno}  找不到文件 {name}")

    print(f"\n引用 {total_refs} 处")
    if misses:
        print(f"漂移 {len(misses)} 处：")
        for miss in misses:
            print(f"  ! {miss}")
        return 1
    print("全部命中")
    return 0


if __name__ == "__main__":
    sys.exit(main())
