using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DcsDashboard.Api.Data;
using DcsDashboard.Api.Models;

namespace DcsDashboard.Api.Services;

public sealed class GrpcInstallerService
{
    private const string ReleasesApi = "https://api.github.com/repos/DCS-gRPC/rust-server/releases/latest";
    private const long MaximumDownloadSize = 128L * 1024 * 1024;
    private const long MaximumExpandedSize = 256L * 1024 * 1024;
    private const int MaximumArchiveEntries = 1000;
    private const string LoaderLine = @"dofile(lfs.writedir()..[[Scripts\DCS-gRPC\grpc-mission.lua]])";
    private readonly SettingsStore _settings;
    private readonly DcsProcessService _dcs;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GrpcInstallerService> _logger;
    private readonly string _logPath;
    private readonly object _logGate = new();
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly SemaphoreSlim _releaseGate = new(1, 1);
    private LatestRelease? _cachedRelease;
    private DateTimeOffset _releaseExpires;

    private static readonly InstallTarget[] InstallTargets =
    {
        new("Docs/DCS-gRPC", true),
        new("Missions/DCS-gRPC-Example.miz", false),
        new("Mods/tech/DCS-gRPC", true),
        new("Scripts/DCS-gRPC", true),
        new("Scripts/Hooks/DCS-gRPC.lua", false),
        new("Tools/DCS-gRPC", true),
    };

    public GrpcInstallerService(SettingsStore settings, DcsProcessService dcs, IHttpClientFactory httpClientFactory, IWebHostEnvironment environment, ILogger<GrpcInstallerService> logger)
    {
        _settings = settings;
        _dcs = dcs;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        var dataDirectory = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Groundcrew")
            : Path.Combine(environment.ContentRootPath, "data");
        _logPath = Path.Combine(dataDirectory, "Logs", "dcs-grpc-installer.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!); }
        catch (Exception exception) { _logger.LogWarning(exception, "Groundcrew could not create the DCS-gRPC installer log directory at {LogPath}.", _logPath); }
    }

    public GrpcInstallerLog GetLog()
    {
        lock (_logGate)
        {
            try
            {
                if (!File.Exists(_logPath)) return new(_logPath, Array.Empty<string>());
                return new(_logPath, File.ReadLines(_logPath).TakeLast(250).ToList());
            }
            catch (Exception exception) { return new(_logPath, new[] { $"Groundcrew could not read the installer log: {ShortMessage(exception)}" }); }
        }
    }

    public async Task<GrpcInstallationStatus> GetStatusAsync(bool includeLatest = true)
    {
        var settings = await _settings.GetAsync();
        var integration = settings.Integrations.FirstOrDefault(item => string.Equals(item.Id, "grpc", StringComparison.OrdinalIgnoreCase));
        var host = string.IsNullOrWhiteSpace(integration?.Host) ? "127.0.0.1" : integration.Host;
        var port = integration?.Port is > 0 and <= 65535 ? integration.Port.Value : 50051;
        var savedGamesPath = Path.IsPathFullyQualified(settings.SavedGamesPath) ? Path.GetFullPath(settings.SavedGamesPath) : "";
        var missionScriptingPath = ResolveMissionScriptingPath(settings.DcsExecutablePath) ?? "";
        var versionPath = string.IsNullOrWhiteSpace(savedGamesPath) ? "" : Path.Combine(savedGamesPath, "Scripts", "DCS-gRPC", "version.lua");
        var installedVersion = ReadInstalledVersion(versionPath);
        var installed = !string.IsNullOrWhiteSpace(savedGamesPath)
            && File.Exists(Path.Combine(savedGamesPath, "Scripts", "DCS-gRPC", "grpc-mission.lua"))
            && File.Exists(Path.Combine(savedGamesPath, "Scripts", "Hooks", "DCS-gRPC.lua"))
            && File.Exists(Path.Combine(savedGamesPath, "Mods", "tech", "DCS-gRPC", "dcs_grpc.dll"));
        var loaderConfigured = ContainsLoader(missionScriptingPath);
        var configPath = string.IsNullOrWhiteSpace(savedGamesPath) ? "" : Path.Combine(savedGamesPath, "Config", "dcs-grpc.lua");
        var autostartConfigured = ContainsAutostart(configPath);
        var requirements = new List<string>();
        if (string.IsNullOrWhiteSpace(savedGamesPath) || !Directory.Exists(savedGamesPath)) requirements.Add("Choose an existing DCS Saved Games folder in Settings.");
        if (string.IsNullOrWhiteSpace(missionScriptingPath) || !File.Exists(missionScriptingPath)) requirements.Add("Choose the DCS server executable so Scripts\\MissionScripting.lua can be found.");

        LatestRelease? latest = null;
        string? releaseError = null;
        if (includeLatest)
        {
            try { latest = await GetLatestReleaseAsync(); }
            catch (Exception exception) { releaseError = $"Latest release could not be checked: {ShortMessage(exception)}"; }
        }
        var running = installed && await IsListeningAsync(host, port);
        return new GrpcInstallationStatus(
            installed,
            running,
            loaderConfigured,
            autostartConfigured,
            requirements.Count == 0,
            installedVersion,
            latest?.Version,
            IsNewer(latest?.Version, installedVersion),
            host,
            port,
            savedGamesPath,
            missionScriptingPath,
            latest?.PublishedAt.ToString("O"),
            requirements.Count > 0 ? string.Join(" ", requirements) : releaseError);
    }

    public async Task<GrpcInstallationResult> InstallLatestAsync()
    {
        var installationId = Guid.NewGuid().ToString("N")[..8];
        LogInformation(installationId, "Install requested.");
        await _installGate.WaitAsync();
        try
        {
            var settings = await _settings.GetAsync();
            var savedGamesPath = RequireSavedGamesPath(settings.SavedGamesPath);
            var missionScriptingCandidates = GetMissionScriptingCandidates(settings.DcsExecutablePath);
            foreach (var candidate in missionScriptingCandidates) LogInformation(installationId, $"DCS loader candidate: {candidate}");
            var missionScriptingPath = ResolveMissionScriptingPath(settings.DcsExecutablePath)
                ?? throw new InvalidOperationException("Choose a valid DCS server executable in Settings before installing DCS-gRPC.");
            LogInformation(installationId, $"Saved Games destination: {savedGamesPath}");
            LogInformation(installationId, $"DCS loader file: {missionScriptingPath}");
            if (!File.Exists(missionScriptingPath)) throw new InvalidOperationException($"MissionScripting.lua was not found at '{missionScriptingPath}'.");
            var integration = settings.Integrations.First(item => string.Equals(item.Id, "grpc", StringComparison.OrdinalIgnoreCase));
            var host = string.IsNullOrWhiteSpace(integration.Host) ? "127.0.0.1" : integration.Host;
            var port = integration.Port is > 0 and <= 65535 ? integration.Port.Value : 50051;
            var release = await GetLatestReleaseAsync(forceRefresh: true);
            LogInformation(installationId, $"Resolved official release {release.Version}: {release.AssetName} ({release.Size} bytes).");
            var temporaryZip = Path.Combine(Path.GetTempPath(), $"groundcrew-dcs-grpc-{Guid.NewGuid():N}.zip");
            var stagingPath = Path.Combine(savedGamesPath, $".groundcrew-grpc-staging-{Guid.NewGuid():N}");
            var backupPath = Path.Combine(savedGamesPath, "Groundcrew Backups", "DCS-gRPC", DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
            var dcsWasRunning = false;
            var installationCommitted = false;
            string? sha256 = null;
            string? warning = null;

            try
            {
                LogInformation(installationId, "Downloading release archive from the official DCS-gRPC GitHub repository.");
                sha256 = await DownloadAsync(release, temporaryZip);
                LogInformation(installationId, $"Download completed and size verified. SHA-256: {sha256}");
                ExtractAndValidate(temporaryZip, stagingPath);
                LogInformation(installationId, "Archive paths and required package files validated.");
                dcsWasRunning = await _dcs.FindAsync() is not null;
                if (dcsWasRunning)
                {
                    LogInformation(installationId, "Stopping the running DCS server before changing loader and DLL files.");
                    await _dcs.StopAsync();
                }
                InstallStagedFiles(stagingPath, savedGamesPath, backupPath, missionScriptingPath, host, port, detail => LogInformation(installationId, detail));
                installationCommitted = true;
                integration.ConfigPath = Path.Combine(savedGamesPath, "Config", "dcs-grpc.lua");
                integration.Host = host;
                integration.Port = port;
                try { await _settings.SaveAsync(settings); LogInformation(installationId, $"Saved gRPC endpoint {host}:{port}."); }
                catch (Exception exception)
                {
                    warning = $"DCS-gRPC was installed, but its Groundcrew settings could not be saved: {ShortMessage(exception)}";
                    LogError(installationId, "DCS-gRPC files were installed, but Groundcrew endpoint settings could not be saved.", exception);
                }
            }
            catch (Exception exception)
            {
                LogError(installationId, "Installation failed; any staged or applied package changes were rolled back.", exception);
                if (dcsWasRunning)
                {
                    try { await _dcs.StartAsync(); LogInformation(installationId, "Restored the previously running DCS server after failure."); }
                    catch (Exception restartException) { LogError(installationId, "DCS could not be restarted after the failed installation.", restartException); }
                }
                throw;
            }
            finally
            {
                TryDeleteFile(temporaryZip);
                TryDeleteDirectory(stagingPath);
            }

            var restarted = false;
            if (dcsWasRunning && installationCommitted)
            {
                try { await _dcs.StartAsync(); restarted = true; LogInformation(installationId, "Restarted DCS after installation."); }
                catch (Exception exception)
                {
                    var restartWarning = $"DCS-gRPC was installed, but DCS could not be restarted: {ShortMessage(exception)}";
                    warning = string.IsNullOrWhiteSpace(warning) ? restartWarning : $"{warning} {restartWarning}";
                    LogError(installationId, "DCS-gRPC was installed, but DCS could not be restarted.", exception);
                }
            }
            var status = await GetStatusAsync();
            var retainedBackup = Directory.Exists(backupPath) && Directory.EnumerateFileSystemEntries(backupPath).Any() ? backupPath : null;
            if (retainedBackup is null) TryDeleteDirectory(backupPath);
            LogInformation(installationId, $"DCS-gRPC {release.Version} installation completed.{(retainedBackup is null ? " No previous files required retention." : $" Backup retained at {retainedBackup}.")}");
            return new GrpcInstallationResult(status, release.Version, sha256!, retainedBackup, restarted, warning);
        }
        catch (Exception exception)
        {
            LogError(installationId, "Install request ended with an error.", exception);
            throw;
        }
        finally { _installGate.Release(); }
    }

    private async Task<LatestRelease> GetLatestReleaseAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedRelease is not null && _releaseExpires > DateTimeOffset.UtcNow) return _cachedRelease;
        await _releaseGate.WaitAsync();
        try
        {
            if (!forceRefresh && _cachedRelease is not null && _releaseExpires > DateTimeOffset.UtcNow) return _cachedRelease;
            var client = _httpClientFactory.CreateClient("github-releases");
            using var response = await client.GetAsync(ReleasesApi);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            var version = root.GetProperty("tag_name").GetString()?.TrimStart('v')
                ?? throw new InvalidDataException("The latest DCS-gRPC release does not have a version tag.");
            var expectedName = $"DCS-gRPC-{version}.zip";
            var asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(item => string.Equals(item.GetProperty("name").GetString(), expectedName, StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException($"The latest release does not contain the expected '{expectedName}' asset.");
            var urlText = asset.GetProperty("browser_download_url").GetString();
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps
                || !string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || !url.AbsolutePath.StartsWith("/DCS-gRPC/rust-server/releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException("The release asset URL does not point to the official DCS-gRPC GitHub repository.");
            var size = asset.GetProperty("size").GetInt64();
            if (size is <= 0 or > MaximumDownloadSize) throw new InvalidDataException("The release asset size is outside Groundcrew's download limits.");
            var publishedAt = root.GetProperty("published_at").GetDateTimeOffset();
            _cachedRelease = new LatestRelease(version, expectedName, url, size, publishedAt);
            _releaseExpires = DateTimeOffset.UtcNow.AddMinutes(15);
            return _cachedRelease;
        }
        finally { _releaseGate.Release(); }
    }

    private async Task<string> DownloadAsync(LatestRelease release, string destination)
    {
        var client = _httpClientFactory.CreateClient("github-releases");
        using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumDownloadSize) throw new InvalidDataException("The DCS-gRPC download exceeds Groundcrew's size limit.");
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Open(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer);
            if (read == 0) break;
            total += read;
            if (total > MaximumDownloadSize) throw new InvalidDataException("The DCS-gRPC download exceeds Groundcrew's size limit.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read));
        }
        if (total != release.Size) throw new InvalidDataException($"The downloaded asset size ({total} bytes) does not match GitHub's release metadata ({release.Size} bytes).");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void ExtractAndValidate(string archivePath, string stagingPath)
    {
        Directory.CreateDirectory(stagingPath);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumArchiveEntries) throw new InvalidDataException("The DCS-gRPC archive contains too many entries.");
        if (archive.Entries.Sum(entry => entry.Length) > MaximumExpandedSize) throw new InvalidDataException("The expanded DCS-gRPC archive is larger than 256 MB.");
        var names = archive.Entries.Select(entry => NormalizeZipPath(entry.FullName)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[] { "Scripts/DCS-gRPC/grpc-mission.lua", "Scripts/Hooks/DCS-gRPC.lua", "Mods/tech/DCS-gRPC/dcs_grpc.dll", "Scripts/DCS-gRPC/version.lua" })
            if (!names.Contains(required)) throw new InvalidDataException($"The DCS-gRPC archive is missing required file '{required}'.");

        var root = Path.GetFullPath(stagingPath) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var relative = NormalizeZipPath(entry.FullName);
            if (string.IsNullOrWhiteSpace(relative)) continue;
            if (!IsAllowedArchivePath(relative)) throw new InvalidDataException($"Unexpected path '{relative}' in the DCS-gRPC release archive.");
            var destination = Path.GetFullPath(Path.Combine(stagingPath, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The DCS-gRPC archive contains an unsafe path.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static void InstallStagedFiles(string stagingPath, string savedGamesPath, string backupPath, string missionScriptingPath, string host, int port, Action<string> log)
    {
        Directory.CreateDirectory(backupPath);
        var movedTargets = new List<(string Destination, string? Backup, bool Directory)>();
        var missionBackup = Path.Combine(backupPath, "DCS installation", "Scripts", "MissionScripting.lua");
        var configPath = Path.Combine(savedGamesPath, "Config", "dcs-grpc.lua");
        var configBackup = Path.Combine(backupPath, "Saved Games", "Config", "dcs-grpc.lua");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(missionBackup)!);
            File.Copy(missionScriptingPath, missionBackup, overwrite: false);
            log($"Backed up DCS loader to {missionBackup}.");
            if (File.Exists(configPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configBackup)!);
                File.Copy(configPath, configBackup, overwrite: false);
            }

            foreach (var target in InstallTargets)
            {
                var source = CombineRelative(stagingPath, target.RelativePath);
                if (target.Directory ? !Directory.Exists(source) : !File.Exists(source)) continue;
                var destination = CombineRelative(savedGamesPath, target.RelativePath);
                string? backup = null;
                if (Directory.Exists(destination) || File.Exists(destination))
                {
                    backup = CombineRelative(backupPath, Path.Combine("Previous package", target.RelativePath));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    if (target.Directory) Directory.Move(destination, backup); else File.Move(destination, backup);
                }
                movedTargets.Add((destination, backup, target.Directory));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (target.Directory) Directory.Move(source, destination); else File.Move(source, destination);
                log($"Installed {target.RelativePath} into Saved Games.");
            }

            ConfigureMissionScripting(missionScriptingPath);
            log($"Configured DCS loader at {missionScriptingPath}.");
            ConfigureAutostart(configPath, host, port);
            log($"Configured gRPC autostart at {configPath}.");
        }
        catch
        {
            log("A package or loader step failed; restoring previous files.");
            foreach (var moved in movedTargets.AsEnumerable().Reverse())
            {
                if (moved.Directory) TryDeleteDirectory(moved.Destination); else TryDeleteFile(moved.Destination);
                if (moved.Backup is null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(moved.Destination)!);
                if (moved.Directory && Directory.Exists(moved.Backup)) Directory.Move(moved.Backup, moved.Destination);
                else if (!moved.Directory && File.Exists(moved.Backup)) File.Move(moved.Backup, moved.Destination);
            }
            if (File.Exists(missionBackup)) File.Copy(missionBackup, missionScriptingPath, overwrite: true);
            if (File.Exists(configBackup)) File.Copy(configBackup, configPath, overwrite: true);
            else TryDeleteFile(configPath);
            throw;
        }
    }

    private static void ConfigureMissionScripting(string path)
    {
        var content = File.ReadAllText(path);
        if (content.Contains(@"Scripts\DCS-gRPC\grpc-mission.lua", StringComparison.OrdinalIgnoreCase)
            || content.Contains("Scripts/DCS-gRPC/grpc-mission.lua", StringComparison.OrdinalIgnoreCase)) return;
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var anchor = new Regex("(?m)^(?<indent>[ \\t]*)dofile\\s*\\(\\s*(?<quote>['\"])Scripts[/\\\\]ScriptingSystem\\.lua\\k<quote>\\s*\\)[ \\t]*(?:--[^\\r\\n]*)?$", RegexOptions.CultureInvariant);
        if (anchor.IsMatch(content))
            content = anchor.Replace(content, match => $"{match.Value}{newline}{match.Groups["indent"].Value}{LoaderLine}", 1);
        else
        {
            var sanitization = new Regex("(?m)^(?<indent>[ \\t]*)local\\s+function\\s+sanitizeModule\\s*\\(", RegexOptions.CultureInvariant);
            if (!sanitization.IsMatch(content)) throw new InvalidDataException("Groundcrew could not find a safe pre-sanitization loader position in MissionScripting.lua. No installation files were retained.");
            content = sanitization.Replace(content, match => $"{match.Groups["indent"].Value}{LoaderLine}{newline}{match.Value}", 1);
        }
        WriteAtomic(path, content);
    }

    private static void ConfigureAutostart(string path, string host, int port)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            var content = $"-- Managed initially by Groundcrew. Additional DCS-gRPC options may be added below.\r\nautostart = true\r\nevalEnabled = false\r\nhost = {LuaString(host)}\r\nport = {port}\r\ndebug = false\r\nthroughputLimit = 600\r\nintegrityCheckDisabled = false\r\n";
            WriteAtomic(path, content);
            return;
        }
        var existing = File.ReadAllText(path);
        existing = SetLuaAssignment(existing, "autostart", "true");
        existing = SetLuaAssignment(existing, "host", LuaString(host));
        existing = SetLuaAssignment(existing, "port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteAtomic(path, existing);
    }

    private static string SetLuaAssignment(string content, string key, string value)
    {
        var assignment = new Regex($@"(?m)^(?<indent>[ \t]*){Regex.Escape(key)}\s*=\s*[^\r\n]*(?:\r?$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return assignment.IsMatch(content)
            ? assignment.Replace(content, $"${{indent}}{key} = {value}", 1)
            : $"{key} = {value}\r\n{content}";
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = $"{path}.groundcrew-tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static bool ContainsLoader(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var content = File.ReadAllText(path);
            return content.Contains(@"Scripts\DCS-gRPC\grpc-mission.lua", StringComparison.OrdinalIgnoreCase)
                || content.Contains("Scripts/DCS-gRPC/grpc-mission.lua", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool ContainsAutostart(string path)
    {
        try { return File.Exists(path) && Regex.IsMatch(File.ReadAllText(path), @"(?m)^\s*autostart\s*=\s*true\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
        catch { return false; }
    }

    private static string? ReadInstalledVersion(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var match = Regex.Match(File.ReadAllText(path), "GRPC\\.version\\s*=\\s*['\"](?<version>[^'\"]+)['\"]", RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["version"].Value : null;
        }
        catch { return null; }
    }

    private static async Task<bool> IsListeningAsync(string host, int port)
    {
        var probeHost = host is "0.0.0.0" or "::" ? "127.0.0.1" : host;
        using var client = new TcpClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        try { await client.ConnectAsync(probeHost, port, cancellation.Token); return true; }
        catch { return false; }
    }

    private static string RequireSavedGamesPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new InvalidOperationException("Choose an existing DCS Saved Games folder in Settings before installing DCS-gRPC.");
        return Path.GetFullPath(path);
    }

    private static string? ResolveMissionScriptingPath(string executablePath)
    {
        var candidates = GetMissionScriptingCandidates(executablePath);
        if (candidates.Count == 0) return null;
        var existing = candidates.FirstOrDefault(File.Exists);
        if (existing is not null) return existing;
        var executableDirectory = new FileInfo(executablePath).Directory!;
        var installRoot = executableDirectory.Name.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ? executableDirectory.Parent : executableDirectory;
        return installRoot is null ? null : Path.Combine(installRoot.FullName, "Scripts", "MissionScripting.lua");
    }

    private static IReadOnlyList<string> GetMissionScriptingCandidates(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath)) return Array.Empty<string>();
        var executableDirectory = new FileInfo(executablePath).Directory;
        if (executableDirectory is null) return Array.Empty<string>();
        var candidates = new List<string>();
        for (var directory = executableDirectory; directory is not null && candidates.Count < 4; directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, "Scripts", "MissionScripting.lua"));
        return candidates;
    }

    private static bool IsNewer(string? latest, string? installed)
    {
        if (latest is null || installed is null) return false;
        return Version.TryParse(latest, out var latestVersion) && Version.TryParse(installed, out var installedVersion)
            ? latestVersion > installedVersion
            : !string.Equals(latest, installed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedArchivePath(string path)
    {
        if (path.EndsWith("/", StringComparison.Ordinal))
        {
            var directory = path.TrimEnd('/');
            if (InstallTargets.Any(target => target.RelativePath.StartsWith($"{directory}/", StringComparison.OrdinalIgnoreCase))) return true;
        }
        return InstallTargets.Any(target =>
            target.Directory
                ? path.Equals(target.RelativePath, StringComparison.OrdinalIgnoreCase) || path.StartsWith($"{target.RelativePath}/", StringComparison.OrdinalIgnoreCase)
                : path.Equals(target.RelativePath, StringComparison.OrdinalIgnoreCase));
    }
    private static string NormalizeZipPath(string path) => path.Replace('\\', '/').TrimStart('/');
    private static string CombineRelative(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    private static string LuaString(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    private static string ShortMessage(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 220 ? message : $"{message[..217]}...";
    }
    private void LogInformation(string installationId, string message)
    {
        _logger.LogInformation("DCS-gRPC installer [{InstallationId}]: {Message}", installationId, message);
        AppendLog("INFO", installationId, message);
    }
    private void LogError(string installationId, string message, Exception exception)
    {
        _logger.LogError(exception, "DCS-gRPC installer [{InstallationId}]: {Message}", installationId, message);
        AppendLog("ERROR", installationId, $"{message} {exception.GetType().Name}: {ShortMessage(exception)}");
    }
    private void AppendLog(string level, string installationId, string message)
    {
        lock (_logGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 2 * 1024 * 1024)
                {
                    var previous = $"{_logPath}.1";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(_logPath, previous);
                }
                File.AppendAllText(_logPath, $"{DateTimeOffset.Now:O} [{level}] [{installationId}] {message}{Environment.NewLine}", new UTF8Encoding(false));
            }
            catch { }
        }
    }
    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

    private sealed record LatestRelease(string Version, string AssetName, Uri DownloadUrl, long Size, DateTimeOffset PublishedAt);
    private sealed record InstallTarget(string RelativePath, bool Directory);
}
