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

This starts OpenCode in Docker and launches the TUI.

### 2. Choose Your Profile

Use arrow keys to select a profile, press **Enter** to start, or press **+** to star the current profile, resource directory, or bridge network as a default for future `ocw run` sessions.

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

# Import existing OpenCode data
ocw data import-host

# Export your data for backup
ocw data export backup.ocw

# Update to the latest version
ocw update
```

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
