#!/usr/bin/env bash
# 本地小 CI：一条命令跑完编译、测试、文档检查。
#
#   ./scripts/ci.sh          # 全量：编译 → 全部测试 → 文档检查（约 40-60 秒）
#   ./scripts/ci.sh --fast   # 快查：文档检查 + DocsHygiene（约 15 秒）
#
# 特点：无网络、无 --restore（AI 会话里 restore 是坏的）、无 CI 服务、跑完即退。
# 判定：任一步出现"新红"就退出非零；已知红基线只告警不阻塞（见 KNOWN_REDS）。
# 错误格式：WHAT 发现了什么 / FIX 怎么改 / REF 去哪核对。

set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

FAST=0
[[ "${1:-}" == "--fast" ]] && FAST=1

APP_TESTS="tests/AgentWorkbench.ApplicationTests/AgentWorkbench.ApplicationTests.csproj"

# 已知红基线：只允许这些用例红。出现名单外的新红 → CI 失败。
# 基线来源：2026-11-23 全量实测（3 条 F47 预期红 + 2 条 DocRoutingGate 历史红）。
# 修好一条就从这里删一行；新增一条预期红要先想清楚是不是在把红条合法化。
KNOWN_REDS=(
  "AgentWorkbench.ApplicationTests.Docs.DocRoutingGateTests.R0_检查器能认出伪造的断链与缺失目标"
  "AgentWorkbench.ApplicationTests.Docs.DocRoutingGateTests.R1_索引路由词表里每个词必须在目标文档字面出现"
  "AgentWorkbench.ApplicationTests.Runtime.SessionContractTests.F47_ToolRegistration_FallbackMustBeFalse"
  "AgentWorkbench.ApplicationTests.Runtime.SessionContractTests.F47_DefaultOptions_LongTermEnabledMustBeFalse"
  "AgentWorkbench.ApplicationTests.Runtime.SessionContractTests.F47_OptionsSource_DefaultMustBeFalse"
)

FAILED=0
declare -a RESULTS=()

pass() { RESULTS+=("[PASS] $1"); printf '  → PASS\n'; }
fail() { RESULTS+=("[FAIL] $1"); FAILED=1; printf '  → FAIL\n'; }

step_run() { # step_run <名称> <命令...>：退出码即判定，成功只留摘要
  local name="$1"; shift
  printf '\n── %s\n' "$name"
  local log; log="$(mktemp)"
  if "$@" >"$log" 2>&1; then
    # 成功：只打摘要行（错误数 / 引用数 / 元数据结论），不刷全过程
    grep -E '个错误|个警告|^引用 [0-9]+ 处|^全部命中|^元数据自洽|^注意：|^documents/：|^FAIL ' "$log" | sed 's/^/  /'
    pass "$name"
  else
    tail -15 "$log" | sed 's/^/  /'
    fail "$name"
    printf '  WHAT：%s 返回非零\n' "$name"
    printf '  FIX ：按上面最后 15 行定位，修到该命令自己返回 0\n'
    printf '  REF ：AGENTS.md §4 改动流程\n'
  fi
  rm -f "$log"
}

step_test() {
  printf '\n── 测试（含已知红基线）\n'
  local log; log="$(mktemp)"
  dotnet test AgentWorkbench.slnx --no-restore --no-build >"$log" 2>&1 || true
  grep -E '^已通过!|^失败!' "$log" | sed 's/^/  /'

  local reds unexpected=""
  reds="$(grep -E '^ *失败 AgentWorkbench' "$log" | sed -E 's/^ *失败 //; s/ *\[[0-9]+ ms\]$//' | sort -u)"
  if [[ -n "$reds" ]]; then
    while IFS= read -r t; do
      [[ -z "$t" ]] && continue
      if ! printf '%s\n' "${KNOWN_REDS[@]}" | grep -qxF "$t"; then
        unexpected+="    $t"$'\n'
      fi
    done <<< "$reds"
  fi

  if [[ -n "$unexpected" ]]; then
    fail "测试"
    printf '  WHAT：出现已知基线之外的新红：\n%s' "$unexpected"
    printf '  FIX ：修掉新红；若确实是"预期红"，把它加进 scripts/ci.sh 的 KNOWN_REDS 并写清理由\n'
    printf '  REF ：AGENTS.md §4 第 6/8 条\n'
    rm -f "$log"; return 1
  fi

  if [[ -n "$reds" ]]; then
    printf '  已知红 %s 条（不阻塞，名单见 KNOWN_REDS）\n' "$(printf '%s\n' "$reds" | wc -l | tr -d ' ')"
  fi
  pass "测试"
  rm -f "$log"
}

step_consistency() { # 口径漂移恒 0：只报 WARN 数，不阻塞
  printf '\n── 口径漂移（WARN 需人裁决）\n'
  local log; log="$(mktemp)"
  if ! python scripts/doc_consistency_check.py >"$log" 2>&1; then
    fail "口径漂移"
    tail -5 "$log" | sed 's/^/  /'
    rm -f "$log"; return
  fi
  local n; n="$(grep -c '^WARN ' "$log" || true)"
  printf '  WARN %s 条（不阻塞；禁止拿数量当质量分）\n' "$n"
  pass "口径漂移"
  rm -f "$log"
}

step_run "编译" dotnet build AgentWorkbench.slnx --no-restore -v q || true

if [[ $FAST -eq 0 ]]; then
  step_test
else
  printf '\n── 测试（--fast：只跑 DocsHygiene）\n'
  if dotnet test "$APP_TESTS" --no-restore --filter FullyQualifiedName~DocsHygieneTests >/tmp/aw_ci_docs.log 2>&1; then
    tail -2 /tmp/aw_ci_docs.log | sed 's/^/  /'; pass "DocsHygiene"
  else
    tail -8 /tmp/aw_ci_docs.log | sed 's/^/  /'; fail "DocsHygiene"
  fi
fi

step_run "文档元数据（kb_check）" python scripts/kb_check.py
step_run "引用漂移（check_doc_refs）" python scripts/check_doc_refs.py
step_consistency

printf '\n════ 汇总\n'
for r in "${RESULTS[@]}"; do printf '%s\n' "$r"; done
if [[ $FAILED -ne 0 ]]; then
  printf '\n结果：失败。先看上面每个 FAIL 的 WHAT/FIX/REF。\n'
  exit 1
fi
printf '\n结果：通过。\n'
