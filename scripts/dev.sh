#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
umask 077
mkdir -p src/Runner.Host/data
if [ ! -s src/Runner.Host/data/operator.key ]; then
  openssl rand -hex 32 > src/Runner.Host/data/operator.key
fi
export Security__ApiKey="$(cat src/Runner.Host/data/operator.key)"
export Security__KeyDirectory="$(pwd)/src/Runner.Host/data/keys"
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:5080}"
export Storage__Initialize=true
echo 'Operator key is in src/Runner.Host/data/operator.key (local secret; do not commit).'
dotnet build src/Runner.Host -c Release
cd src/Runner.Host
dotnet bin/Release/net10.0/Runner.Host.dll --initialize
dotnet bin/Release/net10.0/Runner.Host.dll --upgrade-platform
exec dotnet bin/Release/net10.0/Runner.Host.dll
