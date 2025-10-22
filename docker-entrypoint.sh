#!/usr/bin/env bash
set -euo pipefail

HTTP_LOG_API_PORT="${HTTP_LOG_API_PORT:-5005}"
HTTP_LOG_API_URLS="http://0.0.0.0:${HTTP_LOG_API_PORT}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKER_DIR="${SCRIPT_DIR}/AMCSSZ.NWF.Shared.FlowableHttpWorkerUsageExample"
API_DIR="${SCRIPT_DIR}/AMCSSZ.NWF.Shared.HttpLogApi"

cleanup() {
  if [[ -n "${HTTP_LOG_API_PID:-}" ]]; then
    kill "$HTTP_LOG_API_PID" >/dev/null 2>&1 || true
    wait "$HTTP_LOG_API_PID" 2>/dev/null || true
  fi
}

trap cleanup EXIT INT TERM

# Start the local HttpLogApi in the background so the worker can call it.
(cd "$API_DIR" && dotnet AMCSSZ.NWF.Shared.HttpLogApi.dll --urls "${HTTP_LOG_API_URLS}") &
HTTP_LOG_API_PID=$!

# Give the API a moment to boot and fail fast if it exits immediately.
sleep 1
if ! kill -0 "$HTTP_LOG_API_PID" >/dev/null 2>&1; then
  wait "$HTTP_LOG_API_PID"
fi

# Run the worker in the foreground (keeps the container alive).
cd "$WORKER_DIR"
dotnet AMCSSZ.NWF.Shared.FlowableHttpWorkerUsageExample.dll
