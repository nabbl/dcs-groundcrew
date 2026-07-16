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
    private readonly InteractiveProcessLauncher _launcher;
    private readonly ILogger<IntegrationService> _logger;
    private readonly ConcurrentDictionary<string, RemoteProbe> _remoteProbes = new(StringComparer.OrdinalIgnoreCase);
    public IntegrationService(SettingsStore store, DcsGrpcLiveService grpc, InteractiveProcessLauncher launcher, ILogger<IntegrationService> logger)
    {
        _store = store;
        _grpc = grpc;
        _launcher = launcher;
        _logger = logger;
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
                var remoteHost = ResolveRemoteHost(item);
                var remoteUrl = NormalizeRemoteUrl(item.Url);
                var remoteRunning = remoteHost is not null && await ProbeRemoteAsync(item.Id, remoteHost, cancellationToken);
                statuses.Add(new IntegrationStatus(item.Id, item.Name, item.Description, item.Kind, remoteHost is not null, remoteRunning, null, remoteUrl, true));
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

    private async Task<bool> ProbeRemoteAsync(string id, string host, CancellationToken cancellationToken)
    {
        if (_remoteProbes.TryGetValue(id, out var cached)
            && string.Equals(cached.Host, host, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromSeconds(15)) return cached.Running;

        var running = false;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 2_000).WaitAsync(cancellationToken);
            running = reply.Status == IPStatus.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { }
        _remoteProbes[id] = new RemoteProbe(host, running, DateTimeOffset.UtcNow);
        return running;
    }

    private static string? ResolveRemoteHost(IntegrationSettings item)
    {
        var configured = NormalizeRemoteHost(item.RemoteHost);
        if (configured is not null) return configured;
        var legacyUrl = NormalizeRemoteUrl(item.Url);
        return legacyUrl is not null && Uri.TryCreate(legacyUrl, UriKind.Absolute, out var uri) ? uri.DnsSafeHost : null;
    }

    private static string? NormalizeRemoteHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var host = value.Trim();
        if (host.Length > 253 || host.Any(char.IsWhiteSpace) || host.Contains('/') || host.Contains('\\')) return null;
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        return host.Length > 0 ? host : null;
    }

    private static string? NormalizeRemoteUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        return uri.Scheme is "http" or "https" ? uri.ToString() : null;
    }

    private sealed record RemoteProbe(string Host, bool Running, DateTimeOffset CheckedAt);

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
        if (string.Equals(item.Id, "olympus", StringComparison.OrdinalIgnoreCase))
        {
            if (!item.Enabled) throw new InvalidOperationException("Enable the Olympus integration before controlling it.");
            if (action is "start") await StartOlympusAsync(item);
            else if (action is "restart")
            {
                await StopOlympusAsync(item);
                await StartOlympusAsync(item);
            }
            else await StopOlympusAsync(item);
            return;
        }
        if (string.IsNullOrWhiteSpace(item.ExecutablePath) || !File.Exists(item.ExecutablePath))
            throw new InvalidOperationException($"{item.Name} is not configured or installed.");
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(item.ExecutablePath));
        if (action is "stop" or "restart") foreach (var process in processes) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        if (action is "start" or "restart") Process.Start(new ProcessStartInfo(item.ExecutablePath, item.Arguments) { WorkingDirectory = Path.GetDirectoryName(item.ExecutablePath)!, UseShellExecute = false });
    }

    public async Task StartConfiguredWithDcsAsync()
    {
        var settings = await _store.GetAsync();
        var olympus = settings.Integrations.FirstOrDefault(item => string.Equals(item.Id, "olympus", StringComparison.OrdinalIgnoreCase));
        if (olympus is null || !olympus.Enabled || !olympus.StartWithDcs) return;

        try
        {
            if (IsPortListening(olympus.Port))
            {
                _logger.LogInformation("DCS Olympus is already listening on port {Port}; automatic launch was skipped.", olympus.Port);
                return;
            }

            await StartOlympusAsync(olympus);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DCS Olympus could not be started automatically. DCS startup will continue.");
        }
    }

    private async Task StartOlympusAsync(IntegrationSettings item)
    {
        if (IsPortListening(item.Port)) return;
        if (string.IsNullOrWhiteSpace(item.ExecutablePath) || !File.Exists(item.ExecutablePath))
            throw new InvalidOperationException("The Olympus server launcher was not found. Select server.vbs in the Olympus configuration.");
        if (string.IsNullOrWhiteSpace(item.ConfigPath) || !File.Exists(item.ConfigPath))
            throw new InvalidOperationException("The Olympus instance configuration was not found. Select Config\\olympus.json in the Olympus configuration.");

        var launcherPath = item.ExecutablePath;
        var workingDirectory = Path.GetDirectoryName(launcherPath)!;
        InteractiveLaunchResult launched;
        if (string.Equals(Path.GetExtension(launcherPath), ".vbs", StringComparison.OrdinalIgnoreCase))
        {
            var cscript = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cscript.exe");
            if (!File.Exists(cscript)) cscript = "cscript.exe";
            var arguments = $"//Nologo {QuoteArgument(launcherPath)} {QuoteArgument(item.ConfigPath)}";
            launched = _launcher.Start(cscript, arguments, workingDirectory, "DCS Olympus");
        }
        else
        {
            launched = _launcher.Start(launcherPath, item.Arguments, workingDirectory, "DCS Olympus");
        }

        _logger.LogInformation(
            "Started DCS Olympus launcher {ProcessId} in Windows session {SessionId}. Launcher: {Launcher}; config: {Config}",
            launched.Process.Id,
            launched.SessionId,
            launcherPath,
            item.ConfigPath);

        for (var attempt = 0; attempt < 20 && !IsPortListening(item.Port); attempt++)
            await Task.Delay(500);
        if (!IsPortListening(item.Port))
            _logger.LogWarning("DCS Olympus was launched but did not begin listening on port {Port} within 10 seconds.", item.Port);
    }

    private async Task StopOlympusAsync(IntegrationSettings item)
    {
        if (item.Port is not > 0) return;
        var process = await FindNodeProcessOnPortAsync(item.Port.Value);
        if (process is null)
        {
            if (IsPortListening(item.Port))
                throw new InvalidOperationException($"Port {item.Port} is in use, but Groundcrew will not stop it because it is not an Olympus Node.js process.");
            return;
        }
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        _logger.LogInformation("Stopped DCS Olympus Node.js process {ProcessId} on port {Port}.", process.Id, item.Port);
    }

    private static async Task<Process?> FindNodeProcessOnPortAsync(int port)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var netstat = Process.Start(new ProcessStartInfo("netstat.exe", "-ano -p tcp")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (netstat is null) return null;
            var output = await netstat.StandardOutput.ReadToEndAsync();
            await netstat.WaitForExitAsync();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length < 5 || !string.Equals(columns[3], "LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                var separator = columns[1].LastIndexOf(':');
                if (separator < 0 || !int.TryParse(columns[1][(separator + 1)..], out var localPort) || localPort != port) continue;
                if (!int.TryParse(columns[4], out var processId)) continue;
                var process = Process.GetProcessById(processId);
                return string.Equals(process.ProcessName, "node", StringComparison.OrdinalIgnoreCase) ? process : null;
            }
        }
        catch { }
        return null;
    }

    private static bool IsPortListening(int? port)
    {
        if (port is not > 0) return false;
        try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port.Value); }
        catch { return false; }
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
