using System.Text;

namespace Zapret.Core;

/// <summary>
/// "Do not bypass YouTube": removes YouTube from the bypass lists while keeping
/// Discord under the bypass. Useful when video stalls with the bypass but works
/// without it, because the strategy's Google profile (hostfakesplit) hurts streaming.
///
/// list-exclude-user.txt is used by all profiles that support exclusions.
/// list-google.txt has no exclusions, so the YouTube entries are removed from it
/// directly, with a backup file that the revert button restores.
/// </summary>
public static class YouTubeExclusion
{
    public const string MarkerBegin = "# >>> zapret-control: youtube direct >>>";
    public const string MarkerEnd = "# <<< zapret-control: youtube direct <<<";
    public const string BackupSuffix = ".no-youtube.bak";

    public static readonly string[] Domains =
    {
        "youtube.com",
        "youtu.be",
        "ytimg.com",
        "ytimg.l.google.com",
        "yt3.ggpht.com",
        "yt4.ggpht.com",
        "yt3.googleusercontent.com",
        "googlevideo.com",
        "jnn-pa.googleapis.com",
        "wide-youtube.l.google.com",
        "youtube-ui.l.google.com",
        "youtube-nocookie.com",
        "youtubekids.com",
        "youtube.googleapis.com",
        "youtubei.googleapis.com",
        "youtubeembeddedplayer.googleapis.com",
        "yt-video-upload.l.google.com"
    };

    public static bool IsApplied(ZapretPaths paths)
    {
        try
        {
            return File.Exists(paths.ListExcludeUser) &&
                   File.ReadAllText(paths.ListExcludeUser).Contains(MarkerBegin, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static string BackupPath(ZapretPaths paths) => paths.ListGoogle + BackupSuffix;

    public sealed class Result
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public int RemovedFromGoogle { get; init; }
        public int AddedExclusions { get; init; }
        public string? BackupPath { get; init; }
    }

    public static Result Apply(ZapretPaths paths)
    {
        try
        {
            Directory.CreateDirectory(paths.ListsDir);

            var exclude = File.Exists(paths.ListExcludeUser) ? File.ReadAllText(paths.ListExcludeUser) : "";
            var existing = Split(exclude);
            var added = 0;

            var builder = new StringBuilder(exclude.Replace("\r\n", "\n").TrimEnd());
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(MarkerBegin).Append('\n');
            foreach (var domain in Domains)
            {
                if (existing.Contains(domain, StringComparer.OrdinalIgnoreCase)) continue;
                builder.Append(domain).Append('\n');
                added++;
            }
            builder.Append(MarkerEnd).Append('\n');
            Write(paths.ListExcludeUser, builder.ToString());

            var removed = 0;
            string? backup = null;
            if (File.Exists(paths.ListGoogle))
            {
                var google = File.ReadAllText(paths.ListGoogle);
                backup = BackupPath(paths);
                if (!File.Exists(backup)) File.Copy(paths.ListGoogle, backup, overwrite: true);

                var kept = new List<string>();
                foreach (var line in google.Replace("\r\n", "\n").Split('\n'))
                {
                    if (IsYouTubeDomain(line))
                    {
                        removed++;
                        continue;
                    }
                    kept.Add(line.TrimEnd());
                }

                Write(paths.ListGoogle, string.Join('\n', kept).TrimEnd() + "\n");
            }

            return new Result
            {
                Success = true,
                RemovedFromGoogle = removed,
                AddedExclusions = added,
                BackupPath = backup,
                Message = $"YouTube исключён из обхода: добавлено исключений {added}, убрано из list-google {removed}. Перезапустите обход."
            };
        }
        catch (Exception ex)
        {
            return new Result { Success = false, Message = "Не удалось изменить списки: " + ex.Message };
        }
    }

    public static Result Revert(ZapretPaths paths)
    {
        try
        {
            string? restored = null;
            var backup = BackupPath(paths);
            if (File.Exists(backup))
            {
                File.Copy(backup, paths.ListGoogle, overwrite: true);
                File.Delete(backup);
                restored = paths.ListGoogle;
            }

            if (File.Exists(paths.ListExcludeUser))
            {
                var text = File.ReadAllText(paths.ListExcludeUser);
                var start = text.IndexOf(MarkerBegin, StringComparison.Ordinal);
                if (start >= 0)
                {
                    var end = text.IndexOf(MarkerEnd, start, StringComparison.Ordinal);
                    if (end >= 0)
                    {
                        end += MarkerEnd.Length;
                        while (end < text.Length && (text[end] == '\r' || text[end] == '\n')) end++;
                        text = text.Remove(start, end - start);
                    }
                }
                Write(paths.ListExcludeUser, text.TrimEnd() + "\n");
            }

            return new Result
            {
                Success = true,
                Message = restored is null
                    ? "Исключения YouTube удалены. Резервной копии list-google нет, файл не менялся."
                    : "Обход YouTube возвращён: list-google восстановлен, исключения удалены. Перезапустите обход."
            };
        }
        catch (Exception ex)
        {
            return new Result { Success = false, Message = "Не удалось вернуть списки: " + ex.Message };
        }
    }

    private static bool IsYouTubeDomain(string line)
    {
        var domain = line.Trim().ToLowerInvariant();
        if (domain.Length == 0 || domain.StartsWith('#')) return false;

        return Domains.Any(d => domain.Equals(d, StringComparison.OrdinalIgnoreCase)) ||
               domain.Contains("youtube") ||
               domain.Contains("youtu.be") ||
               domain.Contains("ytimg") ||
               domain.Contains("googlevideo") ||
               domain.StartsWith("yt3.") ||
               domain.StartsWith("yt4.") ||
               domain.Contains("jnn-pa.googleapis");
    }

    private static List<string> Split(string text) =>
        text.Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

    private static void Write(string path, string content) =>
        File.WriteAllText(path, content.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
}
