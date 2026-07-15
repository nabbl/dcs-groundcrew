using DcsDashboard.Api.Models;
using Microsoft.Data.Sqlite;

namespace DcsDashboard.Api.Data;

public sealed class ModerationAuditStore
{
    private readonly string _connectionString;

    public ModerationAuditStore(IWebHostEnvironment environment)
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
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS moderation_audit (
                id TEXT PRIMARY KEY,
                player_id TEXT NOT NULL,
                player_name TEXT NOT NULL,
                action TEXT NOT NULL,
                reason TEXT NOT NULL,
                duration_seconds INTEGER NULL,
                succeeded INTEGER NOT NULL,
                error TEXT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_moderation_audit_created ON moderation_audit(created_utc DESC);
            """;
        command.ExecuteNonQuery();
    }

    public async Task AppendAsync(ModerationAuditEntry entry)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO moderation_audit
                (id, player_id, player_name, action, reason, duration_seconds, succeeded, error, created_utc)
            VALUES
                ($id, $player_id, $player_name, $action, $reason, $duration_seconds, $succeeded, $error, $created_utc);
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$player_id", entry.PlayerId);
        command.Parameters.AddWithValue("$player_name", entry.PlayerName);
        command.Parameters.AddWithValue("$action", entry.Action);
        command.Parameters.AddWithValue("$reason", entry.Reason);
        command.Parameters.AddWithValue("$duration_seconds", entry.DurationSeconds is null ? DBNull.Value : entry.DurationSeconds.Value);
        command.Parameters.AddWithValue("$succeeded", entry.Succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$error", entry.Error is null ? DBNull.Value : entry.Error);
        command.Parameters.AddWithValue("$created_utc", entry.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<ModerationAuditEntry>> ListRecentAsync(int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 500);
        var entries = new List<ModerationAuditEntry>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, player_id, player_name, action, reason, duration_seconds, succeeded, error, created_utc
            FROM moderation_audit
            ORDER BY created_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            entries.Add(new ModerationAuditEntry(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : checked((uint)reader.GetInt64(5)), reader.GetInt64(6) != 0,
                reader.IsDBNull(7) ? null : reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8))));
        }
        return entries;
    }
}
