using System.Text;

namespace Zapret.Core;

/// <summary>
/// Turns off the domain part of the Discord bypass: Discord sites and the TCP
/// profiles stop touching Discord. The voice UDP profile matches ports and L7,
/// not domains, so it keeps working; the card says so explicitly.
/// </summary>
public static class DiscordExclusion
{
    public const string MarkerBegin = "# >>> zapret-control: discord direct >>>";
    public const string MarkerEnd = "# <<< zapret-control: discord direct <<<";
    public const string BackupSuffix = ".no-discord.bak";

    public static readonly string[] Domains =
    {
        "dis.gd",
        "discord-attachments-uploads-prd.storage.googleapis.com",
        "discord.app",
        "discord.co",
        "discord.com",
        "discord.design",
        "discord.dev",
        "discord.gift",
        "discord.gifts",
        "discord.gg",
        "discord.media",
        "discord.new",
        "discord.store",
        "discord.status",
        "discord-activities.com",
        "discordactivities.com",
        "discordapp.com",
        "discordapp.net",
        "discordcdn.com",
        "discordmerch.com",
        "discordpartygames.com",
        "discordsays.com",
        "discordsez.com",
        "discordstatus.com",
        "stable.dl2.discordapp.net"
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

    public static string BackupPath(string listPath) => listPath + BackupSuffix;

    public sealed class Result
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public int Removed { get; init; }
        public int Added { get; init; }
    }

    public static Result Apply(ZapretPaths paths)
    {
        try
        {
            Directory.CreateDirectory(paths.ListsDir);

            var exclude = File.Exists(paths.ListExcludeUser) ? File.ReadAllText(paths.ListExcludeUser) : "";
            var existing = Split(exclude)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var builder = new StringBuilder(exclude.Replace("\r\n", "\n").TrimEnd());
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(MarkerBegin).Append('\n');
            var added = 0;
            foreach (var domain in Domains)
            {
                if (existing.Contains(domain)) continue;
                builder.Append(domain).Append('\n');
                added++;
            }
            builder.Append(MarkerEnd).Append('\n');
            Write(paths.ListExcludeUser, builder.ToString());

            var removed = 0;
            foreach (var path in new[] { paths.ListGeneral, paths.ListGoogle })
            {
                if (!File.Exists(path)) continue;

                var text = File.ReadAllText(path);
                var lines = Split(text);
                var kept = new List<string>();
                var changed = false;

                foreach (var line in lines)
                {
                    if (IsDiscordLine(line))
                    {
                        removed++;
                        changed = true;
                        continue;
                    }
                    kept.Add(line.TrimEnd());
                }

                if (!changed) continue;

                var backup = BackupPath(path);
                if (!File.Exists(backup)) File.Copy(path, backup, overwrite: true);
                Write(path, string.Join('\n', kept));
            }

            return new Result
            {
                Success = true,
                Removed = removed,
                Added = added,
                Message = $"Обход Discord (текст и сайт) выключен: добавлено исключений {added}, убрано из списков {removed}. Голосовой UDP-профиль остаётся."
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
            var restored = 0;
            foreach (var path in new[] { paths.ListGeneral, paths.ListGoogle })
            {
                var backup = BackupPath(path);
                if (!File.Exists(backup)) continue;
                File.Copy(backup, path, overwrite: true);
                File.Delete(backup);
                restored++;
            }

            if (File.Exists(paths.ListExcludeUser))
            {
                var text = File.ReadAllText(paths.ListExcludeUser);
                var start = text.IndexOf(MarkerBegin, StringComparison.Ordinal);
                while (start >= 0)
                {
                    var end = text.IndexOf(MarkerEnd, start, StringComparison.Ordinal);
                    if (end < 0) break;
                    end += MarkerEnd.Length;
                    while (end < text.Length && (text[end] == '\r' || text[end] == '\n')) end++;
                    text = text.Remove(start, end - start);
                    start = text.IndexOf(MarkerBegin, StringComparison.Ordinal);
                }
                Write(paths.ListExcludeUser, text.TrimEnd() + "\n");
            }

            return new Result
            {
                Success = true,
                Message = restored > 0
                    ? "Обход Discord возвращён: списки восстановлены, исключения удалены."
                    : "Исключения Discord удалены. Резервных копий списков нет."
            };
        }
        catch (Exception ex)
        {
            return new Result { Success = false, Message = "Не удалось вернуть списки: " + ex.Message };
        }
    }

    private static bool IsDiscordLine(string line)
    {
        var value = line.Trim().ToLowerInvariant();
        if (value.Length == 0 || value.StartsWith('#')) return false;
        return value.Equals("dis.gd", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("discord", StringComparison.Ordinal);
    }

    private static List<string> Split(string text) =>
        text.Replace("\r\n", "\n").Split('\n').ToList();

    private static void Write(string path, string content) =>
        File.WriteAllText(path, content.Replace("\r\n", "\n").Replace("\n", "\r\n").TrimEnd() + "\r\n", new UTF8Encoding(false));
}
