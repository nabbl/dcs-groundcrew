using DcsDashboard.Api.Data;
using DcsDashboard.Api.Models;

namespace DcsDashboard.Api.Services;

public sealed class SnapshotService
{
    private readonly SettingsStore _settings;
    private readonly DcsProcessService _dcs;
    private readonly HostMetricsService _metrics;
    private readonly IntegrationService _integrations;
    private readonly DcsServerConfigurationService _serverConfiguration;
    private readonly DcsGrpcLiveService _grpc;

    public SnapshotService(SettingsStore settings, DcsProcessService dcs, HostMetricsService metrics, IntegrationService integrations, DcsServerConfigurationService serverConfiguration, DcsGrpcLiveService grpc)
    {
        _settings = settings; _dcs = dcs; _metrics = metrics; _integrations = integrations; _serverConfiguration = serverConfiguration; _grpc = grpc;
    }

    public async Task<DashboardSnapshot> GetAsync()
    {
        var settings = await _settings.GetAsync();
        var live = _grpc.GetSnapshot();
        var process = await _dcs.FindAsync();
        var running = process is not null;
        var version = "Not installed";
        if (!string.IsNullOrWhiteSpace(settings.DcsExecutablePath) && File.Exists(settings.DcsExecutablePath))
        {
            try { version = System.Diagnostics.FileVersionInfo.GetVersionInfo(settings.DcsExecutablePath).FileVersion ?? "Unknown"; } catch { version = "Unknown"; }
        }
        var configuredMission = string.IsNullOrWhiteSpace(settings.ActiveMissionPath) ? "No mission selected" : Path.GetFileNameWithoutExtension(settings.ActiveMissionPath);
        var mission = running && live.Connected && !string.IsNullOrWhiteSpace(live.MissionName) ? live.MissionName : configuredMission;
        var uptime = process is not null ? Math.Max(0, (long)(DateTimeOffset.Now - process.StartTime).TotalSeconds) : 0;
        var serverConfiguration = await _serverConfiguration.GetAsync();
        var serverName = serverConfiguration.Exists ? serverConfiguration.Name : settings.ServerName;
        var maxPlayers = serverConfiguration.Exists ? serverConfiguration.MaxPlayers : settings.MaxPlayers;
        var players = running && live.Connected ? live.Players : Array.Empty<Player>();
        var server = new ServerStatus(running ? "running" : "stopped", serverName, version, mission, "Unknown", uptime, running && live.Connected ? live.Fps ?? 0 : 0, running && live.Connected && live.Paused == true, players.Count, maxPlayers);
        return new DashboardSnapshot(false, server, _metrics.Read(process, settings.MissionLibraryPath), players, await _integrations.GetStatusesAsync(), live.Chat);
    }
}
