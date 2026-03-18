# OpencodeWrap

**The easiest way to run OpenCode in Docker.**

Persistent state. Lightweight profiles. Zero hassle.

[![npm](https://img.shields.io/npm/v/@farsight-cda/ocw?style=flat-square)](https://www.npmjs.com/package/@farsight-cda/ocw)
[![license](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE.txt)

---

## What is it?

OpencodeWrap (`ocw`) lets you run [OpenCode](https://opencode.ai) inside a Docker container while keeping all your data and settings persistent across sessions.

Think of it as a wrapper that handles all the Docker complexity for you—so you can focus on coding, not configuration.

---

## ✨ Features

- **🐳 Runs in Docker** — Isolated, reproducible environments
- **💾 Persistent State** — Your data survives between sessions
- **🎨 Profile System** — Pre-configured setups for different workflows
- **🚀 Zero Configuration** — Works out of the box
- **🔄 Auto-Updates** — Always runs the latest OpenCode release
- **🌐 Local UI Choices** — Launch the TUI or local web UI from `ocw run`
- **🖥️ Cross-Platform** — Linux, macOS, and Windows support

### Built-in Profiles

| Profile | Best For |
|---------|----------|
| `default` | General development |
| `frontend` | React, Vue, Angular apps |
| `dotnet` | .NET/C# development |
| `data-science` | Python, ML, notebooks |
| `solidity` | Blockchain/Web3 development |

---

## 🚀 Installation

**Requirements:** Docker must be installed and running.

```bash
npm i -g @farsight-cda/ocw
```

That's it! Now open a new terminal and run:

```bash
ocw --help
```

---

## 🏃 Quick Start

### 1. Run OpenCode

```bash
ocw run
```

This starts OpenCode in Docker, prompts for a UI mode, and then launches the selected local client.

### 2. Choose Your Profile, UI, and Session Extras

Use arrow keys to select a profile and UI mode, then review optional resource directories, session addons, and Docker networks. Press **Enter** to start, or press **+** to star the current profile, resource directory, session addon, or bridge network as a default for future `ocw run` sessions.

Session addons live under `~/.opencode-wrap/addons/<name>`. When enabled, each addon directory is copied into the temporary session profile before launch. File conflicts stop the launch, except `AGENTS.md`, which is merged so addon instructions are combined with the profile and runtime instructions, and the root `.env`, which is merged into the container environment in profile-then-addon order. Duplicate `.env` keys currently stop the launch.

OCW also ships built-in addons named `Question Affinity`, which adds clarification-focused `AGENTS.md` guidance, and `Web Search`, which enables the `websearch` tool by setting `OPENCODE_ENABLE_EXA=1`, without requiring host addon folders.

Available UI modes:

- `TUI` — current OCW-managed terminal client
- `Web` — opens the local browser UI and always prints the local URL
- `Desktop` — shown in the menu, but currently fails with guidance until OpenCode exposes a supported attach-to-existing-server desktop handoff

### 3. Start Coding

OpenCode is now running with persistent state. Your data is automatically saved.

---

## 📦 Common Commands

```bash
# Run OpenCode with profile selection
ocw run

# List available profiles
ocw profile list

# Create a custom profile
ocw profile add myprofile

# List available session addons
ocw addon list

# Create a custom session addon
ocw addon add "My Addon"

# Import existing OpenCode data
ocw data import-host

# Export your data for backup
ocw data export backup.ocw

# Update to the latest version
ocw update
```

---

## UI Modes

- `ocw run` asks for a UI mode on every run; there is no `--ui` flag yet.
- Backends stay bound to loopback only for the interactive `run` flow.
- Web mode auto-opens the browser and also prints the URL for manual recovery.
- If browser launch fails after the backend is healthy, OCW prints guidance and stops the session.
- If desktop mode is selected and the app is missing or cannot attach to the OCW-managed backend, OCW fails with guidance instead of falling back.
- `ocw run` requires an interactive shell because UI mode selection is prompt-only for now.

---

## 🔄 Updating

```bash
# Via npm
npm update -g @farsight-cda/ocw

# Or use the built-in command
ocw update

# Try the latest dev version
ocw update --dev
```

---

## 🛠️ Building From Source

**Requirements:** .NET 10 SDK

```bash
dotnet publish src/OpencodeWrap.csproj -c Release -r linux-x64 --self-contained true
./src/bin/Release/net10.0/linux-x64/publish/ocw --help
```

---

## 📄 License

MIT — See [LICENSE.txt](LICENSE.txt)

---

<p align="center">
  Made with ❤️ for the OpenCode community
</p>
