using System.Diagnostics;
using System.Text.RegularExpressions;
using DcsDashboard.Api.Data;
using DcsDashboard.Api.Models;

namespace DcsDashboard.Api.Services;

public sealed partial class DcsUpdateService : BackgroundService
{
    public const string ReleasePage = "https://updates.digitalcombatsimulator.com/";

    private readonly SettingsStore _settings;
    private readonly DcsProcessService _dcs;
    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<DcsUpdateService> _logger;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly object _statusGate = new();
    private DcsUpdateStatus _status = new(null, null, false, false, false, false, null, ReleasePage, null, null, null);

    public DcsUpdateService(SettingsStore settings, DcsProcessService dcs, IHttpClientFactory httpClients, ILogger<DcsUpdateService> logger)
    {
        _settings = settings;
        _dcs = dcs;
        _httpClients = httpClients;
        _logger = logger;
    }

    public async Task<DcsUpdateStatus> GetStatusAsync()
    {
        var installation = await InspectInstallationAsync();
        lock (_statusGate)
        {
            _status = _status with
            {
                InstalledVersion = installation.Version,
                UpdaterPath = installation.UpdaterPath,
                CanUpdate = installation.CanUpdate,
                UpdateAvailable = IsNewer(_status.LatestVersion, installation.Version)
            };
            return _status;
        }
    }

    public async Task<DcsUpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        await _checkGate.WaitAsync(cancellationToken);
        try
        {
            SetStatus(current => current with { IsChecking = true, Error = null, Message = null });
            var installation = await InspectInstallationAsync();
            try
            {
                var html = await _httpClients.CreateClient("dcs-updates").GetStringAsync(ReleasePage, cancellationToken);
                var latest = ParseLatestVersion(html) ?? throw new InvalidDataException("The official DCS updates page did not contain a recognizable stable version.");
                SetStatus(current => current with
                {
                    InstalledVersion = installation.Version,
                    LatestVersion = latest,
                    UpdateAvailable = IsNewer(latest, installation.Version),
                    CanUpdate = installation.CanUpdate,
                    IsChecking = false,
                    LastCheckedAt = DateTimeOffset.UtcNow,
                    UpdaterPath = installation.UpdaterPath,
                    Message = installation.Version is null ? "Select DCS.exe in Settings to compare the installed version." : IsNewer(latest, installation.Version) ? $"DCS {latest} is available." : "DCS is up to date.",
                    Error = null
                });
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
            {
                _logger.LogWarning(exception, "Could not check the official DCS release page.");
                SetStatus(current => current with
                {
                    InstalledVersion = installation.Version,
                    CanUpdate = installation.CanUpdate,
                    IsChecking = false,
                    LastCheckedAt = DateTimeOffset.UtcNow,
                    UpdaterPath = installation.UpdaterPath,
                    Error = $"Update check unavailable: {exception.Message}"
                });
            }

            return await GetStatusAsync();
        }
        finally
        {
            SetStatus(current => current with { IsChecking = false });
            _checkGate.Release();
        }
    }

    public async Task<DcsUpdateStatus> BeginUpdateAsync()
    {
        await _updateGate.WaitAsync();
        try
        {
            var current = await GetStatusAsync();
            if (current.IsUpdating) return current;
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("DCS updates can only be applied by Groundcrew on the Windows server host.");
            if (!current.UpdateAvailable) throw new InvalidOperationException("Groundcrew has not detected a newer DCS version to install.");
            if (string.IsNullOrWhiteSpace(current.UpdaterPath) || !File.Exists(current.UpdaterPath))
                throw new InvalidOperationException("DCS_updater.exe was not found. Select the DCS.exe path in Settings first.");

            SetStatus(status => status with { IsUpdating = true, Error = null, Message = "Preparing the DCS update…" });
            _ = Task.Run(() => ApplyUpdateAsync(StoppingToken));
            return await GetStatusAsync();
        }
        finally { _updateGate.Release(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StoppingToken = stoppingToken;
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { _logger.LogWarning(exception, "The scheduled DCS update check failed."); }

            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private CancellationToken StoppingToken { get; set; }

    private async Task ApplyUpdateAsync(CancellationToken stoppingToken)
    {
        var wasRunning = false;
        string? beforeVersion = null;
        try
        {
            var installation = await InspectInstallationAsync();
            if (installation.ExecutablePath is null || installation.UpdaterPath is null)
                throw new InvalidOperationException("The configured DCS installation is incomplete.");

            beforeVersion = installation.Version;
            wasRunning = await _dcs.FindAsync() is not null;
            if (wasRunning)
            {
                SetStatus(status => status with { Message = "Stopping the DCS server…" });
                await _dcs.StopAsync();
            }

            SetStatus(status => status with { Message = "DCS_updater is downloading and installing the update…" });
            var output = await RunUpdaterAsync(installation.UpdaterPath, installation.InstallRoot!, stoppingToken);
            var updated = await InspectInstallationAsync();
            var changed = CompareVersions(updated.Version, beforeVersion) > 0;
            var knownLatest = GetStatusSnapshot().LatestVersion;
            var reachedLatest = updated.Version is not null && knownLatest is not null && CompareVersions(updated.Version, knownLatest) >= 0;
            if (!changed && !reachedLatest)
                throw new InvalidOperationException($"The updater finished, but DCS still reports version {updated.Version ?? "unknown"}. {Tail(output)}".Trim());

            SetStatus(status => status with
            {
                InstalledVersion = updated.Version,
                UpdateAvailable = IsNewer(status.LatestVersion, updated.Version),
                Message = changed ? $"DCS was updated to {updated.Version}." : "DCS was already up to date.",
                Error = null
            });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            SetStatus(status => status with { Error = "The update was interrupted because Groundcrew is shutting down." });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "DCS update failed.");
            SetStatus(status => status with { Error = $"DCS update failed: {exception.Message}", Message = null });
        }
        finally
        {
            if (wasRunning)
            {
                try
                {
                    SetStatus(status => status with { Message = status.Error is null ? "Restarting the DCS server…" : "Update failed; attempting to restore the running server…" });
                    await _dcs.StartAsync();
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Could not restart DCS after its updater exited.");
                    SetStatus(status => status with { Error = $"{status.Error ?? "The update completed, but"} DCS could not be restarted: {exception.Message}", Message = null });
                }
            }

            var installation = await InspectInstallationAsync();
            SetStatus(status => status with
            {
                InstalledVersion = installation.Version,
                UpdateAvailable = IsNewer(status.LatestVersion, installation.Version),
                IsUpdating = false,
                Message = status.Error is null ? (wasRunning ? $"DCS {installation.Version ?? "update"} is installed and the server was restarted." : $"DCS {installation.Version ?? "update"} is installed.") : status.Message
            });
        }
    }

    private static async Task<string> RunUpdaterAsync(string updaterPath, string installRoot, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(updaterPath, "--quiet update")
        {
            WorkingDirectory = installRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start DCS_updater.exe.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var output = $"{await standardOutput}\n{await standardError}".Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException($"DCS_updater.exe exited with code {process.ExitCode}. {Tail(output)}".Trim());
        return output;
    }

    private async Task<Installation> InspectInstallationAsync()
    {
        var settings = await _settings.GetAsync();
        string? executable;
        try { executable = string.IsNullOrWhiteSpace(settings.DcsExecutablePath) ? null : Path.GetFullPath(settings.DcsExecutablePath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(null, null, null, null, false);
        }
        if (executable is null || !File.Exists(executable)) return new(executable, null, null, null, false);

        var binDirectory = Path.GetDirectoryName(executable)!;
        var installRoot = Directory.GetParent(binDirectory)?.FullName;
        var candidates = new[]
        {
            Path.Combine(binDirectory, "DCS_updater.exe"),
            installRoot is null ? null : Path.Combine(installRoot, "bin", "DCS_updater.exe"),
            installRoot is null ? null : Path.Combine(installRoot, "DCS_updater.exe")
        };
        var updater = candidates.FirstOrDefault(path => path is not null && File.Exists(path));
        return new(executable, installRoot, updater, ReadVersion(executable), OperatingSystem.IsWindows() && updater is not null);
    }

    internal static string? ParseLatestVersion(string html)
    {
        var match = LatestStableVersionRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static bool IsNewer(string? latest, string? installed) => !string.IsNullOrWhiteSpace(latest) && !string.IsNullOrWhiteSpace(installed) && CompareVersions(latest, installed) > 0;

    internal static int CompareVersions(string? left, string? right)
    {
        var leftParts = VersionNumberRegex().Match(left ?? "").Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = VersionNumberRegex().Match(right ?? "").Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (leftParts.Length == 0) return 0;
        if (rightParts.Length == 0) return 1;
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            var leftPart = index < leftParts.Length && long.TryParse(leftParts[index], out var l) ? l : 0;
            var rightPart = index < rightParts.Length && long.TryParse(rightParts[index], out var r) ? r : 0;
            if (leftPart != rightPart) return leftPart.CompareTo(rightPart);
        }
        return 0;
    }

    private static string? ReadVersion(string executablePath)
    {
        try
        {
            var raw = FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
            var match = VersionNumberRegex().Match(raw ?? "");
            return match.Success ? match.Value : raw;
        }
        catch { return null; }
    }

    private DcsUpdateStatus GetStatusSnapshot() { lock (_statusGate) return _status; }
    private void SetStatus(Func<DcsUpdateStatus, DcsUpdateStatus> update) { lock (_statusGate) _status = update(_status); }
    private static string Tail(string value) => value.Length <= 500 ? value : value[^500..];

    [GeneratedRegex(@"Latest\s+stable\s+version\s+is[\s\S]*?([0-9]+(?:\.[0-9]+){2,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LatestStableVersionRegex();
    [GeneratedRegex(@"[0-9]+(?:\.[0-9]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumberRegex();

    private sealed record Installation(string? ExecutablePath, string? InstallRoot, string? UpdaterPath, string? Version, bool CanUpdate);
}
