using System.Text;

namespace Zapret.Core;

/// <summary>
/// Builds the winws command line. Values keep their original text, only the
/// cmd variables (%BIN%, %LISTS%, %~dp0, %GameFilterTCP%, %GameFilterUDP%) are expanded,
/// so a strategy runs exactly as the matching .bat would run it.
/// </summary>
public static class WinwsCommandBuilder
{
    public static IReadOnlyList<string> BuildArgs(Strategy strategy, GameFilter gameFilter, ZapretPaths paths)
    {
        var args = new List<string>();
        foreach (var arg in EnumerateArgs(strategy))
        {
            args.Add(ExpandArg(arg, gameFilter, paths));
        }
        return args;
    }

    public static string BuildCommandLine(Strategy strategy, GameFilter gameFilter, ZapretPaths paths) =>
        Join(BuildArgs(strategy, gameFilter, paths));

    private static IEnumerable<WinwsArg> EnumerateArgs(Strategy strategy)
    {
        for (var i = 0; i < strategy.Segments.Count; i++)
        {
            if (i > 0) yield return new WinwsArg("--new");
            foreach (var arg in strategy.Segments[i].Args) yield return arg;
        }
    }

    private static string ExpandArg(WinwsArg arg, GameFilter gameFilter, ZapretPaths paths)
    {
        var name = arg.Name;
        var value = arg.Value;
        if (value is null) return name;

        value = value
            .Replace("%BIN%", EnsureTrailingSeparator(paths.BinDir), StringComparison.OrdinalIgnoreCase)
            .Replace("%LISTS%", EnsureTrailingSeparator(paths.ListsDir), StringComparison.OrdinalIgnoreCase)
            .Replace("%~dp0", EnsureTrailingSeparator(paths.Root), StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterTCP%", gameFilter.EffectiveTcpPorts, StringComparison.OrdinalIgnoreCase)
            .Replace("%GameFilterUDP%", gameFilter.EffectiveUdpPorts, StringComparison.OrdinalIgnoreCase);

        return $"{name}={value}";
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    /// <summary>Quotes arguments for CreateProcess / sc.exe binPath.</summary>
    public static string Join(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Quote(arg));
        }
        return sb.ToString();
    }

    public static string Quote(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        if (!arg.Any(char.IsWhiteSpace) && !arg.Contains('"')) return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }
}
