# OpencodeWrap (`ocw`)

Run [OpenCode](https://opencode.ai) in Docker with persistent state and lightweight profile management.

## Highlights

- Runs OpenCode in an isolated Docker container on Linux/Windows hosts.
- Persists OpenCode data across runs using a named Docker volume.
- Uses profile-based Dockerfiles from `~/.opencode-wrap/<profile>/Dockerfile` and OpenCode config files from `~/.opencode-wrap/<profile>/opencode/`.
- Includes built-in starter profiles: `default` and `dotnet`.

## Quick Start

Requirement: Docker (daemon running).

1. Download a prebuilt binary from the latest **Build Artifacts** workflow run (`ocw-linux-x64` or `ocw-win-x64`).
2. Extract the binary and install it somewhere in your `PATH`.
   - Linux example:

   ```bash
   chmod +x ocw
   sudo mv ocw /usr/local/bin/ocw
   ```

   - Windows example (PowerShell): move `ocw.exe` to a directory already in `PATH`, or add its directory to your user `PATH`.
3. Open a new shell and run `ocw --help` (or `ocw.exe --help` on Windows) to verify it resolves from `PATH`.
4. Optional: import existing host OpenCode state into the Docker volume:

```bash
ocw data import-host
```

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

# Run with a specific profile config from ~/.opencode-wrap/<profile>
ocw run dotnet

# Profile management
ocw profile list
ocw profile add myprofile
ocw profile build myprofile

# Persisted state backup/restore
ocw data export backup.ocw
ocw data import backup.ocw
```

## License

MIT (see `LICENSE.txt`)
