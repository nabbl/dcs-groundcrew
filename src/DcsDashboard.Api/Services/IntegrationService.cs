using System.Diagnostics;
using DcsDashboard.Api.Data;
using DcsDashboard.Api.Models;

namespace DcsDashboard.Api.Services;

public sealed class IntegrationService
{
    private readonly SettingsStore _store;
    public IntegrationService(SettingsStore store) => _store = store;

    public async Task<IReadOnlyList<IntegrationStatus>> GetStatusesAsync()
    {
        var settings = await _store.GetAsync();
        return settings.Integrations.Select(item =>
        {
            var installed = !string.IsNullOrWhiteSpace(item.ExecutablePath) && File.Exists(item.ExecutablePath);
            var process = installed ? Process.GetProcessesByName(Path.GetFileNameWithoutExtension(item.ExecutablePath)).FirstOrDefault() : null;
            string? version = null;
            try { version = installed ? FileVersionInfo.GetVersionInfo(item.ExecutablePath).FileVersion : null; } catch { }
            return new IntegrationStatus(item.Id, item.Name, item.Description, installed, process is not null, version, item.Url, true);
        }).ToList();
    }

    public async Task ControlAsync(string id, string action)
    {
        var settings = await _store.GetAsync();
        var item = settings.Integrations.SingleOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Unknown integration '{id}'.");
        if (string.IsNullOrWhiteSpace(item.ExecutablePath) || !File.Exists(item.ExecutablePath))
            throw new InvalidOperationException($"{item.Name} is not configured or installed.");
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(item.ExecutablePath));
        if (action is "stop" or "restart") foreach (var process in processes) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        if (action is "start" or "restart") Process.Start(new ProcessStartInfo(item.ExecutablePath, item.Arguments) { WorkingDirectory = Path.GetDirectoryName(item.ExecutablePath)!, UseShellExecute = false });
    }
}

