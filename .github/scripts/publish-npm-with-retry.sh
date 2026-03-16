#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo "Usage: $0 <package_dir> <package_name> <version> <tag>" >&2
  exit 2
fi

package_dir="$1"
package_name="$2"
version="$3"
tag="$4"

max_attempts="${NPM_PUBLISH_MAX_ATTEMPTS:-5}"
delay_seconds="${NPM_PUBLISH_INITIAL_DELAY_SECONDS:-5}"
visibility_attempts="${NPM_PUBLISH_VISIBILITY_ATTEMPTS:-12}"
visibility_delay_seconds="${NPM_PUBLISH_VISIBILITY_DELAY_SECONDS:-5}"

wait_for_visibility() {
  local package_name="$1"
  local version="$2"
  local attempt=1

  while (( attempt <= visibility_attempts )); do
    if npm view "${package_name}@${version}" version >/dev/null 2>&1; then
      return 0
    fi

    if (( attempt == visibility_attempts )); then
      return 1
    fi

    sleep "${visibility_delay_seconds}"
    attempt=$((attempt + 1))
  done
}

if npm view "${package_name}@${version}" version >/dev/null 2>&1; then
  echo "${package_name}@${version} is already published; skipping."
  exit 0
fi

attempt=1
while (( attempt <= max_attempts )); do
  if (cd "$package_dir" && npm publish --access public --tag "$tag"); then
    if ! wait_for_visibility "${package_name}" "${version}"; then
      echo "Published ${package_name}@${version}, but it was not visible in npm metadata after ${visibility_attempts} checks." >&2
      exit 1
    fi

    echo "Published ${package_name}@${version} on attempt ${attempt}."
    exit 0
  fi

  if npm view "${package_name}@${version}" version >/dev/null 2>&1; then
    echo "${package_name}@${version} is already available after a publish error; continuing."
    exit 0
  fi

  if (( attempt == max_attempts )); then
    echo "Failed to publish ${package_name}@${version} after ${max_attempts} attempts." >&2
    exit 1
  fi

  next_attempt=$((attempt + 1))
  echo "Publish failed for ${package_name}@${version}; retrying in ${delay_seconds}s (attempt ${next_attempt}/${max_attempts})." >&2
  sleep "$delay_seconds"
  delay_seconds=$((delay_seconds * 2))
  attempt=$next_attempt
done
