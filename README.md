# ocw

`ocw` is a standalone C# CLI launcher that starts an interactive Docker container, mounts your current directory, keeps global Opencode config on the host, persists Opencode data/state in a Docker named volume, then attaches to `opencode` inside the container.

The CLI entrypoints are implemented with `System.CommandLine`.

## Behavior

- Expects host OS to be Windows or Linux.
- Ensures host config directory `$HOME/.opencode-wrap` exists every time `ocw` starts.
- If `$HOME/.opencode-wrap` is empty, initializes profile scaffolding:
  - `profiles.yaml`
  - `profiles/default/Dockerfile`
  - `profiles/dotnet/Dockerfile` (includes `dotnet-sdk-10.0`)
- Builds a local Docker image (`opencode-wrap:<content-hash>`) if it does not exist.
- Container includes:
  - Opencode installed via `curl -fsSL https://opencode.ai/install | bash`
  - `python3`
  - common shell utilities (`bash`, `curl`, `git`, `jq`, etc.)
- Mounts:
  - Current host directory -> `/workspace`
  - For `ocw run <profile>`, selected profile directory from `$HOME/.opencode-wrap/profiles/*` (read-only) -> `/opt/opencode-wrap/host-config`
  - Docker named volume `opencode-wrap-xdg` -> `$HOME/.xdg` in container
- Sets XDG homes so Opencode config is loaded from host config and data/state persist in the volume:
  - `XDG_CONFIG_HOME=$HOME/.config`
  - `XDG_DATA_HOME=$HOME/.xdg/.local/share`
  - `XDG_STATE_HOME=$HOME/.xdg/.local/state`
- Before launching `opencode`, resets `$XDG_CONFIG_HOME/opencode`; for `ocw run <profile>`, copies all files from `/opt/opencode-wrap/host-config` into it.
- Uses host UID/GID on Linux (`--user <uid>:<gid>`), runs as root on Windows.
- Runs and attaches to `opencode` in the container with interactive TTY.
- `ocw run <profile>` runs `opencode` with the specified profile config.
- Any command that is not `run` or `data` is forwarded directly to `opencode` (no profile config mount/copy).
- Stops and deletes the container on process termination/disconnect (`--rm` + explicit cleanup).
- Supports state migration commands under `data`:
  - `ocw data import <archive.zip>` extracts a ZIP and imports any of:
    - `.local/share/opencode`
    - `.local/state/opencode`
    into the XDG volume.
    - Import fails if the target volume already has Opencode state unless `-f`/`--force` is provided.
  - `ocw data export <archive.zip>` exports volume contents into a single ZIP containing:
    - `.local/share/opencode`
    - `.local/state/opencode`
  - `ocw data import-host` imports state directly from host home directory:
    - `~/.local/share/opencode`
    - `~/.local/state/opencode`
    - Import fails if the target volume already has Opencode state unless `-f`/`--force` is provided.
  - `ocw data reset-volume` prompts for confirmation and deletes the named Docker volume (`opencode-wrap-xdg`).
- Supports profile management commands under `profile`:
  - `ocw profile list` prints all profiles from `profiles.yaml` and marks the default profile.
  - `ocw profile add <name>` adds a profile entry in `profiles.yaml`, creates `profiles/<name>/`, and writes a starter `Dockerfile`.
  - `ocw profile delete <name>` removes a profile entry from `profiles.yaml` and deletes `profiles/<name>/`.
    - Deleting the default profile is not allowed.
  - `ocw profile build <name>` rebuilds that profile's Docker image with `docker build --no-cache`.
  - `ocw profile open` opens `$HOME/.opencode-wrap/profiles` in the host file explorer.

## Prerequisites

- Docker installed and daemon running
- .NET SDK (for building/publishing)

## Build Standalone Binary

This project defaults to Native AOT + single-file publishing.

Publish Native AOT binaries:

### Linux x64

```bash
dotnet publish -c Release -r linux-x64
```

### Windows x64

```powershell
dotnet publish -c Release -r win-x64
```

Published output will be under:

- `src/bin/Release/net10.0/linux-x64/publish/ocw`
- `src/bin/Release/net10.0/win-x64/publish/ocw.exe`

Put that binary on your `PATH` as `ocw`.

## Usage

From any project directory:

```bash
ocw run default
```

Pass args through to Opencode:

```bash
ocw --model gpt-5
```

Choose any profile defined in `$HOME/.opencode-wrap/profiles.yaml`.

Import existing Opencode state:

```bash
ocw data import /path/to/backup.zip
```

Overwrite existing imported state in the volume:

```bash
ocw data import --force /path/to/backup.zip
```

Expected ZIP layout:

- `.local/share/opencode`
- `.local/state/opencode`

Export current Opencode state:

```bash
ocw data export /path/to/backup.zip
```

Import state directly from host home directory:

```bash
ocw data import-host
```

Overwrite existing imported state in the volume:

```bash
ocw data import-host -f
```

Reset the named state volume (with confirmation prompt):

```bash
ocw data reset-volume
```

Add a new profile:

```bash
ocw profile add myprofile
```

List all profiles:

```bash
ocw profile list
```

Delete a profile:

```bash
ocw profile delete myprofile
```

Open the profiles directory in your file explorer:

```bash
ocw profile open
```

Rebuild a profile image without Docker cache:

```bash
ocw profile build myprofile
```
