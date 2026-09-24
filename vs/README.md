# OpenCode for Visual Studio

A Visual Studio 2022 extension that integrates [OpenCode](https://opencode.ai) as a tool window via WebView2.

## Features

- **WebView2 Tool Window** — OpenCode web interface embedded directly in VS
- **Auto Server Detection** — Dynamically resolves `opencode serve` port from process output
- **Version Manager Support** — Finds `opencode` installed under fnm, nvm, nvm-windows, volta, pnpm or bun, even when it is not on PATH
- **Session Management** — Creates or finds sessions per project via OpenCode HTTP API
- **Workspace Isolation** — localStorage interception provides per-project data isolation
- **Project Sidebar Injection** — Injects project path into OpenCode localStorage for sidebar visibility
- **VS Theme Integration** — Syncs WebView appearance with Visual Studio color theme in real-time
- **HTTP Proxy Layer** — Intercepts `*.vsoc-app` requests and proxies to the actual OpenCode server
- **Server Controller** — Decoupled server lifecycle; survives tool window close (5-min idle timeout)
- **Smart Shutdown** — Waits for agent to finish before shutting down; cancels on tool window re-open
- **Process Binding** — OpenCode server process is automatically terminated with Visual Studio
- **Error Recovery** — Connection monitoring with styled error page and retry mechanism
- **Multi-language** — EN / zh-Hans via .NET satellite assemblies
- **Solution Auto-refresh** — Detects solution/folder changes and reconnects

## Requirements

| Component | Version |
|-----------|---------|
| Visual Studio | 2022 (17.0+) |
| OpenCode CLI | — installed (`npm i -g opencode-ai`) |
| .NET SDK | 10.0+ (for build) |

> **Note**: The extension targets .NET Framework 4.7.2 runtime, but building from source requires the .NET 10 SDK.

### Node.js version managers (fnm, nvm, volta, …)

Visual Studio is a GUI application and never runs your shell profile, so a
Node.js installed through a version manager is **not** on VS's `PATH` — even
though `opencode` works fine in your terminal. The extension does not rely on
`PATH`: it scans the on-disk layout of the common version managers instead.

| Manager   | Locations probed |
|-----------|------------------|
| fnm       | `$FNM_DIR`, `%APPDATA%\fnm`, `%LOCALAPPDATA%\fnm`, `%USERPROFILE%\.fnm`, `%XDG_DATA_HOME%\fnm` — including `aliases\default` and every `node-versions\<ver>\installation` |
| nvm       | `$NVM_DIR\versions\node\<ver>` |
| nvm-windows | `$NVM_HOME`, `%APPDATA%\nvm` — every `v<ver>` |
| volta     | `$VOLTA_HOME\bin` (default `%LOCALAPPDATA%\Volta`) |
| pnpm      | `$PNPM_HOME`, `%LOCALAPPDATA%\pnpm` |
| bun       | `%USERPROFILE%\.bun\bin` |

Plain installs are covered too: `%APPDATA%\npm`, the `prefix` from
`%USERPROFILE%\.npmrc`, `%ProgramFiles%\nodejs` and the standalone
`%USERPROFILE%\.opencode\bin`.

If your setup is unusual, point the extension at the binary directly:

```bat
setx VSOPENCODE_OPENCODE_PATH "C:\path\to\opencode.exe"
```

Restart Visual Studio afterwards. When `opencode` is only available as a
`.cmd` npm shim, the extension resolves it to the real `.exe` behind it, so the
server process can be started and terminated directly.

## Installation

1. Download the `.vsix` from [Releases](https://github.com/DreamBoxSpy/VSOpenCode/releases)
2. Close all VS instances, double-click the `.vsix`
3. Open VS → **View → Other Windows → OpenCode**

## Build

### In Visual Studio

```
Open VSOpenCode.sln → Build → F5 (experimental instance)
```

### CLI

```bash
dotnet build vs/VSOpenCode.csproj -c Release
```

The built `.vsix` is at `vs/bin/Release/net472/VSOpenCode.vsix`.

## Architecture

```
VSOpenCodePackage
├── ServerController (singleton, shared across windows)
│   ├── OpenCodeServerService     → opencode serve process lifecycle
│   │   ├── OpenCodeLocator       → binary resolution (fnm/nvm/volta/PATH)
│   │   └── ProcessBinding        → child process auto-termination
│   ├── OpenCodeSessionService    → /session, /project HTTP API
│   ├── ConnectionMonitor         → periodic health checks
│   └── 5-min idle shutdown timer
│
├── OpenCodeToolWindow            → ToolWindowPane
│   └── OpenCodeToolWindowControl → UserControl
│       ├── WebView2              → renders OpenCode web UI
│       │   └── WebView2HostBridge (COM)  → JS ↔ .NET interop
│       ├── WebResourceRequested  → HTTP proxy (vsoc-app → real server)
│       ├── ProjectRootResolver   → DTE / git root detection
│       ├── VSColorTheme          → theme sync with VS environment colors
│       ├── ErrorPage.html         → embedded resource (error display)
│       ├── LoadingPage.html        → embedded resource (loading screen)
│       └── Inject.js              → theme CSS injection + localStorage isolation + project setup
│
└── ShowOpenCodeWindowCommand     → View > Other Windows menu
```

## Project Structure

```
vs/
├── Commands/           # VS menu command registration
├── Models/             # API DTOs (Session, Project, Health, Path, Server)
├── Services/           # Server, session, connection, project resolver
│   ├── IOpenCodeServerService.cs
│   ├── IOpenCodeSessionService.cs
│   ├── IConnectionMonitor.cs
│   ├── IProjectRootResolver.cs
│   ├── ServerController.cs
│   ├── OpenCodeLocator.cs      → resolves the opencode binary (fnm/nvm/volta/…)
│   ├── OpenCodeServerService.cs
│   ├── OpenCodeSessionService.cs
│   ├── ConnectionMonitor.cs
│   ├── ProjectRootResolver.cs
│   └── ProcessBinding.cs
├── Resources/          # Embedded resources & localization
│   ├── ErrorPage.html
│   ├── LoadingPage.html
│   ├── Inject.js
│   ├── Icon.png
│   ├── Strings.resx
│   ├── Strings.zh-Hans.resx
│   └── StringsHelper.cs
├── Controls/           # WPF user controls directory
├── Properties/         # AssemblyInfo
└── VSOpenCodePackage.cs
```

## License

MIT
