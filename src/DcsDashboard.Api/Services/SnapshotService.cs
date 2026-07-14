using DcsDashboard.Api.Data;
using DcsDashboard.Api.Models;

namespace DcsDashboard.Api.Services;

public sealed class SnapshotService
{
    private readonly SettingsStore _settings;
    private readonly DcsProcessService _dcs;
    private readonly HostMetricsService _metrics;
    private readonly IntegrationService _integrations;

    public SnapshotService(SettingsStore settings, DcsProcessService dcs, HostMetricsService metrics, IntegrationService integrations)
    {
        _settings = settings; _dcs = dcs; _metrics = metrics; _integrations = integrations;
    }

    public async Task<DashboardSnapshot> GetAsync()
    {
        var settings = await _settings.GetAsync();
        var process = await _dcs.FindAsync();
        var running = process is not null;
        var version = "Not installed";
        if (!string.IsNullOrWhiteSpace(settings.DcsExecutablePath) && File.Exists(settings.DcsExecutablePath))
        {
            try { version = System.Diagnostics.FileVersionInfo.GetVersionInfo(settings.DcsExecutablePath).FileVersion ?? "Unknown"; } catch { version = "Unknown"; }
        }
        var mission = string.IsNullOrWhiteSpace(settings.ActiveMissionPath) ? "No mission selected" : Path.GetFileNameWithoutExtension(settings.ActiveMissionPath);
        var uptime = running ? Math.Max(0, (long)(DateTimeOffset.Now - process!.StartTime).TotalSeconds) : 0;
        var server = new ServerStatus(running ? "running" : "stopped", settings.ServerName, version, mission, "Unknown", uptime, 0, 0, settings.MaxPlayers);
        return new DashboardSnapshot(false, server, _metrics.Read(process, settings.MissionLibraryPath), Array.Empty<Player>(), await _integrations.GetStatusesAsync(), Array.Empty<ChatMessage>());
    }
}

