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

if npm view --prefer-online "${package_name}@${version}" version >/dev/null 2>&1; then
  echo "${package_name}@${version} is already published; skipping."
  exit 0
fi

attempt=1
while (( attempt <= max_attempts )); do
  if (cd "$package_dir" && npm publish --access public --tag "$tag"); then
    # npm can accept the publish before its metadata is visible to readers.
    echo "Published ${package_name}@${version} on attempt ${attempt}."
    exit 0
  fi

  if npm view --prefer-online "${package_name}@${version}" version >/dev/null 2>&1; then
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
