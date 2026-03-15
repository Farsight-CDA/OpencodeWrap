#!/usr/bin/env bash
set -e

printf '[ocw] launching opencode...\n' >&2
exec opencode "$@"
