using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DcsDashboard.Api.Services;

public static partial class DcsVersionReader
{
    public static string? Read(string executablePath, string? savedGamesPath = null)
    {
        var executableVersion = ReadExecutableVersion(executablePath);
        var configurationVersion = ReadConfigurationVersion(executablePath);
        var logVersion = ReadLogVersion(savedGamesPath);
        return new[] { configurationVersion, logVersion, executableVersion }
            .Where(version => version is not null)
            .Where(version => executableVersion is null || SameCoreBuild(executableVersion, version))
            .OrderByDescending(SegmentCount)
            .ThenBy(version => version == configurationVersion ? 0 : version == logVersion ? 1 : 2)
            .FirstOrDefault() ?? executableVersion;
    }

    public static bool SameCoreBuild(string? left, string? right)
    {
        var leftParts = Parts(left);
        var rightParts = Parts(right);
        if (leftParts.Length < 4 || rightParts.Length < 4) return false;
        return leftParts.Take(4).SequenceEqual(rightParts.Take(4), StringComparer.Ordinal);
    }

    private static string? ReadExecutableVersion(string executablePath)
    {
        try
        {
            var information = FileVersionInfo.GetVersionInfo(executablePath);
            return new[] { Normalize(information.ProductVersion), Normalize(information.FileVersion) }
                .Where(version => version is not null)
                .OrderByDescending(SegmentCount)
                .ThenByDescending(version => version!.Length)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static string? ReadConfigurationVersion(string executablePath)
    {
        try
        {
            var binDirectory = Path.GetDirectoryName(executablePath);
            var installRoot = binDirectory is null ? null : Directory.GetParent(binDirectory)?.FullName;
            var path = installRoot is null ? null : Path.Combine(installRoot, "autoupdate.cfg");
            if (path is null || !File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                ? Normalize(version.GetString())
                : null;
        }
        catch { return null; }
    }

    private static string? ReadLogVersion(string? savedGamesPath)
    {
        if (string.IsNullOrWhiteSpace(savedGamesPath)) return null;
        var path = Path.Combine(savedGamesPath, "Logs", "dcs.log");
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            for (var lineNumber = 0; lineNumber < 500 && !reader.EndOfStream; lineNumber++)
            {
                var line = reader.ReadLine();
                var match = DcsLogVersionRegex().Match(line ?? "");
                if (match.Success) return match.Groups[1].Value;
            }
        }
        catch { }
        return null;
    }

    private static string? Normalize(string? value)
    {
        var match = VersionNumberRegex().Match(value ?? "");
        return match.Success ? match.Value : null;
    }

    private static string[] Parts(string? version) => Normalize(version)?.Split('.', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
    private static int SegmentCount(string? version) => Parts(version).Length;

    [GeneratedRegex(@"\bDCS/([0-9]+(?:\.[0-9]+){2,})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DcsLogVersionRegex();
    [GeneratedRegex(@"[0-9]+(?:\.[0-9]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumberRegex();
}
