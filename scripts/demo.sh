#!/usr/bin/env bash
#
# AgentWorkbench 演示启动器（Git Bash）
#
# 为什么需要它：
#   1. 后端实例存活时既占 8080，又锁着 bin/**/*.dll —— 下一次 build 会报 MSB3027，
#      看着像代码错，真因是旧进程没退。
#   2. 演示至少要两个进程（后端 + 前端 dev server），两个终端来回切很容易演砸。
#
# 用法：
#   bash scripts/demo.sh                # 构建 + 起后端 + 起前端
#   SKIP_BUILD=1 bash scripts/demo.sh   # 跳过构建（改完前端只想重启时用）
#   PORT=9090 WEB_PORT=5199 bash scripts/demo.sh
#
# 退出：Ctrl+C 会同时收掉后端与前端（本脚本自己管这两个子进程）。
set -u

PORT="${PORT:-8080}"
WEB_PORT="${WEB_PORT:-5173}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
API_PROJ="$ROOT/src/AgentWorkbench.Webapi/AgentWorkbench.Webapi.csproj"
WEB_DIR="$ROOT/web"

api_pid=""
web_pid=""

cleanup() {
  echo ""
  echo "==> 停止演示服务"
  [ -n "$web_pid" ] && kill "$web_pid" 2>/dev/null
  [ -n "$api_pid" ] && kill "$api_pid" 2>/dev/null
  # dotnet run 会再 fork 一个真正的 app 进程，杀父不一定带走子，按端口补一刀
  pids="$(netstat -ano 2>/dev/null | tr -d '\r' | grep LISTENING | grep ":${PORT} " | awk '{print $5}' | sort -u)"
  for pid in ${pids}; do taskkill //F //PID "${pid}" >/dev/null 2>&1; done
  exit 0
}
trap cleanup INT TERM

echo "==> [1/4] 释放端口 ${PORT}"
pids="$(netstat -ano 2>/dev/null | tr -d '\r' | grep LISTENING | grep ":${PORT} " | awk '{print $5}' | sort -u)"
if [ -z "${pids}" ]; then
  echo "    端口空闲"
else
  for pid in ${pids}; do
    echo "    清理旧进程 ${pid}"
    taskkill //F //PID "${pid}" >/dev/null 2>&1 || echo "    !! 杀不掉 ${pid}（可能需要管理员权限）"
  done
  sleep 1
fi

if [ "${SKIP_BUILD:-0}" != "1" ]; then
  echo "==> [2/4] 构建"
  (cd "$ROOT" && dotnet build AgentWorkbench.slnx --nologo -v q >/dev/null) || {
    echo "    构建失败，先修编译再演示"; exit 1;
  }
  echo "    构建通过"
else
  echo "==> [2/4] 跳过构建（SKIP_BUILD=1）"
fi

echo "==> [3/4] 启动后端 http://localhost:${PORT}"
(cd "$ROOT" && dotnet run --project "$API_PROJ" --no-build --urls "http://localhost:${PORT}" >"$ROOT/data/demo-api.log" 2>&1) &
api_pid=$!

for i in $(seq 1 60); do
  if curl -s --noproxy '*' "http://localhost:${PORT}/api/health" >/dev/null 2>&1; then
    echo "    后端就绪（第 ${i} 次探测）"
    break
  fi
  if [ "$i" = "60" ]; then
    echo "    后端 60 秒未就绪，日志：data/demo-api.log"
    tail -20 "$ROOT/data/demo-api.log" 2>/dev/null
    cleanup
  fi
  sleep 1
done

if [ ! -d "$WEB_DIR/node_modules" ]; then
  echo "==> 前端依赖缺失，安装中（首次较慢）"
  (cd "$WEB_DIR" && npm install >/dev/null) || { echo "    npm install 失败"; cleanup; }
fi

echo "==> [4/4] 启动前端 http://localhost:${WEB_PORT}"
(cd "$WEB_DIR" && npm run dev -- --port "$WEB_PORT" >"$ROOT/data/demo-web.log" 2>&1) &
web_pid=$!

sleep 3
cat <<EOF

────────────────────────────────────────────
  演示地址   http://localhost:${WEB_PORT}
  接口地址   http://localhost:${PORT}/api/health
  日志       data/demo-api.log / data/demo-web.log
  退出       Ctrl+C（会一起收掉两个进程）
────────────────────────────────────────────

EOF

wait "$api_pid"
