using System.Diagnostics;
using System.Net.NetworkInformation;
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
        var listeningPorts = OperatingSystem.IsWindows()
            ? IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet()
            : new HashSet<int>();
        return settings.Integrations.Select(item =>
        {
            var executableInstalled = !string.IsNullOrWhiteSpace(item.ExecutablePath) && File.Exists(item.ExecutablePath);
            var configInstalled = !string.IsNullOrWhiteSpace(item.ConfigPath) && File.Exists(item.ConfigPath);
            var portListening = item.Port is > 0 && listeningPorts.Contains(item.Port.Value);
            var webConfigured = item.Kind == "web" && !string.IsNullOrWhiteSpace(item.Url);
            var tacviewConfigured = item.Id == "tacview" && !string.IsNullOrWhiteSpace(settings.TacviewRecordingsPath) && Directory.Exists(settings.TacviewRecordingsPath);
            var installed = executableInstalled || configInstalled || portListening || webConfigured || tacviewConfigured;
            var process = executableInstalled ? Process.GetProcessesByName(Path.GetFileNameWithoutExtension(item.ExecutablePath)).FirstOrDefault() : null;
            string? version = null;
            try { version = executableInstalled ? FileVersionInfo.GetVersionInfo(item.ExecutablePath).FileVersion : null; } catch { }
            return new IntegrationStatus(item.Id, item.Name, item.Description, item.Kind, installed, process is not null || portListening, version, item.Url, true);
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
