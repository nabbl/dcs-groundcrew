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
- `.miz` selection with an explicit restart confirmation flow
- SRS, Olympus, Tacview, SkyEye, and Digital Kneeboard install/running detection and process controls once their paths are configured
- embedded integration window with a new-tab fallback
- preview data when the frontend is run without the Windows service
- responsive desktop/tablet/mobile layout

Adapter work still required after DCS is installed:

- use the local DCS Dedicated Server WebGUI interface for current players, server FPS, mission control, chat, kick, and ban
- confirm the exact WebGUI command set against the installed DCS build before enabling mutations
- connect each integration’s configuration schema and installer/update workflow
- test iframe headers for Olympus and Digital Kneeboard; use a host-side reverse proxy or new tab where framing is blocked

The API currently returns `501 Not Implemented` for chat and moderation instead of claiming those actions succeeded. Mission selection is stored and the process is restarted, but the DCS WebGUI adapter must be completed before mission switching should be considered production-ready.

## Local UI preview

```powershell
cd src/dashboard-ui
npm install
npm run dev
```

The UI uses preview data if the backend is unavailable. Start the API separately to see actual host state:

```powershell
cd src/DcsDashboard.Api
dotnet run
```

## Windows deployment

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
