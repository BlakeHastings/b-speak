using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BSpeak.Host;

/// <summary>Update settings (the <c>updates</c> section of config.json).</summary>
public sealed class UpdateOptions
{
    /// <summary>Check GitHub for a newer release (at most once a day, in the background).</summary>
    public bool Check { get; set; } = true;

    /// <summary>The GitHub repository releases come from.</summary>
    public string Repo { get; set; } = "BlakeHastings/b-speak";
}

/// <summary>Where the skill was installed (~/.bspeak/install.json), so updates can refresh it.</summary>
public sealed class InstallInfo
{
    /// <summary>Folders the skill was copied into; updates refresh them.</summary>
    public List<string> SkillDirs { get; set; } = [];

    public static string Path => System.IO.Path.Combine(Profile.Home, "install.json");

    public static InstallInfo Load()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<InstallInfo>(File.ReadAllText(Path), Profile.Json) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Profile.Home);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Profile.Json));
    }
}

public sealed record ReleaseAsset([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("browser_download_url")] string Url);
public sealed record Release([property: JsonPropertyName("tag_name")] string Tag, [property: JsonPropertyName("html_url")] string Url, [property: JsonPropertyName("assets")] List<ReleaseAsset> Assets)
{
    public string Version => Tag.TrimStart('v');
}

public sealed record UpdateStatus(string Current, string? Latest, bool Available, string? ReleaseUrl, DateTimeOffset? CheckedAt, string Method);

/// <summary>Finds newer releases on GitHub and installs them in place.</summary>
public static class Updates
{
    private sealed record Cache(string Latest, string Url, DateTimeOffset CheckedAt);

    private static string CachePath => Path.Combine(Profile.Home, "update.json");

    /// <summary>"release" for a downloaded build (its archive carries a bspeak.release marker), "source" for a local build.</summary>
    public static string InstallMethod => File.Exists(Path.Combine(AppContext.BaseDirectory, "bspeak.release")) ? "release" : "source";

    /// <summary>This build's version, from the git tag it was built from (MinVer), without build metadata.</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = typeof(Updates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    /// <summary>True when <paramref name="candidate"/> is a newer semantic version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static (Version Core, bool Pre) Parse(string s)
        {
            s = s.TrimStart('v');
            var dash = s.IndexOf('-');
            var core = dash >= 0 ? s[..dash] : s;
            return (Version.TryParse(core, out var v) ? v : new Version(0, 0, 0), dash >= 0);
        }
        var (a, aPre) = Parse(candidate);
        var (b, bPre) = Parse(current);
        var cmp = a.CompareTo(b);
        return cmp > 0 || (cmp == 0 && bPre && !aPre);
    }

    /// <summary>The cached answer, refreshed from GitHub when older than a day (or when <paramref name="force"/>). Never throws.</summary>
    public static async Task<UpdateStatus> CheckAsync(UpdateOptions options, bool force = false, CancellationToken ct = default)
    {
        var method = InstallMethod;
        var cache = ReadCache();
        if (force || cache is null || DateTimeOffset.UtcNow - cache.CheckedAt > TimeSpan.FromDays(1))
        {
            try
            {
                var release = await LatestReleaseAsync(options, ct);
                cache = new Cache(release.Version, release.Url, DateTimeOffset.UtcNow);
                File.WriteAllText(CachePath, JsonSerializer.Serialize(cache, Profile.Json));
            }
            catch { /* offline or rate limited: keep what we had */ }
        }
        var current = CurrentVersion;
        return new UpdateStatus(current, cache?.Latest, cache is not null && IsNewer(cache.Latest, current), cache?.Url, cache?.CheckedAt, method);
    }

    /// <summary>What the brief tells an agent when an update is waiting, or null.</summary>
    public static string? Notice(UpdateStatus status)
    {
        if (!status.Available) return null;
        var how = status.Method == "source"
            ? "This copy was built from source: if they agree, update the clone (git pull) and rerun scripts/install-from-source.sh."
            : "If they agree, run `bspeak update`. It stops the host, installs the new version and refreshes the skill, then exits; after that, join the room again and carry on where you were.";
        return $"""
            ## Update available

            b-speak {status.Latest} is out; this is {status.Current}. Before going further, ask the user whether they want to update now. {how} If they decline, continue as normal.
            """;
    }

    private static Cache? ReadCache()
    {
        try { return File.Exists(CachePath) ? JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath), Profile.Json) : null; }
        catch { return null; }
    }

    private static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("b-speak-updater");
        return http;
    }

    public static async Task<Release> LatestReleaseAsync(UpdateOptions options, CancellationToken ct)
    {
        using var http = Http();
        http.Timeout = TimeSpan.FromSeconds(10);
        var json = await http.GetStringAsync($"https://api.github.com/repos/{options.Repo}/releases/latest", ct);
        return ParseRelease(json);
    }

    /// <summary>Reads a GitHub "latest release" response.</summary>
    public static Release ParseRelease(string json)
    {
        var release = JsonSerializer.Deserialize<Release>(json) ?? throw new InvalidDataException("Empty release.");
        return release with { Assets = release.Assets ?? [] };
    }

    /// <summary>The release platform name for this machine, as used in asset names.</summary>
    public static string Platform
    {
        get
        {
            var arch = RuntimeInformation.OSArchitecture switch { Architecture.Arm64 => "arm64", _ => "x64" };
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            return $"{os}-{arch}";
        }
    }

    /// <summary>
    /// Downloads the latest release for this platform, verifies its checksum, and swaps it into the folder
    /// this program runs from. Files in use (this process, an agent's wait) are renamed aside rather than
    /// overwritten, which works even on Windows; the leftovers are deleted on a later run.
    /// </summary>
    public static async Task<Release> ApplyAsync(UpdateOptions options, ILogger logger, Func<Task> stopHost, CancellationToken ct = default)
    {
        var release = await LatestReleaseAsync(options, ct);
        var archiveName = $"bspeak-{release.Version}-{Platform}{(OperatingSystem.IsWindows() ? ".zip" : ".tar.gz")}";
        var archive = release.Assets.FirstOrDefault(a => a.Name == archiveName)
            ?? throw new InvalidOperationException($"Release {release.Tag} has no build for {Platform} ({archiveName}).");
        var sums = release.Assets.FirstOrDefault(a => a.Name == "SHA256SUMS")
            ?? throw new InvalidOperationException($"Release {release.Tag} has no SHA256SUMS.");

        var staging = Path.Combine(Profile.Home, "staging", release.Version);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        var download = Path.Combine(staging, archiveName);

        using (var http = Http())
        {
            logger.LogInformation("Downloading {Archive}", archiveName);
            await using (var src = await http.GetStreamAsync(archive.Url, ct))
            await using (var dst = File.Create(download))
                await src.CopyToAsync(dst, ct);
            var expected = (await http.GetStringAsync(sums.Url, ct)).Split('\n')
                .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == archiveName)?[0]
                ?? throw new InvalidDataException($"SHA256SUMS has no entry for {archiveName}.");
            await using var file = File.OpenRead(download);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{archiveName} failed its checksum.");
        }

        var unpacked = Path.Combine(staging, "files");
        if (archiveName.EndsWith(".zip")) ZipFile.ExtractToDirectory(download, unpacked);
        else
        {
            Directory.CreateDirectory(unpacked);
            await using var gz = new GZipStream(File.OpenRead(download), CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gz, unpacked, overwriteFiles: true, ct);
        }

        await stopHost();
        Swap(unpacked, AppContext.BaseDirectory);
        RefreshSkills(Path.Combine(AppContext.BaseDirectory, "skill"));
        try { Directory.Delete(Path.Combine(Profile.Home, "staging"), recursive: true); } catch { }
        return release;
    }

    /// <summary>Moves every file from <paramref name="from"/> into <paramref name="to"/>, renaming files in use aside first.</summary>
    public static void Swap(string from, string to)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        foreach (var source in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                try { File.Delete(target); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { File.Move(target, $"{target}.old-{stamp}"); }
            }
            File.Move(source, target);
            if (!OperatingSystem.IsWindows() && Path.GetFileName(target) == "bspeak")
                File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>Deletes files a previous update renamed aside, where nothing holds them any more.</summary>
    public static void CleanUpOldFiles()
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.old-*", SearchOption.AllDirectories))
                try { File.Delete(old); } catch { }
        }
        catch { }
    }

    /// <summary>Copies the skill shipped with this build into every folder it was installed to.</summary>
    public static void RefreshSkills(string shippedSkillDir)
    {
        var source = Path.Combine(shippedSkillDir, "b-speak");
        if (!Directory.Exists(source)) return;
        foreach (var dir in InstallInfo.Load().SkillDirs)
            CopySkill(source, dir);
    }

    /// <summary>Copies the skill folder into <paramref name="skillsRoot"/>/b-speak and records it for updates.</summary>
    public static string InstallSkill(string skillsRoot)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "skill", "b-speak");
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"This build has no bundled skill ({source}).");
        var target = Path.Combine(skillsRoot, "b-speak");
        CopySkill(source, target);
        var info = InstallInfo.Load();
        if (!info.SkillDirs.Contains(target, StringComparer.OrdinalIgnoreCase)) info.SkillDirs.Add(target);
        info.Save();
        return target;
    }

    private static void CopySkill(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
