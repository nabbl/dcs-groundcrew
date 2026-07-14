using System.Diagnostics;
using DcsDashboard.Api.Data;

namespace DcsDashboard.Api.Services;

public sealed class DcsProcessService
{
    private readonly SettingsStore _settings;
    private readonly ILogger<DcsProcessService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DcsProcessService(SettingsStore settings, ILogger<DcsProcessService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<Process?> FindAsync()
    {
        var settings = await _settings.GetAsync();
        var name = string.IsNullOrWhiteSpace(settings.DcsExecutablePath)
            ? "DCS"
            : Path.GetFileNameWithoutExtension(settings.DcsExecutablePath);
        try { return Process.GetProcessesByName(name).OrderByDescending(p => p.StartTime).FirstOrDefault(); }
        catch { return null; }
    }

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (await FindAsync() is not null) return;
            var settings = await _settings.GetAsync();
            if (string.IsNullOrWhiteSpace(settings.DcsExecutablePath) || !File.Exists(settings.DcsExecutablePath))
                throw new InvalidOperationException("Configure a valid DCS executable path before starting the server.");

            var startInfo = new ProcessStartInfo(settings.DcsExecutablePath, settings.DcsArguments)
            {
                WorkingDirectory = Path.GetDirectoryName(settings.DcsExecutablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start the DCS process.");
            _logger.LogInformation("Started DCS dedicated server.");
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var process = await FindAsync();
            if (process is null) return;
            if (process.CloseMainWindow() && await WaitForExitAsync(process, TimeSpan.FromSeconds(15))) return;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            _logger.LogWarning("DCS required a forced process-tree stop.");
        }
        finally { _gate.Release(); }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(TimeSpan.FromSeconds(2));
        await StartAsync();
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cancellation.Token); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
