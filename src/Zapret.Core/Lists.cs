using System.Text;

namespace Zapret.Core;

public enum IpSetMode
{
    None,
    Any,
    Loaded
}

/// <summary>
/// Mirrors the three ipset modes of service.bat:
/// none  - the list contains only the 203.0.113.113/32 marker, no IP is filtered;
/// any   - the list is empty, every IP matches;
/// loaded- the real list from the repository.
/// </summary>
public static class IpSetManager
{
    public const string NoneMarker = "203.0.113.113/32";

    public static IpSetMode GetMode(ZapretPaths paths)
    {
        if (!File.Exists(paths.IpSetAll)) return IpSetMode.None;
        var text = File.ReadAllText(paths.IpSetAll);
        if (string.IsNullOrWhiteSpace(text)) return IpSetMode.Any;
        return text.Contains(NoneMarker, StringComparison.Ordinal) ? IpSetMode.None : IpSetMode.Loaded;
    }

    public static string Describe(IpSetMode mode) => mode switch
    {
        IpSetMode.None => "none (только домены)",
        IpSetMode.Any => "any (все адреса)",
        _ => "loaded (список из репозитория)"
    };

    public static bool SetMode(ZapretPaths paths, IpSetMode mode, out string message)
    {
        try
        {
            Directory.CreateDirectory(paths.ListsDir);

            switch (mode)
            {
                case IpSetMode.None:
                    if (File.Exists(paths.IpSetAll) && IpSetManager.GetMode(paths) == IpSetMode.Loaded)
                    {
                        File.Copy(paths.IpSetAll, paths.IpSetBackup, overwrite: true);
                    }
                    File.WriteAllText(paths.IpSetAll, NoneMarker + "\r\n", new UTF8Encoding(false));
                    message = "IPSet переключён в режим none.";
                    return true;

                case IpSetMode.Any:
                    if (File.Exists(paths.IpSetAll) && IpSetManager.GetMode(paths) == IpSetMode.Loaded)
                    {
                        File.Copy(paths.IpSetAll, paths.IpSetBackup, overwrite: true);
                    }
                    File.WriteAllText(paths.IpSetAll, "", new UTF8Encoding(false));
                    message = "IPSet переключён в режим any.";
                    return true;

                case IpSetMode.Loaded:
                    if (!File.Exists(paths.IpSetBackup))
                    {
                        message = "Нет резервной копии списка. Сначала обновите IPSet из репозитория.";
                        return false;
                    }
                    File.Copy(paths.IpSetBackup, paths.IpSetAll, overwrite: true);
                    message = "IPSet переключён в режим loaded.";
                    return true;
            }

            message = "Неизвестный режим.";
            return false;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }
}

/// <summary>User lists that survive a reinstall/update, same files as upstream 1.9.7+.</summary>
public sealed class UserLists
{
    private readonly ZapretPaths _paths;

    public UserLists(ZapretPaths paths) => _paths = paths;

    public string GeneralUser => _paths.ListGeneralUser;
    public string ExcludeUser => _paths.ListExcludeUser;
    public string IpSetExcludeUser => _paths.IpSetExcludeUser;

    public void EnsureDefaults()
    {
        Directory.CreateDirectory(_paths.ListsDir);

        if (!File.Exists(GeneralUser))
            File.WriteAllText(GeneralUser, "# Never leave this file empty\r\ndomain.example.abc\r\n", new UTF8Encoding(false));

        if (!File.Exists(ExcludeUser))
            File.WriteAllText(ExcludeUser, "domain.example.abc\r\n", new UTF8Encoding(false));

        if (!File.Exists(IpSetExcludeUser))
            File.WriteAllText(IpSetExcludeUser, "203.0.113.113/32\r\n", new UTF8Encoding(false));
    }

    public string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    public void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
    }

    public IReadOnlyList<string> ReadDomains(string path) =>
        Read(path)
            .Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

    public void AddDomain(string path, string domain)
    {
        var domains = ReadDomains(path).ToList();
        if (domains.Contains(domain, StringComparer.OrdinalIgnoreCase)) return;
        Write(path, string.Join("\r\n", domains.Append(domain)) + "\r\n");
    }

    public void RemoveDomain(string path, string domain)
    {
        var domains = ReadDomains(path).Where(d => !d.Equals(domain, StringComparison.OrdinalIgnoreCase)).ToList();
        Write(path, string.Join("\r\n", domains) + "\r\n");
    }
}
