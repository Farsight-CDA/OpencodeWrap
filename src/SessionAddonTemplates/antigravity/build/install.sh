#!/usr/bin/env bash
set -euo pipefail

release_url=https://storage.googleapis.com/antigravity-public/antigravity-cli/1.5.14-5302690847260672
case "$(uname -m)" in
    x86_64)
        archive=linux-x64/cli_linux_x64.tar.gz
        checksum=83311d3f354567612dc16c1ae2ea9b503d6f6833aa4e138d39aa229da3f3bb99b0e4d7cca0bce4796b0060d6a51d8113eb7449ed5dc0e7900bf217107ea5bff9
        ;;
    aarch64)
        archive=linux-arm/cli_linux_arm64.tar.gz
        checksum=c9b29bba475f63a1a80161ae93ba72b221c7a586d9851e4d0061546704bab66d6b2760b25d716d191724fc7564f3246456ee151e28aa37af48dbcd72bdc6c463
        ;;
    *)
        printf 'Antigravity CLI does not support architecture %s.\n' "$(uname -m)" >&2
        exit 1
        ;;
esac

staging="$(mktemp -d)"
trap 'rm -rf -- "$staging"' EXIT
curl --fail --silent --show-error --location --retry 3 --proto '=https' \
    "$release_url/$archive" -o "$staging/agy.tar.gz"
printf '%s  %s\n' "$checksum" "$staging/agy.tar.gz" | sha512sum --check
tar -xzf "$staging/agy.tar.gz" -C "$staging" antigravity
install -D -m 0755 "$staging/antigravity" /opt/ocw/antigravity/agy
install -D -m 0644 share-skills.mjs /opt/ocw/antigravity/share-skills.mjs
install -D -m 0755 agy-launcher.sh /usr/local/bin/agy
