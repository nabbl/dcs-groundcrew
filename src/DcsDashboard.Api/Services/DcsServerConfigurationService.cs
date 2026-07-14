using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DcsDashboard.Api.Data;
using DcsDashboard.Api.Models;

namespace DcsDashboard.Api.Services;

public sealed class DcsServerConfigurationService
{
    private const string ValuePattern = "\"(?:\\\\.|[^\"\\\\])*\"|true|false|-?\\d+(?:\\.\\d+)?";
    private readonly SettingsStore _store;

    public DcsServerConfigurationService(SettingsStore store) => _store = store;

    public async Task<DcsServerConfiguration> GetAsync()
    {
        var settings = await _store.GetAsync();
        var path = ResolvePath(settings.SavedGamesPath);
        if (path is null) return FromContent("", false, null, "");
        if (!File.Exists(path)) return FromContent(path, false, null, "");
        var content = await File.ReadAllTextAsync(path);
        return FromContent(path, true, File.GetLastWriteTimeUtc(path), content);
    }

    public async Task<DcsServerConfigurationSaveResult> SaveAsync(DcsServerConfigurationUpdate update)
    {
        Validate(update);
        var settings = await _store.GetAsync();
        var path = ResolvePath(settings.SavedGamesPath)
            ?? throw new InvalidOperationException("Choose the DCS Saved Games folder in Settings before editing server configuration.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var existed = File.Exists(path);
        var content = existed
            ? await File.ReadAllTextAsync(path)
            : CreateBaseDocument(settings.ActiveMissionPath);
        string? backupPath = null;
        if (existed)
        {
            backupPath = $"{path}.groundcrew-backup-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}";
            File.Copy(path, backupPath, overwrite: false);
        }

        var values = new List<(string Key, string Value, bool Advanced)>
        {
            ("name", LuaString(update.Name.Trim()), false),
            ("description", LuaString(update.Description), false),
            ("maxPlayers", update.MaxPlayers.ToString(CultureInfo.InvariantCulture), false),
            ("port", update.Port.ToString(CultureInfo.InvariantCulture), false),
            ("isPublic", LuaBoolean(update.IsPublic), false),
            ("bind_address", LuaString(update.BindAddress.Trim()), false),
            ("listLoop", LuaBoolean(update.ListLoop), false),
            ("listShuffle", LuaBoolean(update.ListShuffle), false),
            ("resume_mode", update.ResumeMode.ToString(CultureInfo.InvariantCulture), true),
            ("maxPing", update.MaxPing.ToString(CultureInfo.InvariantCulture), true),
            ("require_pure_clients", LuaBoolean(update.RequirePureClients), false),
            ("require_pure_scripts", LuaBoolean(update.RequirePureScripts), false),
            ("require_pure_textures", LuaBoolean(update.RequirePureTextures), false),
            ("require_pure_models", LuaBoolean(update.RequirePureModels), false),
            ("allow_ownship_export", LuaBoolean(update.AllowOwnshipExport), true),
            ("allow_object_export", LuaBoolean(update.AllowObjectExport), true),
            ("allow_sensor_export", LuaBoolean(update.AllowSensorExport), true),
            ("allow_change_skin", LuaBoolean(update.AllowChangeSkin), true),
            ("allow_change_tailno", LuaBoolean(update.AllowChangeTailNumber), true),
            ("voice_chat_server", LuaBoolean(update.VoiceChatServer), true),
            ("allow_trial_only_clients", LuaBoolean(update.AllowTrialOnlyClients), true),
            ("allow_dynamic_radio", LuaBoolean(update.AllowDynamicRadio), true),
            ("allow_players_pool", LuaBoolean(update.AllowPlayersPool), true),
            ("server_can_screenshot", LuaBoolean(update.ServerCanScreenshot), true),
        };
        if (update.ClearPassword) values.Add(("password", LuaString(""), false));
        else if (update.Password is not null) values.Add(("password", LuaString(update.Password), false));

        var additions = new List<(string Key, string Value, bool Advanced)>();
        foreach (var value in values)
        {
            if (!TryReplaceLastValue(ref content, value.Key, value.Value)) additions.Add(value);
        }
        if (additions.Count > 0) content = AppendOverrides(content, additions);

        var temporaryPath = $"{path}.groundcrew-tmp";
        await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, path, overwrite: true);
        settings.ServerName = update.Name.Trim();
        settings.MaxPlayers = update.MaxPlayers;
        await _store.SaveAsync(settings);
        var configuration = FromContent(path, true, File.GetLastWriteTimeUtc(path), content);
        return new DcsServerConfigurationSaveResult(configuration, backupPath);
    }

    private static DcsServerConfiguration FromContent(string path, bool exists, DateTimeOffset? modified, string content) => new(
        path,
        exists,
        modified,
        ReadString(content, "name", "DCS Server"),
        ReadString(content, "description", ""),
        !string.IsNullOrEmpty(ReadString(content, "password", "")),
        ReadInteger(content, "maxPlayers", 32),
        ReadInteger(content, "port", 10308),
        ReadBoolean(content, "isPublic", true),
        ReadString(content, "bind_address", ""),
        ReadBoolean(content, "listLoop", false),
        ReadBoolean(content, "listShuffle", false),
        ReadInteger(content, "resume_mode", 1),
        ReadInteger(content, "maxPing", 0),
        ReadBoolean(content, "require_pure_clients", true),
        ReadBoolean(content, "require_pure_scripts", false),
        ReadBoolean(content, "require_pure_textures", true),
        ReadBoolean(content, "require_pure_models", true),
        ReadBoolean(content, "allow_ownship_export", false),
        ReadBoolean(content, "allow_object_export", false),
        ReadBoolean(content, "allow_sensor_export", false),
        ReadBoolean(content, "allow_change_skin", true),
        ReadBoolean(content, "allow_change_tailno", true),
        ReadBoolean(content, "voice_chat_server", true),
        ReadBoolean(content, "allow_trial_only_clients", false),
        ReadBoolean(content, "allow_dynamic_radio", true),
        ReadBoolean(content, "allow_players_pool", true),
        ReadBoolean(content, "server_can_screenshot", false));

    private static string? ResolvePath(string savedGamesPath)
    {
        if (string.IsNullOrWhiteSpace(savedGamesPath) || !Path.IsPathFullyQualified(savedGamesPath)) return null;
        return Path.Combine(savedGamesPath, "Config", "serverSettings.lua");
    }

    private static void Validate(DcsServerConfigurationUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.Name) || update.Name.Length > 200) throw new ArgumentException("Server name must contain 1 to 200 characters.");
        if (update.Description.Length > 4000) throw new ArgumentException("Server description cannot exceed 4000 characters.");
        if (update.Password is { Length: > 200 }) throw new ArgumentException("Server password cannot exceed 200 characters.");
        if (update.MaxPlayers is < 1 or > 256) throw new ArgumentException("Player limit must be between 1 and 256.");
        if (update.Port is < 1 or > 65535) throw new ArgumentException("Game port must be between 1 and 65535.");
        if (update.ResumeMode is < 0 or > 2) throw new ArgumentException("Resume mode must be manual, on load, or with clients.");
        if (update.MaxPing is < 0 or > 5000) throw new ArgumentException("Maximum ping must be between 0 and 5000 ms.");
    }

    private static string CreateBaseDocument(string activeMissionPath)
    {
        var mission = !string.IsNullOrWhiteSpace(activeMissionPath) && Path.IsPathFullyQualified(activeMissionPath)
            ? $"cfg[\"missionList\"][1] = {LuaString(activeMissionPath)}\r\n"
            : "";
        return $"cfg = {{}}\r\ncfg[\"advanced\"] = {{}}\r\ncfg[\"missionList\"] = {{}}\r\n{mission}cfg[\"current\"] = 1\r\ncfg[\"listStartIndex\"] = 1\r\ncfg[\"uri\"] = \"startServer\"\r\n";
    }

    private static bool TryReplaceLastValue(ref string content, string key, string replacement)
    {
        var matches = ActiveMatches(content, key);
        if (matches.Count == 0) return false;
        var group = matches[^1].Groups["value"];
        content = string.Concat(content.AsSpan(0, group.Index), replacement, content.AsSpan(group.Index + group.Length));
        return true;
    }

    private static List<Match> ActiveMatches(string content, string key)
    {
        var regex = new Regex($"\\[\"{Regex.Escape(key)}\"\\]\\s*=\\s*(?<value>{ValuePattern})", RegexOptions.CultureInvariant);
        return regex.Matches(content).Where(match =>
        {
            var lineStart = content.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;
            var prefix = content[lineStart..match.Index];
            return !prefix.Contains("--", StringComparison.Ordinal);
        }).ToList();
    }

    private static string? ReadValue(string content, string key)
    {
        var matches = ActiveMatches(content, key);
        return matches.Count == 0 ? null : matches[^1].Groups["value"].Value;
    }

    private static string ReadString(string content, string key, string fallback)
    {
        var value = ReadValue(content, key);
        return value is not null && value.StartsWith('"') ? DecodeLuaString(value) : fallback;
    }

    private static int ReadInteger(string content, string key, int fallback)
    {
        var value = ReadValue(content, key);
        if (value?.StartsWith('"') == true) value = DecodeLuaString(value);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : fallback;
    }

    private static bool ReadBoolean(string content, string key, bool fallback)
    {
        var value = ReadValue(content, key);
        return value is null ? fallback : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendOverrides(string content, IReadOnlyList<(string Key, string Value, bool Advanced)> additions)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var builder = new StringBuilder(content.TrimEnd('\r', '\n'));
        builder.Append(newline).Append(newline).Append("-- Groundcrew managed settings").Append(newline);
        if (additions.Any(value => value.Advanced)) builder.Append("cfg[\"advanced\"] = cfg[\"advanced\"] or {}").Append(newline);
        foreach (var (key, value, advanced) in additions)
        {
            builder.Append(advanced ? "cfg[\"advanced\"]" : "cfg").Append("[\"").Append(key).Append("\"] = ").Append(value).Append(newline);
        }
        return builder.ToString();
    }

    private static string LuaString(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n")}\"";
    private static string LuaBoolean(bool value) => value ? "true" : "false";

    private static string DecodeLuaString(string quoted)
    {
        var builder = new StringBuilder();
        for (var index = 1; index < quoted.Length - 1; index++)
        {
            var current = quoted[index];
            if (current != '\\' || index + 1 >= quoted.Length - 1) { builder.Append(current); continue; }
            current = quoted[++index];
            builder.Append(current switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => current });
        }
        return builder.ToString();
    }
}
