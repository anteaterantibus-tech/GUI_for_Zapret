using System.Text;

namespace Zapret.Core;

public enum ListMergeMode
{
    /// <summary>Keep local edits and add/remove only what changed upstream.</summary>
    Merge,

    /// <summary>Overwrite with the files from the release.</summary>
    Replace,

    /// <summary>Never touch existing list files.</summary>
    KeepLocal
}

public sealed class ListMergeStats
{
    public int Added { get; init; }
    public int Removed { get; init; }
    public int Kept { get; init; }
    public int Total { get; init; }

    public bool HasChanges => Added > 0 || Removed > 0;

    public string Describe() =>
        $"+{Added} новых, -{Removed} устаревших, сохранено своих: {Kept}, всего строк: {Total}";
}

/// <summary>
/// Line-based three-way merge for the upstream list files.
///
/// The user edits lists/*.txt by hand, and upstream changes them on every release.
/// A plain overwrite loses the edits, a plain union keeps entries upstream dropped.
/// Here the previous release file is used as the merge base:
///   * lines the user never touched follow the new release (additions and removals);
///   * lines the user added or edited stay;
///   * identical content is not duplicated.
/// If the base is unavailable (offline or fresh install), a safe union is used:
/// keep everything local and append only genuinely new lines.
/// </summary>
public static class ListMerger
{
    public static string Merge(string? ours, string? theirs, string? baseText, out ListMergeStats stats)
    {
        var ourLines = Split(ours);
        var theirLines = Split(theirs);
        var baseLines = baseText is null ? null : Split(baseText);

        // No local file or nothing to merge: take the release file as is.
        if (ourLines.Count == 0)
        {
            stats = new ListMergeStats { Added = theirLines.Count, Total = theirLines.Count };
            return Join(theirLines);
        }

        // Nothing new from upstream: keep local content untouched.
        if (theirLines.Count == 0)
        {
            stats = new ListMergeStats { Kept = ourLines.Count, Total = ourLines.Count };
            return Join(ourLines);
        }

        var comparer = StringComparer.OrdinalIgnoreCase;
        var ourSet = new HashSet<string>(ourLines, comparer);
        var theirSet = new HashSet<string>(theirLines, comparer);

        if (baseLines is null || baseLines.Count == 0)
        {
            // Union mode: local file stays authoritative, new release lines are appended.
            var result = new List<string>(ourLines);
            var seen = new HashSet<string>(ourLines, comparer);
            var added = 0;
            foreach (var line in theirLines)
            {
                if (seen.Add(line)) { result.Add(line); added++; }
            }

            stats = new ListMergeStats { Added = added, Kept = ourLines.Count, Total = result.Count };
            return Join(result);
        }

        var baseSet = new HashSet<string>(baseLines, comparer);
        var deletedUpstream = new HashSet<string>(baseSet, comparer);
        deletedUpstream.ExceptWith(theirSet);

        var merged = new List<string>(theirLines.Count + ourLines.Count);
        var mergedSet = new HashSet<string>(comparer);

        // New release order first: additions and untouched lines.
        foreach (var line in theirLines)
        {
            if (mergedSet.Add(line)) merged.Add(line);
        }

        var kept = 0;
        var removedLocal = 0;
        foreach (var line in ourLines)
        {
            if (theirSet.Contains(line)) continue;          // already present in the release
            if (deletedUpstream.Contains(line)) { removedLocal++; continue; } // upstream dropped it, user did not edit
            if (mergedSet.Add(line)) { merged.Add(line); kept++; }
        }

        var addedCount = theirSet.Count(line => !ourSet.Contains(line));

        stats = new ListMergeStats
        {
            Added = addedCount,
            Removed = removedLocal,
            Kept = kept,
            Total = merged.Count
        };

        return Join(merged);
    }

    private static List<string> Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();

        return text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static string Join(IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines) builder.Append(line).Append("\r\n");
        return builder.ToString();
    }
}
