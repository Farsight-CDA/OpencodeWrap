# OpencodeWrap (`ocw`)

Run [OpenCode](https://opencode.ai) in Docker with persistent state and lightweight profile management.

## Highlights

- Runs OpenCode in an isolated Docker container on Linux/macOS/Windows hosts.
- Persists OpenCode data across runs using a named Docker volume.
- Keeps OCW-managed persistent runtime state under `/ocw/state` and session-scoped config, pasted files, and ad hoc tool installs under `/ocw/session` inside the container.
- Uses profile-based Dockerfiles from `~/.opencode-wrap/profiles/<profile>/Dockerfile`, profile entrypoints from `~/.opencode-wrap/profiles/<profile>/entrypoint.sh`, OpenCode config files from `~/.opencode-wrap/profiles/<profile>/opencode/`, and profile-local helper binaries from `~/.opencode-wrap/profiles/<profile>/bin/`.
- Includes built-in starter profiles: `default`, `frontend`, `dotnet`, `data-science`, and `solidity`.

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
3. Optional: import existing host OpenCode state into the Docker volume:

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
# - Space: toggle mount mode
# - R: add read-only resource directory
# - Backspace (or D): remove last resource directory
# - Enter: run, Esc: cancel

# Or specify a profile explicitly
ocw run --profile dotnet
ocw run -p frontend
ocw run -p data-science
ocw run -p solidity

# Workspace mount mode
ocw run --mount-mode mount           # default: read-write workspace mount
ocw run --mount-mode readonly-mount  # mount workspace as read-only
ocw run --mount-mode no-mount        # do not mount workspace; session starts in /workspace

# Additional read-only resource mounts (repeat option)
ocw run -p default --resource-dir "C:\\Something"
ocw run -p default --resource-dir ../shared-assets --resource-dir ../docs
# Mounted in container under /workspace/.ocw-resources/<directory-name>
# OCW also appends runtime AGENTS instructions in the session profile's opencode directory so OpenCode
# knows these mounts are read-only reference material, and that it is running inside a Docker container
# where /tmp and /ocw/session/bin are safe for scratch clones and ad hoc session tools.

# Profile management
ocw profile list
ocw profile add myprofile
ocw profile build myprofile
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
