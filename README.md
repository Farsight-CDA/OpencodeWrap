# OpencodeWrap (`ocw`)

Run [OpenCode](https://opencode.ai) in Docker with persistent state and lightweight profile management.

## Highlights

- Runs OpenCode in an isolated Docker container on Linux/Windows hosts.
- Persists OpenCode data across runs using a named Docker volume.
- Uses profile-based Dockerfiles/configs from `~/.opencode-wrap`.
- Includes built-in starter profiles: `default` and `dotnet`.

## Quick Start

Requirements:

- Docker (daemon running)
- .NET 10 SDK (only needed if building from source)

Run from source:

```bash
dotnet run --project src/OpencodeWrap.csproj -- --help
```

Build a standalone binary:

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
