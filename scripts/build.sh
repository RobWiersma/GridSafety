#!/usr/bin/env bash
# Builds everything CDK needs: Lambda bundles (linux-arm64, ReadyToRun) and the React bundle.
set -euo pipefail
cd "$(dirname "$0")/.."

rm -rf artifacts
dotnet publish src/GridSafety.Api -c Release -r linux-arm64 --self-contained false -o artifacts/api
dotnet publish src/GridSafety.Functions -c Release -r linux-arm64 --self-contained false -o artifacts/functions

(cd web && npm ci && npm run build)
echo "Build complete -> artifacts/ and web/dist/"
