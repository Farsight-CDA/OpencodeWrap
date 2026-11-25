#!/usr/bin/env bash
set -euo pipefail

release_url=https://storage.googleapis.com/antigravity-public/antigravity-cli/1.5.17-4823843970351104
case "$(uname -m)" in
    x86_64)
        archive=linux-x64/cli_linux_x64.tar.gz
        checksum=71a967aa16e0ab5d87196bcd6772130bdab55b2fd768cd24d21b1da401ff9f60280b976cad3dc14a2d646233b0eaf8caf87c490eed73044d54550cbb3d8d67d2
        ;;
    aarch64)
        archive=linux-arm/cli_linux_arm64.tar.gz
        checksum=e4f086d0730b325cfd438ea90396ee78eb23a3110ef7f792824c8817b8d0432462b7b5f91b3d933fc1932a6f4ba73de505e2e96f44448c9fb2427292ead716f7
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
