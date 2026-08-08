# OpencodeWrap

**Run OpenCode V2 in Docker—no config needed.**

Your data persists. Your settings follow you. Just type `ocw run`.

---

## Highlights

- **Zero setup** — One npm install, then just run
- **Your data stays** — Everything persists across sessions
- **Smart profiles** — Built-in all-in-one setup plus custom profiles
- **Session addons** — Drop in custom tools and configurations
- **Version controlled** — OCW pins matching `opencode2` server and client artifacts
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

OCW disables OpenCode's own updater so its server and client stay on the same pinned version. A session-local loopback bridge maps host paths to their container mounts, including on Windows. The host tab strip is scoped to each run; backend sessions remain persistent and can be reopened with `/sessions`. V2 state uses the fresh `opencode-wrap-xdg-v2` Docker volume; OCW does not migrate or back up V1 state.

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
- `AGENTS.md`, root `.env`, and `opencode/opencode.json` are merged across the profile and active addons
- Built-in addons include **web-search** (enable Exa search) and **frontend-design** (installs the frontend design skill)

---

## License

MIT

---

<p align="center">Built for the OpenCode community</p>
