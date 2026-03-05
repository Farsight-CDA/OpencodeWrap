# OpencodeWrap (`ocw`)

Run [OpenCode](https://opencode.ai) in Docker with persistent state and lightweight profile management.

## Highlights

- Runs OpenCode in an isolated Docker container on Linux/macOS/Windows hosts.
- Persists OpenCode data across runs using a named Docker volume.
- Uses profile-based Dockerfiles from `~/.opencode-wrap/<profile>/Dockerfile`, profile entrypoints from `~/.opencode-wrap/<profile>/entrypoint.sh`, and OpenCode config files from `~/.opencode-wrap/<profile>/opencode/`.
- Includes built-in starter profiles: `default`, `frontend`, `dotnet`, and `data-science`.

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
# Forward OpenCode args directly (default profile)
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

# Workspace mount mode
ocw run --mount-mode mount           # default: read-write workspace mount
ocw run --mount-mode readonly-mount  # mount workspace as read-only
ocw run --mount-mode no-mount        # do not mount workspace

# Additional read-only resource mounts (repeat option)
ocw run -p default --resource-dir "C:\\Something"
ocw run -p default --resource-dir ../shared-assets --resource-dir ../docs
# Mounted in container under /workspace/.ocw-resources/<directory-name>

# Profile management
ocw profile list
ocw profile add myprofile
ocw profile build myprofile

# Customize startup behavior per profile
# ~/.opencode-wrap/myprofile/entrypoint.sh

# Persisted state backup/restore
ocw data export backup.ocw
ocw data import backup.ocw
```

## License

MIT (see `LICENSE.txt`)
