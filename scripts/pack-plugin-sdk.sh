#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
runner_sdk_version="${1:-0.1.0}"
runner_sdk_id="${RUNNER_PLUGIN_PACKAGE_ID:-YunusKiran.Runner.PluginSdk}"
[[ "$runner_sdk_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.-]+)?$ ]] || { echo 'Use a semantic package version.' >&2; exit 1; }
[[ "$runner_sdk_id" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]+$ ]] || { echo 'Invalid package ID.' >&2; exit 1; }
dotnet pack src/Runner.Contracts -c Release -o artifacts/nuget -p:PackageVersion="$runner_sdk_version" -p:RunnerPluginPackageId="$runner_sdk_id"
echo "Prepared artifacts/nuget/$runner_sdk_id.$runner_sdk_version.nupkg and symbols. Nothing was published."
