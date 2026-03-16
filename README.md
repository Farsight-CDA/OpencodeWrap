# OpencodeWrap (`ocw`)

Run [OpenCode](https://opencode.ai) in Docker with persistent state and lightweight profile management.

## Highlights

- Runs OpenCode in an isolated Docker container on Linux/macOS/Windows hosts.
- Persists OpenCode data across runs using a named Docker volume.
- Keeps OCW-managed persistent runtime state under `/ocw/state` and session-scoped config, pasted files, and ad hoc tool installs under `/ocw/session` inside the container.
- Uses profile-based Dockerfiles from `~/.opencode-wrap/profiles/<profile>/Dockerfile`, profile entrypoints from `~/.opencode-wrap/profiles/<profile>/entrypoint.sh`, OpenCode config files from `~/.opencode-wrap/profiles/<profile>/opencode/`, and profile-local helper binaries from `~/.opencode-wrap/profiles/<profile>/bin/`.
- Resolves and manages the latest upstream OpenCode release on both the host and Docker runtime automatically.
- Includes built-in starter profiles: `default`, `frontend`, `dotnet`, `data-science`, and `solidity`.
- `ocw run` starts `opencode serve` in Docker and attaches the OCW-managed host `opencode` TUI over localhost.

## Quick Start

Requirement: Docker (daemon running).

1. Install via npm:

   ```bash
   npm i -g @farsight-cda/ocw
   ```

   Upgrade later with:

   ```bash
   npm update -g @farsight-cda/ocw
   # or inside the CLI:
   ocw update
   # opt into the npm dev dist-tag:
   ocw update --dev
   ```

2. Open a new shell and run `ocw --help` to verify it resolves from `PATH`.
3. Run `ocw run` or `ocw --help`. OCW downloads and manages the latest OpenCode release automatically when needed.
4. Optional: import existing host OpenCode state into the Docker volume:

```bash
ocw data import-host
```

Prebuilt binaries are also available from the Actions workflow artifacts.

## Build From Source

Developer requirement:

- .NET 10 SDK

```bash
dotnet publish src/OpencodeWrap.csproj -c Release -r linux-x64 --self-contained true
./src/bin/Release/net10.0/linux-x64/publish/ocw --help
```

## Usage

```bash
# Forward OpenCode args directly without mounting a profile
ocw <opencode-args>

# Run with profile selection prompt
ocw run
# In interactive selection:
# - Up/Down: choose profile
# - Left/Right: switch between profile, resources, and networks
# - M: toggle workspace mount mode
# - R or Space: add a read-only resource directory on the resource tab
# - Backspace/Delete: remove the selected resource directory
# - Space: toggle the selected Docker network on the network tab
# - Enter: run, Esc: cancel
# Mounted in container under /workspace/.ocw-resources/<directory-name>
# `ocw run` publishes the backend to localhost only and launches
# the OCW-managed host `opencode attach http://127.0.0.1:<port>` client.
# OCW also appends runtime AGENTS instructions in the session profile's opencode directory so OpenCode
# knows these mounts are read-only reference material, and that it is running inside a Docker container
# where /tmp and /ocw/session/bin are safe for scratch clones and ad hoc session tools.

# Show deferred session logs after the interactive session exits
ocw run --verbose

# Profile management
ocw profile list
ocw profile add myprofile
ocw profile build myprofile
# Profile Dockerfiles provide the environment/tooling layer only.
# OCW adds the latest OpenCode runtime on top automatically.
# New profiles include ~/.opencode-wrap/profiles/myprofile/bin/
# Files placed there are mounted at /ocw/session/profile/bin and added to PATH
# Built-in profiles materialized for a run also include that bin/ directory

# Customize startup behavior per profile
# ~/.opencode-wrap/profiles/myprofile/entrypoint.sh

# Persisted state backup/restore
ocw data export backup.ocw
ocw data import backup.ocw
```

## License

MIT (see `LICENSE.txt`)
