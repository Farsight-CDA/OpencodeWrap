# OpencodeWrap (`ocw`)

Run [OpenCode](https://opencode.ai) V2 in Docker on Windows. The OpenCode server and every command it runs live in a container, while the TUI runs natively in your terminal. Your project folder is mounted in, and sessions, auth and settings persist between runs.

![ocw run menu](docs/images/run-menu.png)

## Requirements

- Windows x64 with an AVX2-capable CPU
- Docker Desktop running Linux containers
- Node.js / npm, for installing and updating

## Install

```powershell
npm i -g @farsight-cda/ocw
```

## Usage

```powershell
cd C:\path\to\your\project
ocw run
```

Pick a setup and press **Enter**. The current folder is mounted at `/workspace/<folder-name>`. Each run uses the latest stable OpenCode release and pins it for that session.

Entries marked ★ are your personal defaults. Entries marked ◆ come from the project's `ocw.json`, which you can commit to share the setup.

### Extra mounts

Add more host folders or Docker named volumes, read-only or read-write. They're mounted at `/resources/<name>` by default.

![Extra mounts tab](docs/images/run-mounts.png)

### Session addons

Turn [addons](#addons) on or off for this run.

![Session addons tab](docs/images/run-addons.png)

### Docker networks

Use `bridge` (the default) or `host`. `host` requires host networking to be enabled in Docker Desktop. In bridge mode you can also attach existing networks, such as your compose project's network.

![Docker networks tab](docs/images/run-networks.png)

### Docker options

Disable the workspace mount, run the container privileged, or mount the Docker daemon. Mounting the daemon gives the agent access to your host's Docker.

![Docker options tab](docs/images/run-docker.png)

## Profiles

A profile defines the container's toolchain. The built-in **`all-in-one`** profile covers frontend, .NET, Go, Rust, Postgres/data, Solidity and Ansible.

```powershell
ocw profile add <name>       # create %USERPROFILE%\.opencode-wrap\profiles\<name>
ocw profile add all-in-one   # editable copy that overrides the built-in
ocw profile build <name>     # rebuild the image without cache
ocw profile <list|open|delete>
```

```text
<name>\
├── Dockerfile      # your toolchain; must include Node.js + npm
├── bin\            # added to PATH
├── opencode\       # OpenCode config dir: opencode.json, AGENTS.md, skills, ...
├── .env            # optional, container environment variables
└── entrypoint.sh   # optional custom startup; must exec opencode2 "$@"
```

## Addons

Addons are folders that get layered on top of the profile for a single run. Use them for skills, agent instructions, MCP config or scripts you don't want baked into every profile. **frontend-design** is built in.

```powershell
ocw addon add <name>   # create %USERPROFILE%\.opencode-wrap\addons\<name>
ocw addon <list|open|delete>
```

Addons use the same layout as a profile, minus the `Dockerfile`. `opencode/opencode.json`, `opencode/AGENTS.md` and `.env` are merged. If any other file collides with the profile or another addon, the launch is blocked.


## Update

```powershell
ocw update           # install the latest release
ocw update --check   # check only
```

## License

[MIT](LICENSE.txt)
