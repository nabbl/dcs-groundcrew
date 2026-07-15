using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DcsDashboard.Api.Data;
using DcsDashboard.Api.Models;

namespace DcsDashboard.Api.Services;

public sealed class MissionReadinessService
{
    private const long MaximumArchiveSize = 512L * 1024 * 1024;
    private const long MaximumDataFileSize = 64L * 1024 * 1024;
    private const long MaximumScannedScriptSize = 3L * 1024 * 1024;
    private readonly SettingsStore _settings;
    private readonly DcsServerConfigurationService _serverConfiguration;
    private readonly ConcurrentDictionary<string, MissionReadinessReport> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Name, string[] Needles)[] FrameworkPatterns =
    {
        ("MOOSE", new[] { "base:new", "moose.lua", "moose_include" }),
        ("MIST", new[] { "mist.lua", "mist = {}", "mist.utils" }),
        ("CTLD", new[] { "ctld.lua", "ctld = {}", "ctld." }),
        ("CSAR", new[] { "csar.lua", "csar = {}", "csar." }),
        ("Skynet IADS", new[] { "skynetiads", "skynet-iads" }),
        ("Olympus", new[] { "dcsolympus", "olympus.lua" }),
        ("DCS-gRPC", new[] { "dcs-grpc", "grpc-mission", "grpc.loadscript" }),
        ("Pretense", new[] { "pretense.lua", "zonecommander" }),
    };

    public MissionReadinessService(SettingsStore settings, DcsServerConfigurationService serverConfiguration)
    {
        _settings = settings;
        _serverConfiguration = serverConfiguration;
    }

    public async Task<MissionReadinessReport> InspectAsync(string requestedPath)
    {
        var settings = await _settings.GetAsync();
        var path = ValidateMissionPath(requestedPath, settings.MissionLibraryPath);
        var file = new FileInfo(path);
        var playerLimit = settings.MaxPlayers;
        try
        {
            var serverConfiguration = await _serverConfiguration.GetAsync();
            if (serverConfiguration.Exists) playerLimit = serverConfiguration.MaxPlayers;
        }
        catch
        {
            // Mission inspection remains available when serverSettings.lua cannot be read.
        }
        var cacheKey = $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{playerLimit}|{settings.DcsExecutablePath}";
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        var report = await AnalyzeAsync(file, settings.DcsExecutablePath, playerLimit);
        if (_cache.Count > 1000) _cache.Clear();
        _cache[cacheKey] = report;
        return report;
    }

    private static async Task<MissionReadinessReport> AnalyzeAsync(FileInfo file, string dcsExecutablePath, int playerLimit)
    {
        var hash = await CalculateHashBestEffortAsync(file.FullName);
        try
        {
            await using var stream = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > 5000) throw new InvalidDataException("The archive contains more than 5,000 entries.");
            if (archive.Entries.Sum(entry => entry.Length) > MaximumArchiveSize) throw new InvalidDataException("The uncompressed archive is larger than 512 MB.");

            var unsafePaths = archive.Entries.Count(entry => IsUnsafeArchivePath(entry.FullName));
            var entries = archive.Entries
                .GroupBy(entry => NormalizeArchivePath(entry.FullName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var missionEntry = FindEntry(entries, "mission") ?? throw new InvalidDataException("The archive does not contain a mission data file.");
            var missionText = await ReadTextAsync(missionEntry, MaximumDataFileSize);
            var parseResult = LuaDataParser.ParseAssignmentBestEffort(missionText);
            var mission = parseResult.Value;
            if (!mission.IsTable) throw new InvalidDataException("The mission data root is not a Lua table.");

            var notes = new List<MissionReadinessCheck>();
            if (parseResult.SkippedFields > 0)
                notes.Add(new MissionReadinessCheck("info", "Some mission fields were skipped", $"Groundcrew ignored {parseResult.SkippedFields} unsupported or malformed fields and continued with the data it could read."));

            var dictionary = await ReadOptionalLuaTableAsync(FindEntry(entries, "l10n/default/dictionary"), "Localization dictionary", notes);
            var mapResources = await ReadOptionalLuaTableAsync(FindEntry(entries, "l10n/default/mapresource"), "Resource index", notes);
            var archiveTheatre = (await ReadOptionalTextAsync(FindEntry(entries, "theatre"), "Theatre metadata", notes))?.Trim().Trim('"');
            var theatre = mission.Get("theatre")?.AsString() ?? archiveTheatre ?? "Unknown";
            var titleValue = mission.Get("sortie")?.AsString() ?? mission.Get("name")?.AsString();
            var title = ResolveLocalized(titleValue, dictionary) ?? Path.GetFileNameWithoutExtension(file.Name);

            var slotsRead = true;
            IReadOnlyList<MissionSlotSummary> slots;
            try { slots = ReadSlots(mission); }
            catch (Exception exception)
            {
                slotsRead = false;
                slots = Array.Empty<MissionSlotSummary>();
                AddBestEffortNote(notes, "Slot summary unavailable", "Static slot data", exception);
            }
            var blueSlots = slots.Where(slot => slot.Coalition == "Blue").Sum(slot => slot.Count);
            var redSlots = slots.Where(slot => slot.Coalition == "Red").Sum(slot => slot.Count);
            var neutralSlots = slots.Where(slot => slot.Coalition == "Neutral").Sum(slot => slot.Count);
            var totalSlots = blueSlots + redSlots + neutralSlots;
            IReadOnlyList<MissionDependency> dependencies;
            try { dependencies = ReadDependencies(mission, theatre, dcsExecutablePath); }
            catch (Exception exception)
            {
                dependencies = Array.Empty<MissionDependency>();
                AddBestEffortNote(notes, "Dependency summary unavailable", "Dependency data", exception);
            }
            IReadOnlyList<string> frameworks;
            try { frameworks = await DetectFrameworksAsync(archive, missionText, notes); }
            catch (Exception exception)
            {
                frameworks = Array.Empty<string>();
                AddBestEffortNote(notes, "Script detection unavailable", "Embedded script data", exception);
            }
            var missingResources = 0;
            try { missingResources = CountMissingResources(entries, mapResources); }
            catch (Exception exception) { AddBestEffortNote(notes, "Resource check unavailable", "Resource index data", exception); }

            var missionDate = ReadBestEffort(() => ReadMissionDate(mission), "Not specified", notes, "Mission date unavailable", "Mission date");
            var startTime = ReadBestEffort(() => FormatStartTime(mission.Get("start_time")?.AsInteger()), "Not specified", notes, "Start time unavailable", "Mission start time");
            var weather = ReadBestEffort(() => FormatWeather(mission.Get("weather")), "Not specified", notes, "Weather summary unavailable", "Weather data");
            var checks = notes.Concat(BuildChecks(unsafePaths, totalSlots, slotsRead, playerLimit, dependencies, missingResources, archiveTheatre, theatre)).ToList();
            var status = checks.Any(check => check.Severity == "error")
                ? "error"
                : checks.Any(check => check.Severity == "warning") ? "warning" : "ready";

            return new MissionReadinessReport(
                file.FullName, hash, true, status, title, DisplayTheatre(theatre), missionDate, startTime, weather,
                file.Length, file.LastWriteTimeUtc, totalSlots, blueSlots, redSlots, neutralSlots,
                slots, dependencies, frameworks, checks);
        }
        catch (Exception exception)
        {
            return new MissionReadinessReport(
                file.FullName, hash, false, "error", Path.GetFileNameWithoutExtension(file.Name), "Unknown",
                "Not specified", "Not specified", "Not available", file.Length, file.LastWriteTimeUtc,
                0, 0, 0, 0, Array.Empty<MissionSlotSummary>(), Array.Empty<MissionDependency>(), Array.Empty<string>(),
                new[] { new MissionReadinessCheck("error", "Mission archive could not be inspected", $"Groundcrew left the dashboard running and skipped this report: {Describe(exception)}") });
        }
    }

    private static string ValidateMissionPath(string requestedPath, string missionLibraryPath)
    {
        if (string.IsNullOrWhiteSpace(missionLibraryPath) || !Path.IsPathFullyQualified(missionLibraryPath))
            throw new InvalidOperationException("Choose a mission library in Settings before inspecting missions.");
        if (string.IsNullOrWhiteSpace(requestedPath) || !Path.IsPathFullyQualified(requestedPath))
            throw new ArgumentException("Choose an absolute .miz file path.");

        var root = Path.GetFullPath(missionLibraryPath);
        var path = Path.GetFullPath(requestedPath);
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("Only missions inside the configured mission library can be inspected.");
        if (!string.Equals(Path.GetExtension(path), ".miz", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("The selected .miz file no longer exists.", path);
        return path;
    }

    private static IReadOnlyList<MissionSlotSummary> ReadSlots(LuaDataValue mission)
    {
        var counts = new Dictionary<(string Airframe, string Coalition), int>();
        var coalitionRoot = mission.Get("coalition");
        foreach (var (coalitionKey, coalitionName) in new[] { ("blue", "Blue"), ("red", "Red"), ("neutrals", "Neutral") })
        {
            var countries = coalitionRoot?.Get(coalitionKey)?.Get("country");
            if (countries is null) continue;
            foreach (var country in countries.Values)
            foreach (var category in new[] { "plane", "helicopter" })
            foreach (var group in country.Get(category)?.Get("group")?.Values ?? Enumerable.Empty<LuaDataValue>())
            foreach (var unit in group.Get("units")?.Values ?? Enumerable.Empty<LuaDataValue>())
            {
                var skill = unit.Get("skill")?.AsString();
                if (!string.Equals(skill, "Client", StringComparison.OrdinalIgnoreCase) && !string.Equals(skill, "Player", StringComparison.OrdinalIgnoreCase)) continue;
                var airframe = unit.Get("type")?.AsString() ?? "Unknown aircraft";
                var key = (airframe, coalitionName);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        return counts.Select(item => new MissionSlotSummary(item.Key.Airframe, item.Key.Coalition, item.Value))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Airframe, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<MissionDependency> ReadDependencies(LuaDataValue mission, string theatre, string dcsExecutablePath)
    {
        var catalog = InstalledCatalog.Read(dcsExecutablePath);
        var dependencies = new List<MissionDependency>();
        if (!string.IsNullOrWhiteSpace(theatre) && theatre != "Unknown")
            dependencies.Add(new MissionDependency(DisplayTheatre(theatre), "Terrain", catalog.TerrainStatus(theatre)));

        var requiredModules = mission.Get("requiredModules");
        if (requiredModules?.Fields is not null)
        {
            foreach (var (key, value) in requiredModules.Fields)
            {
                var name = value.AsString() ?? (value.Scalar is bool enabled && enabled ? key : null);
                if (string.IsNullOrWhiteSpace(name)) continue;
                dependencies.Add(new MissionDependency(name, "Module", catalog.ModuleStatus(name)));
            }
        }

        return dependencies
            .DistinctBy(item => (item.Name.ToLowerInvariant(), item.Kind))
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<MissionReadinessCheck> BuildChecks(
        int unsafePaths,
        int slots,
        bool slotsRead,
        int playerLimit,
        IReadOnlyList<MissionDependency> dependencies,
        int missingResources,
        string? archiveTheatre,
        string theatre)
    {
        var checks = new List<MissionReadinessCheck>();
        if (slotsRead)
            checks.Add(slots > 0
                ? new MissionReadinessCheck("pass", "Static slots found", $"{slots} Client or Player units were found in the mission data.")
                : new MissionReadinessCheck("warning", "No static player slots found", "The mission may rely on runtime slot scripts or may not be intended for multiplayer."));

        if (slots > playerLimit)
            checks.Add(new MissionReadinessCheck("warning", "Player cap below slot count", $"The mission exposes {slots} static slots while serverSettings.lua allows {playerLimit} players."));
        var terrain = dependencies.FirstOrDefault(item => item.Kind == "Terrain");
        if (terrain?.Status == "missing") checks.Add(new MissionReadinessCheck("error", "Terrain not detected", $"{terrain.Name} was not found in the configured DCS installation."));
        else if (terrain?.Status == "available") checks.Add(new MissionReadinessCheck("pass", "Terrain available", $"{terrain.Name} was found in the configured DCS installation."));
        else if (terrain is not null) checks.Add(new MissionReadinessCheck("info", "Terrain not verified", "Configure a valid DCS executable path to verify installed terrain files."));
        if (!string.IsNullOrWhiteSpace(archiveTheatre) && theatre != "Unknown" && !string.Equals(NormalizeName(archiveTheatre), NormalizeName(theatre), StringComparison.Ordinal))
            checks.Add(new MissionReadinessCheck("warning", "Theatre metadata differs", $"The archive says '{archiveTheatre}' while mission data says '{theatre}'."));
        if (missingResources > 0) checks.Add(new MissionReadinessCheck("warning", "Referenced resources missing", $"{missingResources} files listed by mapResource were not found in the archive."));
        if (unsafePaths > 0) checks.Add(new MissionReadinessCheck("error", "Unsafe archive paths", $"{unsafePaths} archive entries contain absolute or parent-relative paths."));
        if (dependencies.Any(item => item.Kind == "Module"))
            checks.Add(new MissionReadinessCheck("info", "Declared modules", "Additional modules are declared by the mission; Groundcrew does not assume every declaration is a hard client requirement."));
        return checks;
    }

    private static async Task<IReadOnlyList<string>> DetectFrameworksAsync(ZipArchive archive, string missionText, List<MissionReadinessCheck> notes)
    {
        var searchable = new StringBuilder(missionText.Length + 8192).Append(missionText).Append('\n');
        long scanned = 0;
        var skipped = 0;
        foreach (var entry in archive.Entries.Where(entry => entry.Length is > 0 and <= MaximumScannedScriptSize))
        {
            if (scanned >= 12L * 1024 * 1024) break;
            var extension = Path.GetExtension(entry.Name);
            if (!string.Equals(extension, ".lua", StringComparison.OrdinalIgnoreCase) && !entry.FullName.Contains("script", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                searchable.Append(entry.FullName).Append('\n').Append(await ReadTextAsync(entry, MaximumScannedScriptSize)).Append('\n');
                scanned += entry.Length;
            }
            catch { skipped++; }
        }

        if (skipped > 0)
            notes.Add(new MissionReadinessCheck("info", "Some embedded scripts were skipped", $"{skipped} script files could not be decoded. Framework detection continued with the remaining files."));

        var text = searchable.ToString();
        return FrameworkPatterns
            .Where(pattern => pattern.Needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            .Select(pattern => pattern.Name)
            .ToList();
    }

    private static int CountMissingResources(IReadOnlyDictionary<string, ZipArchiveEntry> entries, LuaDataValue? mapResources)
    {
        if (mapResources?.Fields is null) return 0;
        var missing = 0;
        foreach (var resource in mapResources.Fields.Values.Select(value => value.AsString()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = NormalizeArchivePath(resource!);
            if (!entries.ContainsKey(name) && !entries.ContainsKey($"l10n/default/{name}")) missing++;
        }
        return missing;
    }

    private static string ReadMissionDate(LuaDataValue mission)
    {
        var date = mission.Get("date");
        var year = date?.Get("Year")?.AsInteger() ?? date?.Get("year")?.AsInteger();
        var month = date?.Get("Month")?.AsInteger() ?? date?.Get("month")?.AsInteger();
        var day = date?.Get("Day")?.AsInteger() ?? date?.Get("day")?.AsInteger();
        if (year is null || month is null || day is null) return "Not specified";
        try { return new DateOnly(year.Value, month.Value, day.Value).ToString("dd MMM yyyy", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return "Invalid date"; }
    }

    private static string FormatStartTime(int? seconds)
    {
        if (seconds is null || seconds < 0) return "Not specified";
        var time = TimeSpan.FromSeconds(seconds.Value % 86400);
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}";
    }

    private static string FormatWeather(LuaDataValue? weather)
    {
        if (weather is null) return "Not specified";
        var temperature = weather.Get("season")?.Get("temperature")?.AsInteger();
        var windSpeed = weather.Get("wind")?.Get("atGround")?.Get("speed")?.AsInteger();
        var windDirection = weather.Get("wind")?.Get("atGround")?.Get("dir")?.AsInteger();
        var cloudDensity = weather.Get("clouds")?.Get("density")?.AsInteger();
        var parts = new List<string>();
        if (temperature is not null) parts.Add($"{temperature} °C");
        if (windSpeed is not null) parts.Add(windDirection is null ? $"wind {windSpeed} m/s" : $"wind {windSpeed} m/s at {windDirection}°");
        if (cloudDensity is not null) parts.Add(cloudDensity switch { <= 1 => "clear", <= 5 => "scattered clouds", <= 8 => "broken clouds", _ => "overcast" });
        return parts.Count == 0 ? "Not specified" : string.Join(" · ", parts);
    }

    private static string? ResolveLocalized(string? value, LuaDataValue? dictionary)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return dictionary?.Get(value)?.AsString() ?? value;
    }

    private static ZipArchiveEntry? FindEntry(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string name) => entries.GetValueOrDefault(NormalizeArchivePath(name));
    private static string NormalizeArchivePath(string path) => path.Replace('\\', '/').TrimStart('/');
    private static bool IsUnsafeArchivePath(string path) => Path.IsPathRooted(path) || NormalizeArchivePath(path).Split('/').Any(segment => segment == "..");
    private static string NormalizeName(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string DisplayTheatre(string theatre) => theatre switch
    {
        "PersianGulf" => "Persian Gulf",
        "MarianaIslands" => "Marianas",
        "Nevada" => "Nevada Test and Training Range",
        "Falklands" => "South Atlantic",
        "SinaiMap" => "Sinai",
        _ => theatre,
    };

    private static async Task<LuaDataValue?> ReadOptionalLuaTableAsync(ZipArchiveEntry? entry, string label, List<MissionReadinessCheck> notes)
    {
        if (entry is null) return null;
        try
        {
            var result = LuaDataParser.ParseAssignmentBestEffort(await ReadTextAsync(entry, MaximumDataFileSize));
            if (result.SkippedFields > 0)
                notes.Add(new MissionReadinessCheck("info", $"{label} partially read", $"Groundcrew ignored {result.SkippedFields} unsupported fields in this optional section."));
            return result.Value;
        }
        catch (Exception exception)
        {
            AddBestEffortNote(notes, $"{label} unavailable", label, exception);
            return null;
        }
    }

    private static async Task<string?> ReadOptionalTextAsync(ZipArchiveEntry? entry, string label, List<MissionReadinessCheck> notes)
    {
        if (entry is null) return null;
        try { return await ReadTextAsync(entry, 4096); }
        catch (Exception exception)
        {
            AddBestEffortNote(notes, $"{label} unavailable", label, exception);
            return null;
        }
    }

    private static T ReadBestEffort<T>(Func<T> read, T fallback, List<MissionReadinessCheck> notes, string title, string label)
    {
        try { return read(); }
        catch (Exception exception)
        {
            AddBestEffortNote(notes, title, label, exception);
            return fallback;
        }
    }

    private static void AddBestEffortNote(List<MissionReadinessCheck> notes, string title, string label, Exception exception) =>
        notes.Add(new MissionReadinessCheck("info", title, $"{label} could not be read ({Describe(exception)}). Groundcrew continued with the remaining mission data."));

    private static string Describe(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 180 ? message : $"{message[..177]}...";
    }

    private static async Task<string> ReadTextAsync(ZipArchiveEntry entry, long limit)
    {
        if (entry.Length > limit) throw new InvalidDataException($"Archive entry '{entry.FullName}' is larger than the allowed inspection limit.");
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }

    private static async Task<string> CalculateHashBestEffortAsync(string path)
    {
        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        }
        catch { return "unavailable"; }
    }

    private sealed record InstalledCatalog(bool Configured, HashSet<string> Terrains, HashSet<string> Modules)
    {
        public static InstalledCatalog Read(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return new(false, new(), new());
            var executableDirectory = Directory.GetParent(executablePath);
            var root = executableDirectory?.Name.StartsWith("bin", StringComparison.OrdinalIgnoreCase) == true ? executableDirectory.Parent?.FullName : executableDirectory?.FullName;
            if (root is null || !Directory.Exists(root)) return new(false, new(), new());
            return new(true, ReadDirectories(Path.Combine(root, "Mods", "terrains")), ReadDirectories(
                Path.Combine(root, "Mods", "aircraft"), Path.Combine(root, "Mods", "tech"),
                Path.Combine(root, "CoreMods", "aircraft"), Path.Combine(root, "CoreMods", "tech")));
        }

        public string TerrainStatus(string name)
        {
            if (!Configured) return "unknown";
            var normalized = NormalizeName(name);
            return Terrains.Any(item => item == normalized || item.Contains(normalized, StringComparison.Ordinal) || normalized.Contains(item, StringComparison.Ordinal)) ? "available" : "missing";
        }

        public string ModuleStatus(string name)
        {
            if (!Configured) return "declared";
            var normalized = NormalizeName(name);
            return Modules.Any(item => item == normalized || item.Contains(normalized, StringComparison.Ordinal) || normalized.Contains(item, StringComparison.Ordinal)) ? "available" : "declared";
        }

        private static HashSet<string> ReadDirectories(params string[] paths)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                try
                {
                    if (!Directory.Exists(path)) continue;
                    foreach (var directory in Directory.EnumerateDirectories(path))
                    {
                        var name = Path.GetFileName(directory);
                        if (!string.IsNullOrWhiteSpace(name)) names.Add(NormalizeName(name));
                    }
                }
                catch
                {
                    // Other DCS content folders can still be checked when one folder is inaccessible.
                }
            }
            return names;
        }
    }
}
