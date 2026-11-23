using System.Text;
using System.Text.RegularExpressions;

namespace Zapret.Core;

/// <summary>
/// Converts the upstream .bat launchers into a structured winws command line.
/// The .bat files stay the source of truth, so an upstream update can be picked up
/// without editing the GUI: only the strategy list is re-read.
/// </summary>
public static class BatStrategyParser
{
    public static Strategy Parse(string batPath, ZapretPaths paths)
    {
        var fileName = Path.GetFileName(batPath);
        var name = DisplayName(fileName);
        var raw = BuildCommandText(batPath);
        var tokens = Tokenize(raw);

        var strategy = new Strategy
        {
            FilePath = batPath,
            FileName = fileName,
            Name = name,
            Note = StrategyCatalog.Get(name)
        };

        WinwsProfile? current = null;
        foreach (var token in tokens)
        {
            if (token is "--new" or "--NEW")
            {
                current = new WinwsProfile();
                strategy.Segments.Add(current);
                continue;
            }

            var arg = ParseArg(token);
            if (arg is null) continue;

            if (current is null)
            {
                // Segment 0: global options plus the first filter block.
                current = new WinwsProfile();
                strategy.Segments.Add(current);
            }

            current.Args.Add(arg);
        }

        return strategy;
    }

    public static IReadOnlyList<Strategy> ParseAll(ZapretPaths paths)
    {
        var result = new List<Strategy>();
        foreach (var file in paths.StrategyFiles())
        {
            try
            {
                var s = Parse(file, paths);
                if (s.Profiles.Count > 0) result.Add(s);
            }
            catch
            {
                // A malformed file must not break the whole list.
            }
        }
        return result;
    }

    /// <summary>Turns "general (ALT11).bat" into "ALT11" and "general.bat" into "general".</summary>
    public static string DisplayName(string fileName)
    {
        var bare = Path.GetFileNameWithoutExtension(fileName);
        var m = Regex.Match(bare, @"\(([^)]+)\)");
        if (m.Success) return m.Groups[1].Value.Trim();
        return bare.StartsWith("general", StringComparison.OrdinalIgnoreCase) ? "general" : bare;
    }

    /// <summary>Joins the continued cmd.exe line that contains winws.exe and cuts everything before it.</summary>
    public static string BuildCommandText(string batPath)
    {
        var lines = File.ReadAllLines(batPath);
        var start = Array.FindIndex(lines, l => l.Contains("winws.exe", StringComparison.OrdinalIgnoreCase));
        if (start < 0) return "";

        var sb = new StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var continued = line.EndsWith("^", StringComparison.Ordinal);
            if (continued) line = line[..^1];
            sb.Append(line).Append(' ');
            if (!continued) break;
        }

        var text = sb.ToString();
        var idx = text.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        // the exe path is usually quoted: cut the rest and drop the closing quote
        var rest = text[(idx + "winws.exe".Length)..].TrimStart(' ', '\t', '"');
        return rest;
    }

    /// <summary>
    /// Windows command line splitting (quotes group, backslashes are literal,
    /// caret escapes the next character like cmd does).
    /// </summary>
    public static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (c == '^' && !inQuotes)
            {
                // ^! in a .bat means a literal !, ^& means &, and so on.
                if (i + 1 < commandLine.Length)
                {
                    i++;
                    sb.Append(commandLine[i]);
                    hasToken = true;
                }
                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                    hasToken = false;
                }
                continue;
            }

            sb.Append(c);
            hasToken = true;
        }

        if (hasToken) tokens.Add(sb.ToString());
        return tokens;
    }

    private static WinwsArg? ParseArg(string token)
    {
        if (!token.StartsWith("--", StringComparison.Ordinal)) return null;

        var eq = token.IndexOf('=');
        if (eq < 0)
        {
            return new WinwsArg(token.ToLowerInvariant());
        }

        var name = token[..eq].ToLowerInvariant();
        var value = token[(eq + 1)..];
        return new WinwsArg(name, value);
    }
}
