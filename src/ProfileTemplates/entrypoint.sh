#!/usr/bin/env bash
set -e

printf '[ocw] launching opencode2...\n' >&2
exec opencode2 "$@"
