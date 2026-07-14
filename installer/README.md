# Groundcrew MSI

`Package.wxs` creates the per-machine Windows x64 installer used by the GitHub Actions workflow.

Installed resources:

- application files: `C:\Program Files\Groundcrew`
- service: `DcsGroundcrew` (automatic start)
- persistent configuration: `C:\ProgramData\Groundcrew`
- local dashboard: `http://127.0.0.1:5080`
- launcher executable: `C:\Program Files\Groundcrew\Groundcrew.exe`
- Start Menu shortcut: `Groundcrew\Groundcrew`
- inbound firewall rule: TCP 5080, restricted to `100.64.0.0/10`

The MSI includes a setup wizard for choosing the installation directory, following progress, and displaying installation or service-start failures. Its final screen offers to launch Groundcrew immediately. It also enables verbose Windows Installer logging; automatically generated logs are written to `%TEMP%` as `MSI*.log`.

The setup wizard, installed executable, Start Menu shortcut, and Apps & Features entry use the Groundcrew brand mark from `installer/assets`.

`Groundcrew.exe` has two roles: Windows starts it with `--service` to host both the API and compiled frontend, while launching it normally ensures the service is running and opens `http://127.0.0.1:5080` in the default browser.

The application detects the active Tailscale adapter when the service starts and listens on its IPv4 address as well as localhost. Set the `ASPNETCORE_URLS` environment variable on the service if a different binding is required.

Builds currently use WiX Toolset 6.0.2. Review the WiX Open Source Maintenance Fee terms before using these installers for a revenue-generating distribution.

The MSI does not remove `C:\ProgramData\Groundcrew` during uninstall, which keeps settings available for reinstalls. Remove that directory manually when a full reset is wanted.
