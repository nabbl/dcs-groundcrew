namespace DcsDashboard.Api.Models;

public sealed record Metric(string Label, double Value, string Unit, double Max);
public sealed record Player(string Id, string Name, string Side, string Slot, int Ping, DateTimeOffset JoinedAt);
public sealed record ChatMessage(string Id, string Author, string Message, string Timestamp, bool System = false);

public sealed record IntegrationStatus(
    string Id,
    string Name,
    string Description,
    string Kind,
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
    public string Kind { get; set; } = "process";
    public string ConfigPath { get; set; } = "";
    public string Host { get; set; } = "127.0.0.1";
    public int? Port { get; set; }
    public string SrsAddress { get; set; } = "";
    public string TelemetryAddress { get; set; } = "";
    public string? Url { get; set; }

    public static List<IntegrationSettings> Defaults() => new()
    {
        new() { Id = "srs", Name = "SimpleRadio Standalone", Description = "Voice communications", Kind = "network-process", Port = 5002 },
        new() { Id = "olympus", Name = "DCS Olympus", Description = "Real-time mission control", Kind = "web-process", Port = 3000, Url = "http://127.0.0.1:3000" },
        new() { Id = "tacview", Name = "Tacview", Description = "Flight recording and real-time telemetry", Kind = "telemetry", Port = 42674 },
        new() { Id = "skyeye", Name = "SkyEye", Description = "AI-powered GCI", Kind = "process", SrsAddress = "127.0.0.1:5002", TelemetryAddress = "127.0.0.1:42674" },
        new() { Id = "dks", Name = "Digital Kneeboard Simulator", Description = "Browser-based mission planning and kneeboards", Kind = "web", Url = "https://www.digitalkneeboardsimulator.com/" }
    };
}

public sealed record FileSystemEntry(string Name, string FullPath, bool IsDirectory, long? Size, DateTimeOffset Modified);
public sealed record FileBrowserResult(string CurrentPath, string? ParentPath, IReadOnlyList<FileSystemEntry> Entries);
public sealed record MissionFile(string Name, string FullPath, string RelativePath, long Size, DateTimeOffset Modified, bool Active);
public sealed record MissionLibraryResult(string RootPath, bool Configured, bool Exists, IReadOnlyList<MissionFile> Missions);
public sealed record MissionSlotSummary(string Airframe, string Coalition, int Count);
public sealed record MissionDependency(string Name, string Kind, string Status);
public sealed record MissionReadinessCheck(string Severity, string Title, string Detail);
public sealed record MissionReadinessReport(
    string Path,
    string Hash,
    bool Readable,
    string Status,
    string Title,
    string Theatre,
    string MissionDate,
    string StartTime,
    string Weather,
    long Size,
    DateTimeOffset Modified,
    int TotalSlots,
    int BlueSlots,
    int RedSlots,
    int NeutralSlots,
    IReadOnlyList<MissionSlotSummary> Slots,
    IReadOnlyList<MissionDependency> Dependencies,
    IReadOnlyList<string> Frameworks,
    IReadOnlyList<MissionReadinessCheck> Checks);
public sealed record DcsServerConfiguration(
    string Path,
    bool Exists,
    DateTimeOffset? Modified,
    string Name,
    string Description,
    bool PasswordConfigured,
    int MaxPlayers,
    int Port,
    bool IsPublic,
    string BindAddress,
    bool ListLoop,
    bool ListShuffle,
    int ResumeMode,
    int MaxPing,
    bool RequirePureClients,
    bool RequirePureScripts,
    bool RequirePureTextures,
    bool RequirePureModels,
    bool AllowOwnshipExport,
    bool AllowObjectExport,
    bool AllowSensorExport,
    bool AllowChangeSkin,
    bool AllowChangeTailNumber,
    bool VoiceChatServer,
    bool AllowTrialOnlyClients,
    bool AllowDynamicRadio,
    bool AllowPlayersPool,
    bool ServerCanScreenshot);
public sealed record DcsServerConfigurationUpdate(
    string Name,
    string Description,
    string? Password,
    bool ClearPassword,
    int MaxPlayers,
    int Port,
    bool IsPublic,
    string BindAddress,
    bool ListLoop,
    bool ListShuffle,
    int ResumeMode,
    int MaxPing,
    bool RequirePureClients,
    bool RequirePureScripts,
    bool RequirePureTextures,
    bool RequirePureModels,
    bool AllowOwnshipExport,
    bool AllowObjectExport,
    bool AllowSensorExport,
    bool AllowChangeSkin,
    bool AllowChangeTailNumber,
    bool VoiceChatServer,
    bool AllowTrialOnlyClients,
    bool AllowDynamicRadio,
    bool AllowPlayersPool,
    bool ServerCanScreenshot);
public sealed record DcsServerConfigurationSaveResult(DcsServerConfiguration Configuration, string? BackupPath);
public sealed record MissionSwitchRequest(string Path);
public sealed record ChatSendRequest(string Message);
public sealed record ModerationRequest(string PlayerId, string? Reason);
