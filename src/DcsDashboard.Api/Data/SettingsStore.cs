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
            return payload is null ? new DashboardSettings() : JsonSerializer.Deserialize<DashboardSettings>(payload, _json) ?? new DashboardSettings();
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
}
