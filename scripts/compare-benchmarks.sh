#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
: "${BENCH_MSSQL:?Set BENCH_MSSQL securely to a disposable SQL Server instance}"
dotnet restore tools/Runner.Benchmarks --locked-mode
dotnet build tools/Runner.Benchmarks -c Release --no-restore
dotnet tools/Runner.Benchmarks/bin/Release/net10.0/Runner.Benchmarks.dll "$@"
