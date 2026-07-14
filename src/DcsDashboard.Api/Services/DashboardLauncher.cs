using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DcsDashboard.Api.Services;

internal static class DashboardLauncher
{
    private const string DashboardUrl = "http://127.0.0.1:5080";
    private const string HealthUrl = $"{DashboardUrl}/api/health";
    private const string ServiceName = "DcsGroundcrew";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1) };

    public static async Task OpenAsync()
    {
        if (!await IsReadyAsync())
        {
            if (!await RequestServiceStartAsync()) return;

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(30) && !await IsReadyAsync())
                await Task.Delay(750);
        }

        if (!await IsReadyAsync())
        {
            ShowError(
                "Groundcrew could not start its local service. Open Windows Services, start " +
                $"'{ServiceName}', and try again.\n\nDashboard: {DashboardUrl}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(DashboardUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError($"Groundcrew is running, but the browser could not be opened.\n\nOpen {DashboardUrl} manually.\n\n{ex.Message}");
        }
    }

    private static async Task<bool> RequestServiceStartAsync()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"start {ServiceName}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process is null) return false;
            await process.WaitForExitAsync();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowError("Groundcrew needs permission to start its Windows service. The request was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            ShowError($"Groundcrew could not request its Windows service to start.\n\n{ex.Message}");
            return false;
        }
    }

    private static async Task<bool> IsReadyAsync()
    {
        try
        {
            using var response = await Http.GetAsync(HealthUrl);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void ShowError(string message) =>
        MessageBox(nint.Zero, message, "Groundcrew", 0x00000010);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);
}
