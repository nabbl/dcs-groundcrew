namespace DcsDashboard.Api.Models;

public sealed record Metric(string Label, double Value, string Unit, double Max);
public sealed record Player(string Id, string Name, string Side, string Slot, int Ping, DateTimeOffset JoinedAt);
public sealed record ChatMessage(string Id, string Author, string Message, string Timestamp, bool System = false);

public sealed record IntegrationStatus(
    string Id,
    string Name,
    string Description,
    bool Installed,
    bool Running,
    string? Version,
    string? Url,
    bool Configurable);

public sealed record ServerStatus(
    string State,
    string Name,
    string Version,
    string Mission,
    string Theatre,
    long UptimeSeconds,
    double Fps,
    int Players,
    int MaxPlayers);

public sealed record DashboardSnapshot(
    bool DemoMode,
    ServerStatus Server,
    IReadOnlyList<Metric> Metrics,
    IReadOnlyList<Player> Players,
    IReadOnlyList<IntegrationStatus> Integrations,
    IReadOnlyList<ChatMessage> Chat);

public sealed class DashboardSettings
{
    public string ServerName { get; set; } = "DCS SERVER ONE";
    public string DcsExecutablePath { get; set; } = "";
    public string DcsArguments { get; set; } = "--server --norender";
    public string SavedGamesPath { get; set; } = "";
    public string MissionLibraryPath { get; set; } = "";
    public string TacviewRecordingsPath { get; set; } = "";
    public string ActiveMissionPath { get; set; } = "";
    public int MaxPlayers { get; set; } = 32;
    public List<IntegrationSettings> Integrations { get; set; } = IntegrationSettings.Defaults();
}

public sealed class IntegrationSettings
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? Url { get; set; }

    public static List<IntegrationSettings> Defaults() => new()
    {
        new() { Id = "srs", Name = "SimpleRadio Standalone", Description = "Voice communications" },
        new() { Id = "olympus", Name = "DCS Olympus", Description = "Real-time mission control" },
        new() { Id = "tacview", Name = "Tacview", Description = "Flight recording and ACMI" },
        new() { Id = "skyeye", Name = "SkyEye", Description = "AI-powered GCI" },
        new() { Id = "dks", Name = "Digital Kneeboard", Description = "Mission kneeboard tools" }
    };
}

public sealed record FileSystemEntry(string Name, string FullPath, bool IsDirectory, long? Size, DateTimeOffset Modified);
public sealed record FileBrowserResult(string CurrentPath, string? ParentPath, IReadOnlyList<FileSystemEntry> Entries);
public sealed record MissionSwitchRequest(string Path);
public sealed record ChatSendRequest(string Message);
public sealed record ModerationRequest(string PlayerId, string? Reason);

