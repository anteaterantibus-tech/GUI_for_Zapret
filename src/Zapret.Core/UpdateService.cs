using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zapret.Core;

public static class AppInfo
{
    public const string Name = "Zapret Control";
    public const string Version = "1.0.13";

    /// <summary>
    /// The portable build is the exe whose file name carries the Portable suffix.
    /// The same binary is shipped as ZapretControl-<version>-Portable.exe for direct
    /// use and as ZapretControl.exe inside the installer package.
    /// </summary>
    public static bool IsPortable =>
        ExecutableName.Contains("Portable", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Zapret Control Portable" for the portable exe, "Zapret Control" otherwise.</summary>
    public static string DisplayName => IsPortable ? Name + " Portable" : Name;

    private static string ExecutableName
    {
        get
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                try { path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName; }
                catch { path = null; }
            }
            return System.IO.Path.GetFileNameWithoutExtension(path ?? "");
        }
    }

    /// <summary>Upstream distribution this build targets.</summary>
    public const string UpstreamRepo = "Flowseal/zapret-discord-youtube";

    /// <summary>Filled in once we publish our own releases (self-update feed).</summary>
    public const string SelfRepo = "";
}

public sealed class ReleaseInfo
{
    public required string Tag { get; init; }
    public string Name { get; init; } = "";
    public string Body { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public string ZipUrl { get; init; } = "";
    public DateTimeOffset PublishedAt { get; init; }
}

public sealed class UpdateOptions
{
    /// <summary>Keep ACTIVE_DISCORD_UDP.bin / ACTIVE_GAME_UDP.bin replaced by the user.</summary>
    public bool PreserveActiveFakes { get; set; } = true;

    /// <summary>Keep the current IPSet mode (none/any/loaded) instead of the archive one.</summary>
    public bool PreserveIpSet { get; set; } = true;

    /// <summary>Keep utils/game_filter.enabled and the user list files.</summary>
    public bool PreserveUserFiles { get; set; } = true;

    /// <summary>How to handle lists/*.txt and utils/targets.txt edited by hand.</summary>
    public ListMergeMode ListMode { get; set; } = ListMergeMode.Merge;

    public bool RestartAfterUpdate { get; set; } = true;
}

public sealed class UpdateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string? OldVersion { get; set; }
    public string? NewVersion { get; set; }
    public string? BackupPath { get; set; }
    public bool ServiceWasInstalled { get; set; }
    public string? RestartStrategy { get; set; }
}

/// <summary>
/// In-app updater for the upstream distribution.
///
/// Upstream itself has no binary auto-update: service.bat compares a hardcoded
/// LOCAL_VERSION with .service/version.txt and opens the release page in a browser.
/// This service does the whole job: check, download the zip asset, validate the
/// archive layout, keep user data, replace the files, roll back on failure,
/// restore the service and the running strategy.
/// </summary>
public sealed class UpdateService
{
    private const string VersionUrl = "https://raw.githubusercontent.com/" + AppInfo.UpstreamRepo + "/main/.service/version.txt";
    private const string IpSetUrl = "https://raw.githubusercontent.com/" + AppInfo.UpstreamRepo + "/main/.service/ipset-service.txt";
    private const string HostsUrl = "https://raw.githubusercontent.com/" + AppInfo.UpstreamRepo + "/main/.service/hosts";

    private readonly HttpClient _api;
    private readonly HttpClient _download;

    public UpdateService()
    {
        var apiHandler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        _api = new HttpClient(apiHandler) { Timeout = TimeSpan.FromSeconds(20) };
        _api.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretControl/" + AppInfo.Version);
        _api.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var downloadHandler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        _download = new HttpClient(downloadHandler) { Timeout = TimeSpan.FromMinutes(10) };
        _download.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretControl/" + AppInfo.Version);
    }

    public const string ReleasesApi = "https://api.github.com/repos/" + AppInfo.UpstreamRepo + "/releases";

    /// <summary>Version stored in the legacy launcher, e.g. 1.10.4.</summary>
    public static string? GetInstalledVersion(ZapretPaths paths)
    {
        var service = Path.Combine(paths.Root, "service.bat");
        if (!File.Exists(service)) return null;

        var match = Regex.Match(File.ReadAllText(service), "LOCAL_VERSION=([^\"\\r\\n]+)");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await _api.GetAsync(ReleasesApi + "/latest", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return await GetLatestReleaseFallbackAsync(ct).ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);

            return ReadRelease(doc.RootElement);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return await GetLatestReleaseFallbackAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return await GetLatestReleaseFallbackAsync(ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return await GetLatestReleaseFallbackAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// GitHub API can be slow or blocked while raw.githubusercontent still works.
    /// The tag from .service/version.txt is enough to build a release descriptor.
    /// </summary>
    private async Task<ReleaseInfo?> GetLatestReleaseFallbackAsync(CancellationToken ct)
    {
        var tag = await GetBranchVersionAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(tag)) return null;

        return new ReleaseInfo
        {
            Tag = tag,
            Name = tag,
            Body = "GitHub API не ответил, поэтому список изменений недоступен. Обновление можно установить.",
            HtmlUrl = $"https://github.com/{AppInfo.UpstreamRepo}/releases/tag/{tag}",
            ZipUrl = $"https://github.com/{AppInfo.UpstreamRepo}/releases/download/{tag}/zapret-discord-youtube-{tag}.zip",
            PublishedAt = DateTimeOffset.Now
        };
    }

    /// <summary>All releases newer than the installed version, newest first.</summary>
    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesSinceAsync(string? installedVersion, int max = 8, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await _api.GetAsync(ReleasesApi + "?per_page=30", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Array.Empty<ReleaseInfo>();

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);

            var list = new List<ReleaseInfo>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                var release = ReadRelease(element);
                if (installedVersion is not null && CompareVersions(release.Tag, installedVersion) <= 0) continue;
                list.Add(release);
                if (list.Count >= max) break;
            }

            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return Array.Empty<ReleaseInfo>();
        }
    }

    private static ReleaseInfo ReadRelease(JsonElement element)
    {
        var tag = element.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var name = element.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
        var body = element.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var html = element.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        var date = element.TryGetProperty("published_at", out var d) && d.TryGetDateTimeOffset(out var dt) ? dt : DateTimeOffset.MinValue;

        var zip = "";
        if (element.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                if (!assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                zip = asset.TryGetProperty("browser_download_url", out var url) ? url.GetString() ?? "" : "";
                break;
            }
        }

        return new ReleaseInfo
        {
            Tag = tag,
            Name = name,
            Body = body,
            HtmlUrl = html,
            ZipUrl = zip,
            PublishedAt = date
        };
    }

    /// <summary>Latest releases, newest first (used for the built-in changelog).</summary>
    public async Task<IReadOnlyList<ReleaseInfo>> GetRecentReleasesAsync(int count = 3, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await _api.GetAsync(ReleasesApi + "?per_page=10", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Array.Empty<ReleaseInfo>();

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);

            var list = new List<ReleaseInfo>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                list.Add(ReadRelease(element));
                if (list.Count >= count) break;
            }
            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return Array.Empty<ReleaseInfo>();
        }
    }

    /// <summary>Fallback check used by the legacy launcher: the version.txt on the main branch.</summary>
    public async Task<string?> GetBranchVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var text = await _download.GetStringAsync(VersionUrl, timeout.Token).ConfigureAwait(false);
            return text.Trim();
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> DownloadReleaseAsync(ReleaseInfo release, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(release.ZipUrl))
            throw new InvalidOperationException("В релизе нет zip-архива, скачивание невозможно.");

        var tempDir = Path.Combine(Path.GetTempPath(), "zapret-control");
        Directory.CreateDirectory(tempDir);
        var target = Path.Combine(tempDir, $"zapret-{release.Tag}.zip");

        using var response = await _download.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            readTotal += read;
            progress?.Report(new DownloadProgress(readTotal, total));
        }

        return target;
    }

    /// <summary>Installs a downloaded archive over the current distribution.</summary>
    public async Task<UpdateResult> InstallAsync(
        string zipPath,
        ZapretPaths paths,
        UpdateOptions options,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var result = new UpdateResult();
        var oldVersion = GetInstalledVersion(paths);
        result.OldVersion = oldVersion;

        var workDir = Path.Combine(Path.GetTempPath(), "zapret-control", "extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            log?.Report("Распаковка архива...");
            ZipFile.ExtractToDirectory(zipPath, workDir, overwriteFiles: true);

            var newRoot = FindDistributionRoot(workDir);
            if (newRoot is null)
                throw new InvalidOperationException("Архив не похож на сборку zapret-discord-youtube: нет bin\\winws.exe.");
            if (!ValidateDistribution(newRoot, out var error))
                throw new InvalidOperationException(error ?? "Архив не похож на сборку zapret-discord-youtube.");

            result.NewVersion = GetInstalledVersion(new ZapretPaths(newRoot)) ?? Path.GetFileName(newRoot);

            log?.Report("Остановка текущей стратегии...");
            var service = new ServiceManager(paths);
            result.ServiceWasInstalled = await service.IsInstalledForAsync(paths, ct).ConfigureAwait(false);
            var installedStrategy = result.ServiceWasInstalled
                ? await service.GetInstalledStrategyAsync(ct).ConfigureAwait(false)
                : null;

            WindowsProcess.KillAllFrom(paths.WinwsExe);
            if (result.ServiceWasInstalled)
                await service.UninstallAsync(ct).ConfigureAwait(false);
            await Task.Delay(700, ct).ConfigureAwait(false);

            log?.Report("Резервная копия текущей установки...");
            if (Directory.Exists(paths.Root) && Directory.EnumerateFileSystemEntries(paths.Root).Any())
            {
                result.BackupPath = Backup(paths.Root);
            }
            else
            {
                Directory.CreateDirectory(paths.Root);
                log?.Report("Текущая установка отсутствует: выполняется чистая установка.");
            }

            log?.Report("Замена файлов...");
            var ipsetLoaded = IpSetManager.GetMode(paths) == IpSetMode.Loaded;
            var baseFiles = oldVersion is not null && options.ListMode == ListMergeMode.Merge
                ? await FetchBaseFilesAsync(oldVersion, ct).ConfigureAwait(false)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            CopyDistribution(newRoot, paths.Root, options, baseFiles, ipsetLoaded, log, ct);

            if (!File.Exists(paths.WinwsExe))
                throw new InvalidOperationException("После обновления отсутствует bin\\winws.exe.");

            result.Success = true;
            result.Message = "Обновление установлено.";
            result.RestartStrategy = installedStrategy;

            log?.Report(result.Message);
        }
        catch (Exception ex)
        {
            log?.Report("Ошибка: " + ex.Message);
            result.Success = false;
            result.Message = ex.Message;
            if (result.BackupPath is not null && Directory.Exists(result.BackupPath))
            {
                log?.Report("Откат из резервной копии...");
                try
                {
                    RestoreBackup(paths.Root, result.BackupPath);
                    result.Message += " Изменения отменены.";
                }
                catch (Exception rollbackError)
                {
                    result.Message += " Откат не удался: " + rollbackError.Message;
                }
            }
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }

        return result;
    }

    private static string? FindDistributionRoot(string dir)
    {
        if (File.Exists(Path.Combine(dir, "bin", "winws.exe"))) return dir;

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (File.Exists(Path.Combine(sub, "bin", "winws.exe"))) return sub;
        }
        return null;
    }

    public static bool ValidateDistribution(string root, out string? error)
    {
        error = null;
        if (!File.Exists(Path.Combine(root, "bin", "winws.exe"))) { error = "В архиве нет bin\\winws.exe."; return false; }
        if (!File.Exists(Path.Combine(root, "bin", "WinDivert64.sys"))) { error = "В архиве нет bin\\WinDivert64.sys."; return false; }
        if (!Directory.Exists(Path.Combine(root, "lists"))) { error = "В архиве нет папки lists."; return false; }
        if (!Directory.Exists(Path.Combine(root, "utils"))) { error = "В архиве нет папки utils."; return false; }
        if (!Directory.EnumerateFiles(root, "general*.bat").Any()) { error = "В архиве нет стратегий general*.bat."; return false; }
        return true;
    }

    public static string Backup(string root)
    {
        var parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)) ?? root;
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        var backupRoot = Path.Combine(parent, "zapret-backups");
        Directory.CreateDirectory(backupRoot);

        var target = Path.Combine(backupRoot, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}");
        CopyDirectory(root, target, skip: null);
        PruneBackups(backupRoot, keep: 3);
        return target;
    }

    private static void PruneBackups(string backupRoot, int keep)
    {
        try
        {
            var dirs = new DirectoryInfo(backupRoot).GetDirectories()
                .OrderByDescending(d => d.CreationTimeUtc)
                .Skip(keep)
                .ToList();
            foreach (var dir in dirs) dir.Delete(true);
        }
        catch
        {
            // best effort
        }
    }

    public static void RestoreBackup(string root, string backup)
    {
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("zapret-backups", StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(Path.Combine(backup, name))) Directory.Delete(dir, true);
        }

        CopyDirectory(backup, root, skip: null);
    }

    private static readonly string[] MergeableFiles =
    {
        "lists/list-general.txt",
        "lists/list-google.txt",
        "lists/list-exclude.txt",
        "lists/ipset-exclude.txt",
        "lists/ipset-all.txt.backup",
        "utils/targets.txt"
    };

    /// <summary>Downloads the same files from the previously installed tag: they are the merge base.</summary>
    private async Task<Dictionary<string, string>> FetchBaseFilesAsync(string tag, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in MergeableFiles)
        {
            try
            {
                var url = $"https://raw.githubusercontent.com/{AppInfo.UpstreamRepo}/{tag}/{path}";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await _download.GetAsync(url, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                result[path] = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // merge falls back to the union mode for this file
            }
        }
        return result;
    }

    private static void CopyDistribution(
        string source,
        string target,
        UpdateOptions options,
        IReadOnlyDictionary<string, string> baseFiles,
        bool ipsetLoaded,
        IProgress<string>? log,
        CancellationToken ct)
    {
        // bin
        CopyDirectory(Path.Combine(source, "bin"), Path.Combine(target, "bin"), skip: name =>
            options.PreserveActiveFakes && name.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase));

        // lists: merge instead of overwrite, user files are never touched
        var sourceLists = Path.Combine(source, "lists");
        var targetLists = Path.Combine(target, "lists");
        Directory.CreateDirectory(targetLists);

        foreach (var file in Directory.EnumerateFiles(sourceLists))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var destination = Path.Combine(targetLists, name);
            var exists = File.Exists(destination);

            if (name.Equals("ipset-all.txt", StringComparison.OrdinalIgnoreCase))
            {
                // the archive copy is only a "none" marker; the active mode is preserved
                if (!exists || (!options.PreserveIpSet && options.ListMode == ListMergeMode.Replace))
                    File.Copy(file, destination, overwrite: true);
                continue;
            }

            if (name.Equals("ipset-all.txt.backup", StringComparison.OrdinalIgnoreCase))
            {
                if (!exists)
                {
                    File.Copy(file, destination, overwrite: true);
                    continue;
                }

                switch (options.ListMode)
                {
                    case ListMergeMode.KeepLocal:
                        continue;
                    case ListMergeMode.Replace:
                        File.Copy(file, destination, overwrite: true);
                        continue;
                    default:
                        MergeFile(destination, file, baseFiles.GetValueOrDefault("lists/ipset-all.txt.backup"), name, log);
                        continue;
                }
            }

            if (!exists)
            {
                File.Copy(file, destination, overwrite: true);
                continue;
            }

            switch (options.ListMode)
            {
                case ListMergeMode.KeepLocal:
                    continue;
                case ListMergeMode.Replace:
                    File.Copy(file, destination, overwrite: true);
                    continue;
                default:
                    MergeFile(destination, file, baseFiles.GetValueOrDefault("lists/" + name), name, log);
                    continue;
            }
        }

        // a loaded ipset keeps working from a merged full list, manual additions included
        var sourceBackup = Path.Combine(sourceLists, "ipset-all.txt.backup");
        var activeIpSet = Path.Combine(targetLists, "ipset-all.txt");
        if (options.ListMode == ListMergeMode.Merge && ipsetLoaded &&
            File.Exists(sourceBackup) && File.Exists(activeIpSet))
        {
            MergeFile(activeIpSet, sourceBackup, baseFiles.GetValueOrDefault("lists/ipset-all.txt.backup"), "ipset-all.txt", log);
        }

        // utils: runtime flags stay as they are, targets.txt is merged
        var sourceUtils = Path.Combine(source, "utils");
        var targetUtils = Path.Combine(target, "utils");
        Directory.CreateDirectory(targetUtils);
        var hadUpdateFlag = File.Exists(Path.Combine(targetUtils, "check_updates.enabled"));

        foreach (var file in Directory.EnumerateFiles(sourceUtils))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var destination = Path.Combine(targetUtils, name);

            if (options.PreserveUserFiles &&
                (name.Equals("game_filter.enabled", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("check_updates.enabled", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (name.Equals("targets.txt", StringComparison.OrdinalIgnoreCase) && File.Exists(destination) &&
                options.ListMode != ListMergeMode.Replace)
            {
                if (options.ListMode == ListMergeMode.KeepLocal) continue;
                MergeFile(destination, file, baseFiles.GetValueOrDefault("utils/targets.txt"), name, log);
                continue;
            }

            File.Copy(file, destination, overwrite: true);
        }

        // the user disabled the update check by deleting the flag: keep it disabled
        if (options.PreserveUserFiles && !hadUpdateFlag)
        {
            var flag = Path.Combine(targetUtils, "check_updates.enabled");
            if (File.Exists(flag)) File.Delete(flag);
        }

        // strategies: mirror the release, but keep bat files that upstream no longer ships only if they are not general*
        var newBats = Directory.EnumerateFiles(source, "*.bat").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var old in Directory.EnumerateFiles(target, "general*.bat"))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(old);
            if (!newBats.Contains(name))
            {
                log?.Report("Удалена устаревшая стратегия: " + name);
                File.Delete(old);
            }
        }

        foreach (var bat in Directory.EnumerateFiles(source, "*.bat"))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(bat, Path.Combine(target, Path.GetFileName(bat)), overwrite: true);
        }
    }

    private static void MergeFile(string destination, string releaseFile, string? baseText, string displayName, IProgress<string>? log)
    {
        var ours = File.Exists(destination) ? File.ReadAllText(destination) : null;
        var theirs = File.ReadAllText(releaseFile);
        var merged = ListMerger.Merge(ours, theirs, baseText, out var stats);
        File.WriteAllText(destination, merged, new UTF8Encoding(false));

        if (stats.Added + stats.Removed + stats.Kept > 0)
            log?.Report($"{displayName}: {stats.Describe()}");
    }

    private static void CopyDirectory(string source, string target, Func<string, bool>? skip)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (skip is not null && skip(name)) continue;
            File.Copy(file, Path.Combine(target, name), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), skip);
        }
    }

    /// <summary>Updates lists/ipset-all.txt from the repository (same source as service.bat option 8).</summary>
    public async Task<bool> UpdateIpSetAsync(ZapretPaths paths, CancellationToken ct = default)
    {
        var text = await _download.GetStringAsync(IpSetUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('/')) return false;

        Directory.CreateDirectory(paths.ListsDir);
        if (File.Exists(paths.IpSetAll) && new FileInfo(paths.IpSetAll).Length > 100)
        {
            File.Copy(paths.IpSetAll, paths.IpSetBackup, overwrite: true);
        }

        await File.WriteAllTextAsync(paths.IpSetAll, text.Replace("\n", "\r\n"), new UTF8Encoding(false), ct).ConfigureAwait(false);
        return true;
    }

    public async Task<string?> DownloadHostsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _download.GetStringAsync(HostsUrl, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Merges the repository hosts entries into the system hosts file using markers,
    /// so the block can be replaced or removed later without Notepad.
    /// </summary>
    public static HostsApplyResult ApplyHosts(string content)
    {
        const string begin = "# >>> zapret-control >>>";
        const string end = "# <<< zapret-control <<<";
        var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

        try
        {
            var existing = File.Exists(hostsPath) ? File.ReadAllText(hostsPath) : "";
            var without = RemoveBlock(existing, begin, end);
            var block = new StringBuilder();
            block.AppendLine(begin);
            foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                block.AppendLine(trimmed);
            }
            block.AppendLine(end);

            var updated = without.TrimEnd() + Environment.NewLine + Environment.NewLine + block;
            File.WriteAllText(hostsPath, updated, new UTF8Encoding(false));

            var count = block.ToString().Split('\n').Count(l => l.Contains('.') && !l.TrimStart().StartsWith('#'));
            return new HostsApplyResult(true, count, hostsPath);
        }
        catch (Exception ex)
        {
            return new HostsApplyResult(false, 0, ex.Message);
        }
    }

    public static bool RemoveHostsBlock(out string message)
    {
        const string begin = "# >>> zapret-control >>>";
        const string end = "# <<< zapret-control <<<";
        var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        try
        {
            if (!File.Exists(hostsPath)) { message = "Файл hosts не найден."; return false; }
            var text = File.ReadAllText(hostsPath);
            File.WriteAllText(hostsPath, RemoveBlock(text, begin, end), new UTF8Encoding(false));
            message = "Записи zapret-control удалены из hosts.";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    public static bool HostsHasBlock()
    {
        var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        try
        {
            return File.Exists(hostsPath) && File.ReadAllText(hostsPath).Contains("# >>> zapret-control >>>", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string RemoveBlock(string text, string begin, string end)
    {
        var start = text.IndexOf(begin, StringComparison.Ordinal);
        while (start >= 0)
        {
            var stop = text.IndexOf(end, start, StringComparison.Ordinal);
            if (stop < 0) break;
            stop += end.Length;
            while (stop < text.Length && (text[stop] == '\r' || text[stop] == '\n')) stop++;
            text = text.Remove(start, stop - start);
            start = text.IndexOf(begin, StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>Compares tags like 1.9.9d and 1.10.4. Returns 1 when a &gt; b.</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = ParseVersion(a);
        var pb = ParseVersion(b);

        for (var i = 0; i < 3; i++)
        {
            var cmp = pa.Numbers[i].CompareTo(pb.Numbers[i]);
            if (cmp != 0) return cmp;
        }

        return string.CompareOrdinal(pa.Suffix, pb.Suffix);
    }

    private static (int[] Numbers, string Suffix) ParseVersion(string value)
    {
        var numbers = new int[3];
        var match = Regex.Match(value ?? "", @"^[vV]?(\d+)(?:\.(\d+))?(?:\.(\d+))?([a-zA-Z]*)");
        if (!match.Success) return (numbers, "");

        for (var i = 0; i < 3; i++)
        {
            var group = match.Groups[i + 1];
            numbers[i] = group.Success && int.TryParse(group.Value, out var n) ? n : 0;
        }
        return (numbers, match.Groups[4].Value.ToLowerInvariant());
    }
}

public readonly record struct DownloadProgress(long Received, long? Total)
{
    public double? Percent => Total is > 0 ? Received * 100.0 / Total.Value : null;
}

public readonly record struct HostsApplyResult(bool Success, int Entries, string Message);
