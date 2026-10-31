#!/usr/bin/env bash
#
# AgentWorkbench dev launcher (Git Bash).
#
# WHY THIS EXISTS
#   If a previous Webapi instance is still alive it holds BOTH port 8080 and a
#   write lock on bin/**/*.dll. The next build then dies with MSB3021/MSB3027
#   ("the file is being used by another process ... .NET Host <pid>"), which
#   looks like a code error but is not.
#
#   Root cause: `dotnet run` spawns the app as a CHILD process. Windows does not
#   cascade-kill children when the parent dies, so an abruptly-killed launcher
#   leaves the app running as an orphan. Nothing asks it to exit, and a running
#   process cannot release its own memory-mapped DLLs.
#
#   So: clear the port first, then start. Use this instead of bare `dotnet run`.
#
# USAGE
#   bash scripts/dev.sh              # build + run
#   SKIP_BUILD=1 bash scripts/dev.sh # run without rebuilding
#   PORT=9090 bash scripts/dev.sh    # use another port
#
set -u

PORT="${PORT:-8080}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="src/AgentWorkbench.Webapi/AgentWorkbench.Webapi.csproj"

echo "==> [1/2] releasing port ${PORT}"
pids="$(netstat -ano 2>/dev/null | tr -d '\r' | grep LISTENING | grep ":${PORT} " | awk '{print $5}' | sort -u)"
if [ -z "${pids}" ]; then
  echo "    port is free"
else
  for pid in ${pids}; do
    echo "    killing stale process ${pid}"
    taskkill //F //PID "${pid}" >/dev/null 2>&1 || echo "    !! failed to kill ${pid} (try an elevated shell)"
  done
  sleep 1
fi

cd "${ROOT}" || { echo "cannot cd to ${ROOT}"; exit 1; }

echo "==> [2/2] starting AgentWorkbench.Webapi on http://localhost:${PORT}"
if [ "${SKIP_BUILD:-0}" = "1" ]; then
  exec dotnet run --project "${PROJ}" --no-build --urls "http://localhost:${PORT}"
else
  exec dotnet run --project "${PROJ}" --urls "http://localhost:${PORT}"
fi
