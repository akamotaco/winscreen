# WinScreen

A GNU Screen-style terminal multiplexer for Windows

**[한국어](README.md)** | English

---

## Table of Contents

- [Overview](#overview)
- [Features](#features)
- [Architecture](#architecture)
- [Build](#build)
- [Usage](#usage)
- [Profiles](#profiles)
- [Comparison with GNU Screen](#comparison-with-gnu-screen)
- [Limitations](#limitations)
- [Cautions](#cautions)
- [Troubleshooting](#troubleshooting)
- [License](#license)

---

## Overview

WinScreen provides a GNU `screen`-like UX on Windows.
Create sessions, detach from them, and reattach later from a different terminal.

> **Note**: This project was developed with assistance from AI (Claude).

## Features

- **Screen-style UX**: Select sessions with `-r`, detach with `Ctrl+A, D`
- **Multi-window**: Multiple independent terminal windows in one session (`Ctrl+A, C` to create, `Ctrl+A, N/P` to switch)
- **Detached mode**: Create background sessions with `-d -m`, with command execution support
- **GNU Screen compatible shortcuts**: Support for `-dmS name`, `-dmp profile`, etc.
- **Auto window resize**: Automatic resize to current terminal size when switching windows
- **Session persistence**: Sessions survive terminal closure
- **Profile system**: Support for CMD, PowerShell, Conda, Git Bash, WSL, and more
- **Scrollback buffer**: Restore previous output on reconnection
- **Auto server start**: Server starts automatically when client runs
- **Server management**: Check server status and stop server

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        WinScreen Server                         │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐             │
│  │  Session 1  │  │  Session 2  │  │  Session 3  │             │
│  │   (ConPTY)  │  │   (ConPTY)  │  │   (ConPTY)  │             │
│  │  cmd.exe    │  │ powershell  │  │  conda env  │             │
│  └─────────────┘  └─────────────┘  └─────────────┘             │
│              │              │              │                    │
│              └──────────────┴──────────────┘                    │
│                          │                                      │
│                   Named Pipe (IPC)                              │
└─────────────────────────│───────────────────────────────────────┘
                          │
          ┌───────────────┼───────────────┐
          │               │               │
    ┌─────┴─────┐   ┌─────┴─────┐   ┌─────┴─────┐
    │ Terminal 1│   │ Terminal 2│   │ Terminal 3│
    │  (client) │   │  (client) │   │  (client) │
    └───────────┘   └───────────┘   └───────────┘
```

## Build

### Requirements

- .NET 8.0 SDK
- Windows 10 1809 or later (ConPTY support)

### Build Instructions

```batch
cd WinScreen
build.bat
```

Or manually:

```batch
dotnet publish src\WinScreen.Server -c Release -r win-x64 --self-contained -o publish\win-x64
dotnet publish src\WinScreen.Client -c Release -r win-x64 --self-contained -o publish\win-x64
```

### Installation

After building, `screen.exe` and `winscreen-server.exe` will be created in the `publish\win-x64` folder.

#### Method 1: Add to PATH (Recommended)

To use the `screen` command from anywhere:

1. **Windows Settings** → **System** → **About** → **Advanced system settings**
2. Click **Environment Variables**
3. Select `Path` in **User variables** or **System variables** and click **Edit**
4. Click **New** and enter the full path to the `publish\win-x64` folder
   - Example: `C:\Tools\WinScreen\publish\win-x64`
5. Save with **OK** and open a new terminal window

Or add to user PATH via PowerShell:

```powershell
$path = "C:\Tools\WinScreen\publish\win-x64"
[Environment]::SetEnvironmentVariable("Path", $env:Path + ";$path", "User")
```

#### Method 2: Copy to Existing PATH Location

Copy `screen.exe` and `winscreen-server.exe` to a folder already in your PATH.

## Usage

### Basic Commands

```batch
# Create new session and attach
screen

# Create session with name
screen -S mywork

# Create session with specific profile
screen -p powershell
screen -p conda

# List sessions
screen -ls

# Attach to session (by name or ID)
screen -r mywork
screen -r abc12345

# Auto-attach if only one detached session exists
screen -r

# Attach or create if no session exists (-R)
screen -R
screen -R mywork

# Force reattach when another client is connected (-d -r)
screen -d -r mywork

# Create session in background (detached mode, -d -m)
screen -d -m
screen -d -m -S daemon
screen -d -m -S build -p powershell

# Run command in background (-dmS shorthand supported)
screen -dmS myserver python server.py
screen -dm npm run build
screen -d -m -S worker python worker.py

# Kill session
screen -X kill mywork

# Kill all sessions
screen -X kill-all

# List available profiles
screen --profiles
```

### Server Management

```batch
# Check server status
screen --server

# (Re)start server (stops existing server first)
screen --server-start

# Stop server (all sessions will be terminated)
screen --server-stop
```

### -r vs -R vs -d -r Options

| Option | No session | 1 session (detached) | 1 session (attached) |
|--------|------------|----------------------|----------------------|
| `-r` | Error | Auto-attach | Error (already attached) |
| `-R` | Create new | Auto-attach | Error (already attached) |
| `-d -r` | Error | Auto-attach | Force reattach (disconnect existing client) |

### Key Bindings (While Attached)

| Key Combination | Action |
|-----------------|--------|
| `Ctrl+A, D` | Detach from session |
| `Ctrl+A, C` | Create new window |
| `Ctrl+A, K` | Kill current window |
| `Ctrl+A, N` | Next window |
| `Ctrl+A, P` | Previous window |
| `Ctrl+A, W` | List windows |
| `Ctrl+A, 0-9` | Switch to window N |
| `Ctrl+A, Shift+A` | Rename current window |
| `Ctrl+A, $` | Rename session |
| `Ctrl+A, A` | Send actual Ctrl+A |
| `Ctrl+A, ?` | Show help |

### Typical Workflow

```batch
# 1. Create work session
screen -S project1

# 2. Do your work...
# (long builds, server processes, etc.)

# 3. Detach with Ctrl+A, D
# (you can close the terminal)

# 4. Reattach later from another terminal
screen -r project1

# 5. Continue where you left off
```

### Background Sessions (Detached Mode)

Use `-d -m` to create a session and immediately detach, running it in background.
GNU Screen-style shorthands like `-dmS` are also supported.

```batch
# Create background session, attach later
screen -dmS myserver
screen -r myserver

# Run commands in background
screen -dmS build npm run build
screen -dmS worker python worker.py

# Check running command
screen -r build      # Check build progress
# Ctrl+A, D to detach again

# Run multiple background tasks
screen -dmS web python -m http.server
screen -dmS api python api_server.py
screen -dmS db docker compose up
screen -ls           # Check all sessions
```

### Multi-Window Workflow

Use multiple independent terminal windows within a single session.

```batch
# 1. Create session (first window created automatically)
screen -S mywork

# 2. Create new window (Ctrl+A, C)
# Now you have window 0 and window 1

# 3. Navigate between windows
# Ctrl+A, N → Next window
# Ctrl+A, P → Previous window
# Ctrl+A, 0 → Go to window 0
# Ctrl+A, 1 → Go to window 1

# 4. Check window list (Ctrl+A, W)
# --- Windows ---
#  *0 window-0
#   1 window-1
# ---------------

# 5. Kill current window (Ctrl+A, K or exit)
# Session ends when last window is killed

# 6. All windows preserved after detach
# Detach with Ctrl+A, D, reattach with screen -r
# All windows remain intact

# Note: When terminal size changes, switching windows
# automatically resizes to current terminal size
```

## Profiles

Profiles are stored in `%LOCALAPPDATA%\WinScreen\profiles.json`.

### Built-in Profiles

| Name | Description |
|------|-------------|
| `cmd` | Windows Command Prompt (default) |
| `powershell` | Windows PowerShell |
| `pwsh` | PowerShell Core |
| `conda` | Miniconda/Miniforge/Anaconda (auto-detected) |
| `gitbash` | Git Bash (auto-detected) |
| `wsl` | Windows Subsystem for Linux (auto-detected) |

### Auto-Detection Paths

WinScreen auto-detects profiles from the following paths on first run or when running `screen --profile-reset`:

| Profile | Detection Paths |
|---------|-----------------|
| `conda` | `%USERPROFILE%\miniconda3`, `%USERPROFILE%\miniforge3`, `%USERPROFILE%\anaconda3`, `C:\ProgramData\miniconda3`, `C:\ProgramData\miniforge3`, `C:\ProgramData\anaconda3` |
| `gitbash` | `C:\Program Files\Git\bin\bash.exe`, `C:\Program Files (x86)\Git\bin\bash.exe` |
| `wsl` | `C:\Windows\System32\wsl.exe` |

> **Note**: Auto-detection only runs when `profiles.json` doesn't exist or is reset. If profiles already exist, detection is skipped.
> For installations in other paths, add profiles manually.

### Default Profile Setting

When creating a session without specifying a profile, the default profile is used. You can change the default:

```batch
# Check current default profile
screen --default

# Change default profile
screen --set-default powershell
```

### Profile Management Commands

```batch
# List profiles
screen --profiles

# Show profile details
screen --profile-show conda

# Add new profile
screen --profile-add myenv --shell cmd.exe --startup "conda activate myenv" --desc "My Conda Environment"

# Remove profile
screen --profile-remove myenv

# Reset profiles to defaults
screen --profile-reset
```

### Profile Add Options

| Option | Description |
|--------|-------------|
| `--shell <path>` | Shell executable path (required) |
| `--args <args>` | Shell arguments |
| `--startup <cmd>` | Command to run at startup |
| `--desc <text>` | Profile description |

> **Note**: When using `--startup`, `/K` is automatically added for cmd.exe, and `-NoExit -Command` for PowerShell.
> Do NOT use `--args "/K"` together with `--startup` as it causes duplication errors.
>
> Example: `screen --profile-add myconda --shell cmd.exe --startup "conda activate myenv"`

### Editing Profiles via JSON

Edit `profiles.json` directly for more complex profiles:

```json
{
  "defaultProfile": "cmd",
  "maxScrollbackSizeKB": 2048,
  "profiles": [
    {
      "name": "cmd",
      "description": "Command Prompt",
      "shell": "cmd.exe"
    },
    {
      "name": "myenv",
      "description": "My Python Environment",
      "shell": "cmd.exe",
      "startupCommand": "conda activate myenv",
      "workingDirectory": "C:\\Projects",
      "environment": {
        "PYTHONPATH": "C:\\mylibs"
      }
    }
  ]
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `defaultProfile` | `"cmd"` | Default profile name |
| `maxScrollbackSizeKB` | `1024` | Maximum scrollback buffer size (KB) |

## Comparison with GNU Screen

### Core Features

| Feature | GNU Screen | WinScreen |
|---------|:----------:|:---------:|
| Session management | ✅ | ✅ |
| Detach/Attach | ✅ | ✅ |
| Multi-window | ✅ | ✅ |
| Scrollback buffer | ✅ | ✅ |
| Window index reuse | ✅ | ✅ |
| Rename window | ✅ | ✅ |
| Rename session | ✅ | ✅ |

### Command Compatibility

| Command | GNU Screen | WinScreen | Note |
|---------|:----------:|:---------:|------|
| `-S name` | ✅ | ✅ | Session name |
| `-r` | ✅ | ✅ | Reattach |
| `-R` | ✅ | ✅ | Attach or create |
| `-d -r` | ✅ | ✅ | Force reattach |
| `-d -m` | ✅ | ✅ | Create detached session |
| `-ls` | ✅ | ✅ | List sessions |
| `-X kill` | ✅ | ✅ | Kill session |
| `-X kill-all` | ❌ | ✅ | WinScreen only |
| `-m` | ✅ | ⚠️ | GNU: nested session, WinScreen: session switch (detach current, create new) |

### Key Binding Compatibility (After Ctrl+A)

| Key | GNU Screen | WinScreen |
|-----|:----------:|:---------:|
| `d` | ✅ | ✅ |
| `c` | ✅ | ✅ |
| `k` | ✅ | ✅ |
| `n` | ✅ | ✅ |
| `p` | ✅ | ✅ |
| `w` | ✅ | ✅ |
| `0-9` | ✅ | ✅ |
| `Shift+A` | ✅ | ✅ |
| `$` | ✅ | ✅ |
| `a` | ✅ | ✅ |
| `?` | ✅ | ✅ |

### Unsupported Features

| Feature | Description |
|---------|-------------|
| Split screen | `Ctrl+A, S` (horizontal), `Ctrl+A, |` (vertical) |
| Copy mode | `Ctrl+A, [` |
| Multi-user | Multiple users connecting simultaneously |
| Logging | `Ctrl+A, H` |
| `.screenrc` | Config file (use JSON profiles instead) |

### WinScreen-Specific Features

| Feature | Description |
|---------|-------------|
| Profile system | Shell presets for CMD, PowerShell, Conda, WSL, etc. |
| Auto server start | Server starts automatically with client |
| Auto window resize | Windows resize to terminal size when switched |
| `--server-*` commands | Server status and management |
| `-X kill-all` | Kill all sessions |

## Limitations

- Requires Windows 10 1809 or later (ConPTY API)
- Only one client can attach to a session at a time
- **No split screen**: GNU Screen's `Ctrl+a S` (horizontal), `Ctrl+a |` (vertical) not supported
  - Multi-window is supported; create windows with `Ctrl+a c` and switch with `Ctrl+a n/p`
- Some Linux screen features unavailable due to lack of process fork
- Scrollback buffer: 1MB by default (configurable)
  - Change `maxScrollbackSizeKB` in `profiles.json`
  - Only recent content restored on re-attach for long-output sessions
  - Redirect important output to files: `command > output.log`
- **ConPTY architecture difference**: GNU Screen (Linux) fully controls PTY for independent screen output, while WinScreen (Windows ConPTY) has cmd.exe tracking cursor position
  - Window list (`Ctrl+a w`), help (`Ctrl+a ?`), rename UI use alternate screen buffer
  - Some status messages (window create/kill/rename notifications) are suppressed to prevent cursor position mismatch

## Cautions

- **Session loss on server stop**: All sessions terminate when WinScreen server stops
  - Save important work regularly
  - Check sessions before `screen --server-stop`
- **Running screen inside a session**: Running `screen` inside a session detaches from the current session and switches to a new one
  - GNU Screen-style nested sessions are not supported due to ConPTY architecture
  - Dangerous commands like `screen -X kill-all`, `screen --server-stop` are blocked inside sessions
  - Query and profile management commands like `screen -ls`, `screen --profiles`, `screen --set-default` are allowed
  - Background session creation with `screen -d -m` is also allowed
- **Duplicate session names**: Warning displayed when creating session with existing name
  - Session is created but may need ID to distinguish with `-r`

## Troubleshooting

### Working Directory is Wrong When Launched from Windows Search

When launching `screen` directly from Windows Search (Win key), the working directory may be set to a system folder:

```
C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy>
```

**Cause**: When launched from Windows Search, `explorer.exe` becomes the parent process and the working directory is set unpredictably. This is a special case of launching console apps directly from search.

**Solutions**:

1. **Create a shortcut (.lnk)** (Recommended)
   - Right-click `screen.exe` → Create shortcut
   - Right-click shortcut → Properties → Set **Start in** to `%USERPROFILE%`
   - Copy shortcut to Start Menu folder: `%APPDATA%\Microsoft\Windows\Start Menu\Programs`
   - Now launching from Windows Search starts in home directory

2. **Run from cmd/terminal**
   - Running `screen` from cmd, PowerShell, Windows Terminal works correctly
   - Same pattern as most console app usage

> **Note**: Commands like `screen -ls` work normally inside sessions. WinScreen automatically adds the client path to the session's PATH.

### Server Won't Start

- Verify `winscreen-server.exe` is in the same folder
- Check server status with `screen --server`
- May require administrator privileges

### Session Exits Immediately

- Verify the profile's shell path is correct
- Check detected profiles with `screen --profiles`

### Ctrl+A Doesn't Work

Some terminals bind `Ctrl+A` to other functions like "Select All", preventing WinScreen from receiving the key input.

#### Windows Terminal

1. Open settings: `Ctrl+,` or dropdown next to tabs → Settings
2. Click **Open JSON file** at bottom left (or `Ctrl+Shift+,`)
3. Add to `actions` array:
   ```json
   { "keys": "ctrl+a", "command": "unbound" }
   ```
4. Save and restart Windows Terminal

#### Default Command Prompt (ConHost)

`Ctrl+A` is not bound to other functions in default Command Prompt, so it works normally.

#### Other Terminal Emulators

- **Cmder/ConEmu**: Settings → Keys & Macro, unbind Ctrl+A
- **Hyper**: Modify keymaps in `.hyper.js`
- **Git Bash (mintty)**: Ctrl+A is bound to readline (move to line start) by default, but works in WinScreen due to raw mode

### Output Missing During Fast Typing

- Update to latest version (stdout synchronization improved)

## License

MIT License

## References

- [Windows Pseudo Console (ConPTY)](https://devblogs.microsoft.com/commandline/windows-command-line-introducing-the-windows-pseudo-console-conpty/)
- [GNU Screen](https://www.gnu.org/software/screen/)
