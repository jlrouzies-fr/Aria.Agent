# Idea: Launch Aria Bridge automatically on login

## Goal
Make the Aria Bridge start automatically when the user logs in, with an opt-in during install.

## Current state
The installer (`scripts/install.sh` / `scripts/install.ps1`) only downloads the binary, extracts it, and symlinks it to the user PATH. The user must run `aria-bridge` manually in a terminal.

## Proposed approach

Add an optional flag or interactive prompt to the install scripts:

```bash
# explicit opt-in
./install.sh --launch-agent

# or interactive prompt
Start aria-bridge automatically on login? [y/N]
```

### macOS — LaunchAgent

Create `~/Library/LaunchAgents/com.aria-agent.bridge.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.aria-agent.bridge</string>
    <key>ProgramArguments</key>
    <array>
        <string>/Users/USER/.local/bin/aria-bridge</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>/Users/USER/.local/lib/aria-agent/stdout.log</string>
    <key>StandardErrorPath</key>
    <string>/Users/USER/.local/lib/aria-agent/stderr.log</string>
</dict>
</plist>
```

Then load it:

```bash
launchctl load ~/Library/LaunchAgents/com.aria-agent.bridge.plist
```

### Linux — systemd user service

Create `~/.config/systemd/user/aria-bridge.service`:

```ini
[Unit]
Description=Aria Bridge

[Service]
ExecStart=%h/.local/bin/aria-bridge
Restart=on-failure

[Install]
WantedBy=default.target
```

Then:

```bash
systemctl --user daemon-reload
systemctl --user enable --now aria-bridge
```

### Windows — scheduled task

Create a task that runs at logon:

```powershell
$Action = New-ScheduledTaskAction -Execute "$env:LOCALAPPDATA\Aria\bridge\aria-bridge.exe"
$Trigger = New-ScheduledTaskTrigger -AtLogon
$Settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries
Register-ScheduledTask -TaskName "Aria Bridge" -Action $Action -Trigger $Trigger -Settings $Settings
```

## Open questions

- Should this be opt-in or opt-out?
- Should there be an uninstall script that removes the launch agent/service?
- How does the user stop/restart the background bridge? (`launchctl unload`, `systemctl --user stop`, Task Scheduler)
- Should the bridge show a tray/menu-bar icon so users know it's running?
