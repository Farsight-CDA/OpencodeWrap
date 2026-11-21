#!/usr/bin/env bash
set -euo pipefail

npm install -g --no-fund --no-audit \
    --allow-scripts=@anthropic-ai/claude-code @anthropic-ai/claude-code@2.1.323
