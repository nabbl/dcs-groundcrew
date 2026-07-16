using System.Text.Json;
using DcsDashboard.Api.Models;
using Microsoft.Data.Sqlite;

namespace DcsDashboard.Api.Data;

public sealed class SettingsStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public SettingsStore(IWebHostEnvironment environment)
    {
        var dataDirectory = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Groundcrew")
            : Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(dataDirectory);
        _connectionString = $"Data Source={Path.Combine(dataDirectory, "dashboard.db")}";
        Initialize();
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS settings (id INTEGER PRIMARY KEY CHECK (id = 1), payload TEXT NOT NULL, updated_utc TEXT NOT NULL);";
        command.ExecuteNonQuery();

        command.CommandText = "INSERT OR IGNORE INTO settings (id, payload, updated_utc) VALUES (1, $payload, $updated);";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new DashboardSettings(), _json));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public async Task<DashboardSettings> GetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM settings WHERE id = 1";
            var payload = (string?)await command.ExecuteScalarAsync();
            var settings = payload is null ? new DashboardSettings() : JsonSerializer.Deserialize<DashboardSettings>(payload, _json) ?? new DashboardSettings();
            Normalize(settings);
            return settings;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(DashboardSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE settings SET payload = $payload, updated_utc = $updated WHERE id = 1";
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(settings, _json));
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
    }

    private static void Normalize(DashboardSettings settings)
    {
        settings.Integrations ??= new List<IntegrationSettings>();
        foreach (var defaults in IntegrationSettings.Defaults())
        {
            var existing = settings.Integrations.FirstOrDefault(item => string.Equals(item.Id, defaults.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                settings.Integrations.Add(defaults);
                continue;
            }

            if (string.IsNullOrWhiteSpace(existing.Name)) existing.Name = defaults.Name;
            if (string.IsNullOrWhiteSpace(existing.Description)) existing.Description = defaults.Description;
            if (string.IsNullOrWhiteSpace(existing.Kind) || (existing.Kind == "process" && defaults.Kind != "process")) existing.Kind = defaults.Kind;
            if (string.IsNullOrWhiteSpace(existing.Host)) existing.Host = defaults.Host;
            existing.Port ??= defaults.Port;
            if (string.IsNullOrWhiteSpace(existing.SrsAddress)) existing.SrsAddress = defaults.SrsAddress;
            if (string.IsNullOrWhiteSpace(existing.TelemetryAddress)) existing.TelemetryAddress = defaults.TelemetryAddress;
            existing.Url ??= defaults.Url;
        }

        DiscoverOlympus(settings);
    }

    private static void DiscoverOlympus(DashboardSettings settings)
    {
        var olympus = settings.Integrations.FirstOrDefault(item => string.Equals(item.Id, "olympus", StringComparison.OrdinalIgnoreCase));
        if (olympus is null || string.IsNullOrWhiteSpace(settings.SavedGamesPath)) return;

        var dcsSavedGames = settings.SavedGamesPath.Trim();
        var savedGamesRoot = Directory.GetParent(dcsSavedGames)?.FullName;
        var configCandidate = Path.Combine(dcsSavedGames, "Config", "olympus.json");
        if ((string.IsNullOrWhiteSpace(olympus.ConfigPath) || !File.Exists(olympus.ConfigPath)) && File.Exists(configCandidate))
            olympus.ConfigPath = configCandidate;

        if (string.IsNullOrWhiteSpace(olympus.ExecutablePath) || !File.Exists(olympus.ExecutablePath))
        {
            var launcherCandidates = string.IsNullOrWhiteSpace(savedGamesRoot)
                ? Array.Empty<string>()
                : new[]
                {
                    Path.Combine(savedGamesRoot, "DCS Olympus", "frontend", "server.vbs"),
                    Path.Combine(savedGamesRoot, "DCS Olympus", "frontend", "server", "server.vbs")
                };
            var launcher = launcherCandidates.FirstOrDefault(File.Exists);
            if (launcher is not null) olympus.ExecutablePath = launcher;
        }

        if (string.IsNullOrWhiteSpace(olympus.ConfigPath) || !File.Exists(olympus.ConfigPath)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(olympus.ConfigPath));
            if (!document.RootElement.TryGetProperty("frontend", out var frontend)
                || !frontend.TryGetProperty("port", out var portValue)
                || !portValue.TryGetInt32(out var port)
                || port is < 1 or > 65535) return;
            olympus.Port = port;
            if (string.IsNullOrWhiteSpace(olympus.Url)
                || string.Equals(olympus.Url, "http://127.0.0.1:3000", StringComparison.OrdinalIgnoreCase))
                olympus.Url = $"http://127.0.0.1:{port}";
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
