using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zapret.Core;

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZapretControl");

    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string CacheDir => Path.Combine(DataDir, "cache");

    public static void Ensure()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CacheDir);
    }
}

public sealed class TargetSetting
{
    public string Name { get; set; } = "";
    public string? Url { get; set; }
    public string? PingHost { get; set; }
    public bool Enabled { get; set; } = true;

    public ProbeTarget ToProbeTarget() => new(Name, Url, PingHost);
}

public sealed class AppSettings
{
    public string? ZapretRoot { get; set; }
    public string? LastStrategy { get; set; }

    /// <summary>Start the GUI with Windows (HKCU Run entry, --minimized).</summary>
    public bool RunAtLogon { get; set; }

    /// <summary>Start the selected strategy automatically when the GUI starts.</summary>
    public bool AutoStartStrategy { get; set; }

    public bool MinimizeToTray { get; set; } = true;
    public bool CheckUpdatesOnStart { get; set; } = true;
    public bool HasSeenGuide { get; set; }
    public string? LastUpdateCheckUtc { get; set; }
    public string? SkippedVersion { get; set; }

    public bool PreserveActiveFakesOnUpdate { get; set; } = true;
    public bool PreserveIpSetOnUpdate { get; set; } = true;

    /// <summary>How lists/*.txt and utils/targets.txt are treated during an update.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ListMergeMode ListUpdateMode { get; set; } = ListMergeMode.Merge;

    /// <summary>Download and install a new release automatically on start (no questions).</summary>
    public bool AutoInstallUpdates { get; set; }

    public int ProbeTimeoutSeconds { get; set; } = 4;
    public bool RunDpiTest { get; set; } = true;
    public bool RunUdpTest { get; set; } = true;
    public bool RunMediaTest { get; set; } = true;

    public List<TargetSetting> Targets { get; set; } = new();

    [JsonIgnore] public bool HasTargets => Targets.Count > 0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // corrupted settings: fall back to defaults
        }

        var defaults = new AppSettings();
        defaults.Targets = DefaultTargets();
        return defaults;
    }

    public void Save()
    {
        AppPaths.Ensure();
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Targets from utils/targets.txt, or built-in defaults.</summary>
    public static List<TargetSetting> DefaultTargets() => new()
    {
        new() { Name = "Discord", Url = "https://discord.com", PingHost = "discord.com" },
        new() { Name = "Discord CDN", Url = "https://cdn.discordapp.com", PingHost = "cdn.discordapp.com" },
        new() { Name = "YouTube", Url = "https://www.youtube.com", PingHost = "www.youtube.com" },
        new() { Name = "YouTube видео", Url = "https://redirector.googlevideo.com", PingHost = "redirector.googlevideo.com" },
        new() { Name = "Google", Url = "https://www.google.com", PingHost = "www.google.com" },
        new() { Name = "Cloudflare", Url = "https://www.cloudflare.com", PingHost = "www.cloudflare.com" },
        new() { Name = "Telegram Web", Url = "https://web.telegram.org", PingHost = "web.telegram.org" },
        new() { Name = "DNS Google", Url = null, PingHost = "8.8.8.8" }
    };

    public static List<TargetSetting> LoadTargetsFromFile(ZapretPaths paths)
    {
        var result = new List<TargetSetting>();
        if (!File.Exists(paths.TargetsFile)) return result;

        foreach (var line in File.ReadAllLines(paths.TargetsFile))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, "^\\s*(\\w+)\\s*=\\s*\"(.+)\"\\s*$");
            if (!match.Success) continue;

            var name = match.Groups[1].Value;
            var value = match.Groups[2].Value;

            if (value.StartsWith("PING:", StringComparison.OrdinalIgnoreCase))
                result.Add(new TargetSetting { Name = name, PingHost = value[5..].Trim() });
            else
                result.Add(new TargetSetting
                {
                    Name = name,
                    Url = value,
                    PingHost = new Uri(value).Host
                });
        }
        return result;
    }
}
