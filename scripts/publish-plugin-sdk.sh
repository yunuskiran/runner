#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
runner_feed_url="${1:?Usage: publish-plugin-sdk.sh <feed-endpoint> [version]}"
runner_sdk_version="${2:-0.1.0}"
runner_sdk_id="${RUNNER_PLUGIN_PACKAGE_ID:-YunusKiran.Runner.PluginSdk}"
: "${NUGET_API_KEY:?Set NUGET_API_KEY securely before publishing}"
runner_package="artifacts/nuget/$runner_sdk_id.$runner_sdk_version.nupkg"
[ -f "$runner_package" ] || { echo 'Pack the selected SDK version first.' >&2; exit 1; }
dotnet nuget push "$runner_package" --source "$runner_feed_url" --api-key "$NUGET_API_KEY" --no-symbols
