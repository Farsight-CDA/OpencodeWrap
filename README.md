# OpencodeWrap

**Run OpenCode in Docker—no config needed.**

Your data persists. Your settings follow you. Just type `ocw run`.

---

## Highlights

- **Zero setup** — One npm install, then just run
- **Your data stays** — Everything persists across sessions
- **Smart profiles** — Pre-made setups for frontend, .NET, data science, and more
- **Session addons** — Drop in custom tools and configurations
- **Auto-updates** — Always on the latest OpenCode
- **Your choice of UI** — TUI or web browser
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

Pick a profile, choose your UI, start coding.

![Run Setup](docs/images/run-setup.png)

---

## Built-in Profiles

| Profile | Best For |
|---------|----------|
| `default` | General development |
| `frontend` | React, Vue, Angular |
| `dotnet` | .NET/C# projects |
| `data-science` | Python, ML, notebooks |
| `all-in-one` | Combined frontend, .NET, data, and Solidity tooling |
| `solidity` | Blockchain/Web3 |

Create your own with `ocw profile add <name>`.

---

## Session Addons

Enhance your sessions with custom configurations:

- Drop addon folders in `~/.opencode-wrap/addons/`
- Enable them when running `ocw run`
- `AGENTS.md`, root `.env`, and `opencode/opencode.json` are merged across the profile and active addons
- Built-in addons include **Question Affinity** (AGENTS.md behavior instructions) and **Web Search** (enable Exa search)

---

## License

MIT

---

<p align="center">Built for the OpenCode community</p>
