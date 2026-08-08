# OpencodeWrap

**Run OpenCode V2 in Docker—no config needed.**

Your backend sessions and server data persist. Just type `ocw run`.

---

## Highlights

- **Zero setup** — One npm install, then just run
- **Your data stays** — Backend sessions and server state persist across runs
- **Smart profiles** — Built-in all-in-one setup plus custom profiles
- **Session addons** — Drop in custom tools and configurations
- **Auto-updates** — Resolves the current OpenCode V2 release when each run starts
- **Terminal UI** — Connect a local V2 TUI to the containerized backend
- **Works everywhere** — Linux, macOS, Windows

---

## Install

```bash
npm i -g @farsight-cda/ocw
```

Docker required.

---

## Quick Start

```bash
ocw run
```

Choose a profile and start coding with the built-in all-in-one profile or your own custom one.

OCW resolves the current `@opencode-ai/cli@next` release when a run starts, then disables OpenCode's own updater for that session so its server and client stay on the same resolved version. A session-local loopback bridge maps host paths to their container mounts, including on Windows. The host tab strip is scoped to each run; backend sessions remain persistent and can be reopened with `/sessions`. V2 state uses the fresh `opencode-wrap-xdg-v2` Docker volume; OCW does not automatically import the old V1 volume.

---

## Built-in Profiles

| Profile | Best For |
|---------|----------|
| `all-in-one` | Combined frontend, .NET, Go, Rust, Postgres/data, and Solidity tooling |

Create your own with `ocw profile add <name>`.

---

## Session Addons

Enhance your sessions with custom configurations:

- Drop addon folders in `~/.opencode-wrap/addons/`
- Enable them when running `ocw run`
- `opencode/AGENTS.md`, root `.env`, and `opencode/opencode.json` are merged across the profile and active addons
- Built-in addons include **frontend-design**, which installs the frontend design skill
- A custom root `entrypoint.sh` can override profile startup when needed

---

## License

MIT

---

<p align="center">Built for the OpenCode community</p>
