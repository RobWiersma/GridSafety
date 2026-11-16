#!/usr/bin/env bash
# Usage: scripts/deploy.sh [stage] [extra cdk args...]
#   scripts/deploy.sh dev -c opsEmail=you@example.com
set -euo pipefail
cd "$(dirname "$0")/.."
STAGE="${1:-dev}"; shift || true

scripts/build.sh
cd infra
npx --yes aws-cdk@2 deploy "GridSafety-${STAGE}" -c stage="${STAGE}" --require-approval never "$@"
