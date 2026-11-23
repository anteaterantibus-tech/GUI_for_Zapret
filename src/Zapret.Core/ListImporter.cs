using System.Text;

namespace Zapret.Core;

public sealed class ListImportResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string? BackupPath { get; init; }
    public int FilesChanged { get; init; }
    public int EntriesAdded { get; init; }
    public List<string> Skipped { get; init; } = new();
}

/// <summary>
/// Imports list files chosen by the user (or a whole folder) and merges them into
/// the current lists. Nothing is overwritten: entries are unioned, duplicates removed,
/// and the whole lists folder is backed up first.
/// </summary>
public static class ListImporter
{
    private static readonly string[] KnownNames =
    {
        "list-general.txt",
        "list-general-user.txt",
        "list-google.txt",
        "list-exclude.txt",
        "list-exclude-user.txt",
        "ipset-exclude.txt",
        "ipset-exclude-user.txt",
        "ipset-all.txt"
    };

    public static IReadOnlyList<string> Known => KnownNames;

    public static ListImportResult Import(ZapretPaths paths, IEnumerable<string> sourceFiles)
    {
        var files = sourceFiles.Where(File.Exists).ToList();
        if (files.Count == 0)
            return new ListImportResult { Success = false, Message = "Файлы не выбраны." };

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["list-general.txt"] = paths.ListGeneral,
            ["list-general-user.txt"] = paths.ListGeneralUser,
            ["list-google.txt"] = paths.ListGoogle,
            ["list-exclude.txt"] = paths.ListExclude,
            ["list-exclude-user.txt"] = paths.ListExcludeUser,
            ["ipset-exclude.txt"] = paths.IpSetExclude,
            ["ipset-exclude-user.txt"] = paths.IpSetExcludeUser,
            ["ipset-all.txt"] = paths.IpSetAll
        };

        var pairs = new List<(string Source, string Target)>();
        var skipped = new List<string>();

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (map.TryGetValue(name, out var target)) pairs.Add((file, target));
            else skipped.Add(name);
        }

        if (pairs.Count == 0)
        {
            return new ListImportResult
            {
                Success = false,
                Message = "Ни один файл не совпал по имени со списками сборки.",
                Skipped = skipped
            };
        }

        Directory.CreateDirectory(paths.ListsDir);
        var backup = paths.ListsDir.TrimEnd(Path.DirectorySeparatorChar) + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(backup);
        foreach (var file in Directory.EnumerateFiles(paths.ListsDir))
        {
            File.Copy(file, Path.Combine(backup, Path.GetFileName(file)), overwrite: true);
        }

        var changed = 0;
        var added = 0;

        foreach (var (source, target) in pairs)
        {
            if (target.Equals(paths.IpSetAll, StringComparison.OrdinalIgnoreCase))
            {
                var sourceEntries = ReadEntries(File.ReadAllText(source));
                if (sourceEntries.Count <= 100) continue;

                var currentEntries = ReadEntries(File.Exists(target) ? File.ReadAllText(target) : "");
                var union = Union(currentEntries, sourceEntries, out var newCount);
                Write(target, string.Join('\n', union));
                Write(paths.IpSetBackup, string.Join('\n', union));
                added += newCount;
                changed++;
                continue;
            }

            var current = File.Exists(target) ? File.ReadAllText(target) : "";
            var merged = MergeLines(current, File.ReadAllText(source), out var count);
            Write(target, merged);
            added += count;
            changed++;
        }

        var skippedText = skipped.Count > 0
            ? " Пропущены неизвестные файлы: " + string.Join(", ", skipped) + "."
            : "";

        return new ListImportResult
        {
            Success = true,
            FilesChanged = changed,
            EntriesAdded = added,
            BackupPath = backup,
            Skipped = skipped,
            Message = $"Загружено файлов: {changed}, добавлено строк: {added}.{skippedText} Резервная копия: {backup}"
        };
    }

    private static string MergeLines(string currentText, string sourceText, out int added)
    {
        var current = Split(currentText);
        var source = Split(sourceText);

        var comments = new HashSet<string>(StringComparer.Ordinal);
        var items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var line in current)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith('#'))
            {
                if (comments.Add(trimmed)) result.Add(trimmed);
                continue;
            }
            if (items.Add(trimmed)) result.Add(trimmed);
        }

        added = 0;
        foreach (var line in source)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith('#'))
            {
                if (comments.Add(trimmed)) result.Add(trimmed);
                continue;
            }
            if (items.Add(trimmed))
            {
                result.Add(trimmed);
                added++;
            }
        }

        return string.Join('\n', result);
    }

    private static List<string> Union(IReadOnlyList<string> current, IReadOnlyList<string> source, out int added)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var line in current)
        {
            if (line.Length == 0) continue;
            if (seen.Add(line)) result.Add(line);
        }

        added = 0;
        foreach (var line in source)
        {
            if (line.Length == 0) continue;
            if (seen.Add(line))
            {
                result.Add(line);
                added++;
            }
        }

        return result;
    }

    private static List<string> ReadEntries(string text) =>
        Split(text).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    private static List<string> Split(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();

    private static void Write(string path, string content) =>
        File.WriteAllText(path, content.Replace("\r\n", "\n").Replace("\n", "\r\n").TrimEnd() + "\r\n", new UTF8Encoding(false));
}
