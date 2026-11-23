using System.IO.Compression;
using System.Text;

namespace Zapret.Core;

/// <summary>One strategy file read from a .bat file or a .zip archive.</summary>
public sealed record StrategyFile(string Name, byte[] Bytes);

/// <summary>
/// Moves strategy .bat files between installations: export selected strategies to a
/// folder or a zip, read them back from files, folders and archives. Only real
/// strategy files are accepted, random .bat files are ignored.
/// </summary>
public static class StrategyTransfer
{
    public const string ZipExtension = ".zip";
    public const string BatExtension = ".bat";

    /// <summary>Creates strategies-YYYYMMDD-HHmmss under the chosen folder and copies the files there.</summary>
    public static string ExportToFolder(IEnumerable<Strategy> strategies, string parentFolder, DateTime now)
    {
        var folder = Path.Combine(parentFolder, "strategies-" + now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);
        foreach (var strategy in strategies)
        {
            File.Copy(strategy.FilePath, Path.Combine(folder, strategy.FileName), true);
        }
        return folder;
    }

    /// <summary>Writes the selected strategies into one zip, flat, with their original names.</summary>
    public static void ExportToZip(IEnumerable<Strategy> strategies, string zipPath)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var strategy in strategies)
        {
            zip.CreateEntryFromFile(strategy.FilePath, strategy.FileName, CompressionLevel.Optimal);
        }
    }

    /// <summary>
    /// Reads .bat files and .zip archives. Entries with another extension and files
    /// without a winws command line are counted as invalid, duplicates by file name
    /// are read once.
    /// </summary>
    public static IReadOnlyList<StrategyFile> Read(IEnumerable<string> sources, out int invalid)
    {
        var result = new List<StrategyFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidCount = 0;

        void Add(string name, byte[] bytes)
        {
            name = Path.GetFileName(name);
            if (string.IsNullOrWhiteSpace(name) ||
                !name.EndsWith(BatExtension, StringComparison.OrdinalIgnoreCase) ||
                !LooksLikeStrategy(bytes))
            {
                invalidCount++;
                return;
            }

            if (!seen.Add(name)) return;
            result.Add(new StrategyFile(name, bytes));
        }

        foreach (var source in sources)
        {
            try
            {
                if (source.EndsWith(ZipExtension, StringComparison.OrdinalIgnoreCase))
                {
                    using var zip = ZipFile.OpenRead(source);
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                        if (!entry.Name.EndsWith(BatExtension, StringComparison.OrdinalIgnoreCase)) continue;

                        using var stream = entry.Open();
                        using var buffer = new MemoryStream();
                        stream.CopyTo(buffer);
                        Add(entry.Name, buffer.ToArray());
                    }
                }
                else
                {
                    Add(source, File.ReadAllBytes(source));
                }
            }
            catch
            {
                invalidCount++;
            }
        }

        invalid = invalidCount;
        return result;
    }

    /// <summary>A strategy .bat always starts winws; anything else is not a strategy.</summary>
    public static bool LooksLikeStrategy(byte[] bytes)
    {
        var length = Math.Min(bytes.Length, 256 * 1024);
        var text = Encoding.ASCII.GetString(bytes, 0, length);
        return text.Contains("winws.exe", StringComparison.OrdinalIgnoreCase);
    }
}
