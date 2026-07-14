using DcsDashboard.Api.Data;
using DcsDashboard.Api.Hubs;
using DcsDashboard.Api.Models;
using DcsDashboard.Api.Services;
using System.Net.NetworkInformation;
using System.Net.Sockets;

var serviceMode = args.Contains("--service", StringComparer.OrdinalIgnoreCase);
var launcherMode = args.Contains("--open-dashboard", StringComparer.OrdinalIgnoreCase);
if (OperatingSystem.IsWindows() && (launcherMode || !serviceMode))
{
    await DashboardLauncher.OpenAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "DCS Groundcrew");
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
    builder.WebHost.UseUrls(GetDefaultUrls());
builder.Services.AddSignalR();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<DcsProcessService>();
builder.Services.AddSingleton<HostMetricsService>();
builder.Services.AddSingleton<IntegrationService>();
builder.Services.AddSingleton<DcsServerConfigurationService>();
builder.Services.AddScoped<SnapshotService>();
builder.Services.AddHostedService<SnapshotBroadcastService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", timestamp = DateTimeOffset.UtcNow }));
app.MapGet("/api/snapshot", async (SnapshotService service) => Results.Ok(await service.GetAsync()));
app.MapGet("/api/settings", async (SettingsStore store) => Results.Ok(await store.GetAsync()));
app.MapPut("/api/settings", async (DashboardSettings settings, SettingsStore store) => { await store.SaveAsync(settings); return Results.NoContent(); });
app.MapGet("/api/server-config", async (DcsServerConfigurationService service) => Results.Ok(await service.GetAsync()));
app.MapPut("/api/server-config", async (DcsServerConfigurationUpdate update, DcsServerConfigurationService service) =>
{
    try { return Results.Ok(await service.SaveAsync(update)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (UnauthorizedAccessException) { return Results.Problem("The dashboard service account cannot update serverSettings.lua.", statusCode: 403); }
    catch (IOException ex) { return Results.Problem($"serverSettings.lua could not be updated: {ex.Message}", statusCode: 409); }
});

app.MapPost("/api/server/start", (DcsProcessService service) => RunControl(service.StartAsync));
app.MapPost("/api/server/stop", (DcsProcessService service) => RunControl(service.StopAsync));
app.MapPost("/api/server/restart", (DcsProcessService service) => RunControl(service.RestartAsync));

app.MapGet("/api/missions", async (SettingsStore store) =>
{
    var settings = await store.GetAsync();
    var root = settings.MissionLibraryPath;
    if (string.IsNullOrWhiteSpace(root)) return Results.Ok(new MissionLibraryResult("", false, false, Array.Empty<MissionFile>()));
    if (!Directory.Exists(root)) return Results.Ok(new MissionLibraryResult(root, true, false, Array.Empty<MissionFile>()));

    try
    {
        var activePath = string.IsNullOrWhiteSpace(settings.ActiveMissionPath) ? null : Path.GetFullPath(settings.ActiveMissionPath);
        var missions = Directory.EnumerateFiles(root, "*.miz", SearchOption.AllDirectories)
            .Take(500)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => new MissionFile(
                Path.GetFileNameWithoutExtension(file.Name),
                file.FullName,
                Path.GetRelativePath(root, file.FullName),
                file.Length,
                file.LastWriteTimeUtc,
                activePath is not null && string.Equals(Path.GetFullPath(file.FullName), activePath, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return Results.Ok(new MissionLibraryResult(root, true, true, missions));
    }
    catch (UnauthorizedAccessException) { return Results.Problem("The dashboard service account cannot read the configured mission library.", statusCode: 403); }
});

app.MapGet("/api/files/roots", () => Results.Ok(DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new FileSystemEntry(d.Name, d.RootDirectory.FullName, true, null, DateTimeOffset.MinValue))));
app.MapGet("/api/files", (string path) =>
{
    if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return Results.BadRequest(new { error = "Choose an existing absolute directory on the server host." });
    try
    {
        var directory = new DirectoryInfo(path);
        var entries = directory.EnumerateFileSystemInfos().OrderByDescending(x => x is DirectoryInfo).ThenBy(x => x.Name).Take(500).Select(item =>
            new FileSystemEntry(item.Name, item.FullName, item is DirectoryInfo, item is FileInfo file ? file.Length : null, item.LastWriteTimeUtc)).ToList();
        return Results.Ok(new FileBrowserResult(directory.FullName, directory.Parent?.FullName, entries));
    }
    catch (UnauthorizedAccessException) { return Results.Problem("The dashboard service account cannot access this directory.", statusCode: 403); }
});

app.MapPost("/api/missions/switch", async (MissionSwitchRequest request, SettingsStore store, DcsProcessService dcs) =>
{
    if (!Path.IsPathFullyQualified(request.Path) || !File.Exists(request.Path) || !string.Equals(Path.GetExtension(request.Path), ".miz", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Select an existing .miz file on the server host." });
    var settings = await store.GetAsync();
    settings.ActiveMissionPath = request.Path;
    await store.SaveAsync(settings);
    if (await dcs.FindAsync() is not null) await dcs.RestartAsync(); else await dcs.StartAsync();
    return Results.Accepted();
});

app.MapGet("/api/integrations", async (IntegrationService service) => Results.Ok(await service.GetStatusesAsync()));
app.MapPost("/api/integrations/{id}/{action}", async (string id, string action, IntegrationService service) =>
{
    if (action is not ("start" or "stop" or "restart")) return Results.BadRequest(new { error = "Action must be start, stop, or restart." });
    try { await service.ControlAsync(id, action); return Results.Accepted(); }
    catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
});

app.MapGet("/api/chat", () => Results.Ok(new { configured = false, messages = Array.Empty<ChatMessage>(), note = "Install and configure the DCS server hook before chat can be read." }));
app.MapPost("/api/chat", (ChatSendRequest _) => Results.Problem("DCS chat adapter is not configured.", statusCode: 501));
app.MapPost("/api/players/{playerId}/{action}", (string playerId, string action, ModerationRequest _) => Results.Problem($"The DCS moderation adapter is not configured; '{action}' was not sent for player '{playerId}'.", statusCode: 501));

app.MapHub<DashboardHub>("/hubs/dashboard");
app.MapFallbackToFile("index.html");
app.Run();

static string[] GetDefaultUrls()
{
    const string localUrl = "http://127.0.0.1:5080";
    if (!OperatingSystem.IsWindows()) return new[] { localUrl };

    try
    {
        var tailscaleAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .Where(adapter => adapter.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)
                || adapter.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase))
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => $"http://{address}:5080");
        return new[] { localUrl }.Concat(tailscaleAddresses).Distinct().ToArray();
    }
    catch { return new[] { localUrl }; }
}

static async Task<IResult> RunControl(Func<Task> action)
{
    try { await action(); return Results.Accepted(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    catch (Exception ex) { return Results.Problem(ex.Message); }
}
