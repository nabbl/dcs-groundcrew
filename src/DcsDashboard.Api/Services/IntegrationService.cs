using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using DcsDashboard.Api.Data;
using DcsDashboard.Api.Models;

namespace DcsDashboard.Api.Services;

public sealed class IntegrationService
{
    private readonly SettingsStore _store;
    private readonly DcsGrpcLiveService _grpc;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentDictionary<string, RemoteProbe> _remoteProbes = new(StringComparer.OrdinalIgnoreCase);
    public IntegrationService(SettingsStore store, DcsGrpcLiveService grpc, IHttpClientFactory httpClientFactory)
    {
        _store = store;
        _grpc = grpc;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IReadOnlyList<IntegrationStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _store.GetAsync();
        var listeningPorts = OperatingSystem.IsWindows()
            ? IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet()
            : new HashSet<int>();
        var statuses = new List<IntegrationStatus>();
        foreach (var item in settings.Integrations)
        {
            if (string.Equals(item.Id, "grpc", StringComparison.OrdinalIgnoreCase))
            {
                var grpcRoot = string.IsNullOrWhiteSpace(settings.SavedGamesPath) ? "" : Path.Combine(settings.SavedGamesPath, "Scripts", "DCS-gRPC");
                var grpcInstalled = !string.IsNullOrWhiteSpace(settings.SavedGamesPath)
                    && File.Exists(Path.Combine(grpcRoot, "grpc-mission.lua"))
                    && File.Exists(Path.Combine(settings.SavedGamesPath, "Mods", "tech", "DCS-gRPC", "dcs_grpc.dll"))
                    && File.Exists(Path.Combine(settings.SavedGamesPath, "Scripts", "Hooks", "DCS-gRPC.lua"));
                var grpcVersion = ReadGrpcVersion(Path.Combine(grpcRoot, "version.lua"));
                var live = _grpc.GetSnapshot();
                statuses.Add(new IntegrationStatus(item.Id, item.Name, item.Description, item.Kind, grpcInstalled, live.Connected, live.Version ?? grpcVersion, null, true));
                continue;
            }
            if (string.Equals(item.Id, "skyeye", StringComparison.OrdinalIgnoreCase) && item.Remote)
            {
                var remoteUrl = NormalizeRemoteUrl(item.Url);
                var remoteRunning = remoteUrl is not null && await ProbeRemoteAsync(item.Id, remoteUrl, cancellationToken);
                statuses.Add(new IntegrationStatus(item.Id, item.Name, item.Description, item.Kind, remoteUrl is not null, remoteRunning, null, remoteUrl, true));
                continue;
            }
            var executableInstalled = !string.IsNullOrWhiteSpace(item.ExecutablePath) && File.Exists(item.ExecutablePath);
            var configInstalled = !string.IsNullOrWhiteSpace(item.ConfigPath) && File.Exists(item.ConfigPath);
            var portListening = item.Port is > 0 && listeningPorts.Contains(item.Port.Value);
            var webConfigured = item.Kind == "web" && !string.IsNullOrWhiteSpace(item.Url);
            var tacviewConfigured = item.Id == "tacview" && !string.IsNullOrWhiteSpace(settings.TacviewRecordingsPath) && Directory.Exists(settings.TacviewRecordingsPath);
            var installed = executableInstalled || configInstalled || portListening || webConfigured || tacviewConfigured;
            var process = executableInstalled ? Process.GetProcessesByName(Path.GetFileNameWithoutExtension(item.ExecutablePath)).FirstOrDefault() : null;
            string? version = null;
            try { version = executableInstalled ? FileVersionInfo.GetVersionInfo(item.ExecutablePath).FileVersion : null; } catch { }
            var url = string.Equals(item.Id, "skyeye", StringComparison.OrdinalIgnoreCase) ? null : item.Url;
            statuses.Add(new IntegrationStatus(item.Id, item.Name, item.Description, item.Kind, installed, process is not null || portListening, version, url, true));
        }
        return statuses;
    }

    private async Task<bool> ProbeRemoteAsync(string id, string url, CancellationToken cancellationToken)
    {
        if (_remoteProbes.TryGetValue(id, out var cached)
            && string.Equals(cached.Url, url, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromSeconds(15)) return cached.Running;

        var running = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await _httpClientFactory.CreateClient("integration-health")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            running = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { }
        _remoteProbes[id] = new RemoteProbe(url, running, DateTimeOffset.UtcNow);
        return running;
    }

    private static string? NormalizeRemoteUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        return uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }

    private sealed record RemoteProbe(string Url, bool Running, DateTimeOffset CheckedAt);

    private static string? ReadGrpcVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var content = File.ReadAllText(path);
            var marker = "GRPC.version";
            var markerIndex = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return null;
            var quoteStart = content.IndexOf('"', markerIndex + marker.Length);
            var quoteEnd = quoteStart < 0 ? -1 : content.IndexOf('"', quoteStart + 1);
            return quoteStart >= 0 && quoteEnd > quoteStart ? content[(quoteStart + 1)..quoteEnd] : null;
        }
        catch { return null; }
    }

    public async Task ControlAsync(string id, string action)
    {
        var settings = await _store.GetAsync();
        var item = settings.Integrations.SingleOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Unknown integration '{id}'.");
        if (string.Equals(item.Id, "skyeye", StringComparison.OrdinalIgnoreCase) && item.Remote)
            throw new InvalidOperationException("Remote SkyEye instances cannot be started or restarted by Groundcrew.");
        if (string.IsNullOrWhiteSpace(item.ExecutablePath) || !File.Exists(item.ExecutablePath))
            throw new InvalidOperationException($"{item.Name} is not configured or installed.");
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(item.ExecutablePath));
        if (action is "stop" or "restart") foreach (var process in processes) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        if (action is "start" or "restart") Process.Start(new ProcessStartInfo(item.ExecutablePath, item.Arguments) { WorkingDirectory = Path.GetDirectoryName(item.ExecutablePath)!, UseShellExecute = false });
    }
}
