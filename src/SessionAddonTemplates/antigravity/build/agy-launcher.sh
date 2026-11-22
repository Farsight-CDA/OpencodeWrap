#!/usr/bin/env bash
set -euo pipefail

gemini_home="$HOME/.gemini"
state_directory=/ocw/state/antigravity
if [[ ! -e "$gemini_home" && ! -L "$gemini_home" ]]; then
    mkdir -p -m 0700 "$state_directory"
    # Another terminal may be starting AGY at the same time.
    if ! ln -sT "$state_directory" "$gemini_home" && [[ ! -d "$gemini_home" ]]; then
        exit 1
    fi
fi

if [[ -n "${OCW_PROFILE_ROOT:-}" && "$gemini_home" -ef "$state_directory" ]]; then
    if ! node /opt/ocw/antigravity/share-skills.mjs "$OCW_PROFILE_ROOT" "$state_directory"; then
        printf 'OCW: could not share profile skills with Antigravity; continuing without the skill link.\n' >&2
    fi
fi

if [[ "${1:-}" == acp ]]; then
    shift
    set -- acp --dangerously-skip-permissions "$@"
fi

exec /opt/ocw/antigravity/agy "$@"
