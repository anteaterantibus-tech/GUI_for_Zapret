namespace Zapret.Core;

/// <summary>
/// Layout of an unpacked zapret-discord-youtube distribution.
/// The GUI never copies its own binaries: it drives the upstream folder as-is.
/// </summary>
public sealed class ZapretPaths
{
    public ZapretPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string BinDir => Path.Combine(Root, "bin");
    public string ListsDir => Path.Combine(Root, "lists");
    public string UtilsDir => Path.Combine(Root, "utils");
    public string WinwsExe => Path.Combine(BinDir, "winws.exe");
    public string WinDivertDll => Path.Combine(BinDir, "WinDivert.dll");
    public string WinDivertSys => Path.Combine(BinDir, "WinDivert64.sys");
    public string IpSetAll => Path.Combine(ListsDir, "ipset-all.txt");
    public string IpSetBackup => IpSetAll + ".backup";
    public string ListGeneral => Path.Combine(ListsDir, "list-general.txt");
    public string ListGoogle => Path.Combine(ListsDir, "list-google.txt");
    public string ListExclude => Path.Combine(ListsDir, "list-exclude.txt");
    public string IpSetExclude => Path.Combine(ListsDir, "ipset-exclude.txt");
    public string ListGeneralUser => Path.Combine(ListsDir, "list-general-user.txt");
    public string ListExcludeUser => Path.Combine(ListsDir, "list-exclude-user.txt");
    public string IpSetExcludeUser => Path.Combine(ListsDir, "ipset-exclude-user.txt");
    public string GameFilterFile => Path.Combine(UtilsDir, "game_filter.enabled");
    public string TargetsFile => Path.Combine(UtilsDir, "targets.txt");

    public bool IsValid => File.Exists(WinwsExe);

    /// <summary>Strategy .bat/.json files shipped with the distribution, service.bat excluded.</summary>
    public IEnumerable<string> StrategyFiles()
    {
        if (!Directory.Exists(Root)) return Array.Empty<string>();
        return Directory.EnumerateFiles(Root, "*.bat")
            .Where(p => !Path.GetFileName(p).StartsWith("service", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Looks for a usable distribution, in the order: explicit hint, app folder, common dev locations.</summary>
    public static ZapretPaths? Discover(string? hint = null)
    {
        foreach (var candidate in Candidates(hint))
        {
            try
            {
                var p = new ZapretPaths(candidate);
                if (p.IsValid) return p;
            }
            catch
            {
                // invalid path, keep looking
            }
        }
        return null;
    }

    public static IEnumerable<string> Candidates(string? hint = null)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(hint)) list.Add(hint!);

        var baseDir = AppContext.BaseDirectory;
        list.Add(Path.Combine(baseDir, "zapret"));
        list.Add(baseDir);
        list.Add(Path.GetFullPath(Path.Combine(baseDir, "..")));
        list.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..")));
        list.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..")));

        // Development layout: <repo>/src/Zapret.App/bin/Debug/net9.0-windows -> <repo>/src/zapret
        var up = baseDir;
        for (var i = 0; i < 6; i++)
        {
            up = Path.GetFullPath(Path.Combine(up, ".."));
            list.Add(Path.Combine(up, "src", "zapret"));
            list.Add(Path.Combine(up, "zapret"));
        }

        return list.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
