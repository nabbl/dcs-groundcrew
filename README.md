# Groundcrew

A browser-based control plane for one Windows DCS Dedicated Server host. The visual language takes cues from the official DCS launcher: a compact navigation rail, charcoal operational surfaces, amber control accents, and a persistent instance-control column.

## Current milestone

The repository contains a working React/TypeScript interface and an ASP.NET Core Windows control service.

Working now:

- DCS process discovery plus start, stop, and restart controls
- live SignalR snapshots with polling fallback
- host CPU, memory, disk, DCS working-set, DCS CPU, and process uptime collection
- SQLite-backed host configuration
- server-side Windows file browser for executables, Saved Games, missions, and Tacview recordings
- recursive `.miz` discovery from the mission-library folder configured in Settings, with an explicit restart confirmation flow
- cached, read-only `.miz` readiness summaries for theatre, date/time, weather, static flyable slots, declared dependencies, recognized script frameworks, archive integrity, and server-cap mismatches
- best-effort mission parsing that skips unfamiliar fields and optional sections independently; even a completely unreadable mission produces a contained report without affecting the dashboard or later inspections
- GUI editing for common `Config\serverSettings.lua` options, including server identity, connection cap, port, mission lifecycle, integrity checks, exports, voice chat, and player permissions
- password-safe, surgical Lua updates that preserve mission lists and unknown settings and create a timestamped backup before writing
- working per-integration configuration for executable/config paths, URLs, and network endpoints
- managed DCS-gRPC installation, update, and repair from the official GitHub release, including release/ZIP validation, Saved Games deployment, `MissionScripting.lua` wiring, autostart configuration, version/port status, rollback, and timestamped backups
- resilient DCS-gRPC live-data adapter for health/version, active mission, pause state, mission time, simulation FPS, connected players, ping/coalition/airframe details, incoming chat, and outgoing administrator chat
- DCS-gRPC moderation controls for kick, temporary ban, and move-to-spectators, with confirmation, operator reasons, failure reporting, and a persistent SQLite audit trail
- process and local-port status detection for SRS, Olympus, Tacview, and SkyEye, plus launch/restart controls where an executable is configured
- Digital Kneeboard Simulator launch into its hosted sign-in page
- embedded integration window with a new-tab fallback
- preview data when the frontend is run without the Windows service
- responsive desktop/tablet/mobile layout

Adapter work still required after DCS is installed:

- use DCS-gRPC hook operations for mission loading and lifecycle control instead of process-level fallbacks where possible
- add ban-list review/unban controls and richer moderation-audit filtering
- add installer/update workflows for the remaining third-party tools and, where useful, direct editing of each tool's native configuration file
- test iframe headers for Olympus and Digital Kneeboard; use a host-side reverse proxy or new tab where framing is blocked

Windows process discovery remains authoritative for whether DCS is running and for process uptime; executable metadata, performance counters, files, and Lua configuration remain the primary sources for host and server facts. DCS-gRPC only enriches runtime fields that Groundcrew cannot read reliably from those direct sources, such as live players, mission FPS, pause state, and chat. Every read RPC is best-effort and cached outside the dashboard snapshot loop, so a missing field or interrupted event stream cannot break the UI. Mission selection is stored and the process is restarted; replacing that fallback with DCS-gRPC's hook-level mission loading is the next control-plane step.

The server configuration page's **Maximum players** value is DCS's global connection cap. Groundcrew can summarize static Client and Player slots from each `.miz`, but changing those slots remains a DCS Mission Editor task. Runtime scripts may create behavior that cannot be determined safely without executing mission code, so Groundcrew reports recognized frameworks without trying to emulate Olympus or DCS itself.

### DCS-gRPC installer

Configure both the **DCS executable** and **Saved Games** paths under Settings, then open Integrations → DCS-gRPC. Groundcrew downloads the exact ZIP attached to the latest release of `DCS-gRPC/rust-server`, limits and validates the archive, and installs the expected package files into the configured Saved Games folder. It backs up and patches the DCS installation's `Scripts\MissionScripting.lua`, enables `autostart` in `Config\dcs-grpc.lua`, and restarts DCS only when it was running before the install.

Existing package files and Lua files are retained under `Saved Games\Groundcrew Backups\DCS-gRPC`. Unknown `dcs-grpc.lua` settings are preserved. If Groundcrew cannot identify the official loader anchor or any installation step fails, it restores the previous files instead of guessing. Upstream currently publishes no checksum or code signature alongside the release; Groundcrew restricts downloads to the official GitHub repository and reports the SHA-256 it computes after download, but that hash is an installation record rather than independent publisher verification.

## Local UI preview

```powershell
cd src/dashboard-ui
npm install
npm run dev
```

The UI uses preview data if the backend is unavailable. Start the API separately to see actual host state:

```powershell
cd src/DcsDashboard.Api
dotnet run -- --service
```

## Windows deployment

### MSI installer

Every push to `main`, pull request, and manual workflow run builds a Windows x64 MSI in GitHub Actions. Open the workflow run named **Build Windows installer** and download the `groundcrew-windows-installer-*` artifact.

The MSI:

- shows a standard setup wizard with installation progress and visible error messages
- installs Groundcrew under `C:\Program Files\Groundcrew`
- registers and starts the `DcsGroundcrew` Windows service
- installs `Groundcrew.exe`; double-clicking it starts the service when necessary and opens the dashboard
- adds a **Groundcrew** Start Menu shortcut that launches the executable
- offers to open Groundcrew immediately on the final setup screen
- listens on `http://127.0.0.1:5080` and, when Tailscale is active, its Tailscale IPv4 address on port 5080
- limits the inbound firewall exception to Tailscale peers (`100.64.0.0/10`)
- stores the SQLite database under `C:\ProgramData\Groundcrew` so upgrades preserve the configuration

The generated MSI is currently unsigned, so Windows may display an unknown-publisher warning. Production signing can be added once a code-signing certificate is available.

If setup fails, it now creates a verbose `MSI*.log` file in `%TEMP%`. You can also choose an explicit log path from an elevated PowerShell window:

```powershell
msiexec.exe /i ".\Groundcrew-<version>-win-x64.msi" /L*V ".\Groundcrew-install.log"
```

### Manual service deployment

Run PowerShell as Administrator on the DCS host:

```powershell
.\scripts\publish-windows.ps1
.\scripts\install-windows-service.ps1 `
  -InstallDirectory "C:\DcsGroundcrew" `
  -TailscaleIp "100.x.x.x"
```

Copy the contents of `artifacts\windows-x64` to `C:\DcsGroundcrew` before running the service installer. The installer binds the service to the specified Tailscale address and creates a firewall rule limited to Tailscale’s `100.64.0.0/10` address range.

There is intentionally no dashboard login in this milestone. Do not bind the service to `0.0.0.0` or expose port 5080 directly to the internet. DCS account credentials are independent from dashboard access and do not protect this application.

## Project layout

```text
src/dashboard-ui/       React + TypeScript browser application
src/DcsDashboard.Api/  Windows process, metrics, files, SQLite, and SignalR service
scripts/                Windows publish and service-install scripts
```
