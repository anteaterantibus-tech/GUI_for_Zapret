using System.Globalization;

namespace Zapret.Core;

public enum GameFilterMode
{
    Disabled,
    Tcp,
    Udp,
    All
}

/// <summary>
/// GameFilter settings, stored in utils/game_filter.enabled exactly like upstream service.bat does.
/// Placeholders %GameFilterTCP% / %GameFilterUDP% are resolved at command build time.
/// </summary>
public sealed class GameFilter
{
    public const string DisabledPort = "12";

    public GameFilterMode Mode { get; set; } = GameFilterMode.Disabled;
    public string TcpPorts { get; set; } = "1024-65535";
    public string UdpPorts { get; set; } = "1024-65535";

    public string EffectiveTcpPorts => Mode is GameFilterMode.All or GameFilterMode.Tcp ? TcpPorts : DisabledPort;
    public string EffectiveUdpPorts => Mode is GameFilterMode.All or GameFilterMode.Udp ? UdpPorts : DisabledPort;

    public string Status => Mode switch
    {
        GameFilterMode.All => $"включён (TCP+UDP {TcpPorts})",
        GameFilterMode.Tcp => $"включён (TCP {TcpPorts})",
        GameFilterMode.Udp => $"включён (UDP {UdpPorts})",
        _ => "выключен"
    };

    public static GameFilter Load(ZapretPaths paths)
    {
        var gf = new GameFilter();
        if (!File.Exists(paths.GameFilterFile)) return gf;

        foreach (var line in File.ReadAllLines(paths.GameFilterFile))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (key.Equals("mode", StringComparison.OrdinalIgnoreCase))
            {
                gf.Mode = value.ToLowerInvariant() switch
                {
                    "all" => GameFilterMode.All,
                    "tcp" => GameFilterMode.Tcp,
                    "udp" => GameFilterMode.Udp,
                    _ => GameFilterMode.Disabled
                };
            }
            else if (key.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                gf.Mode = GameFilterMode.All;
            }
            else if (key.Equals("tcp", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                gf.TcpPorts = value;
            }
            else if (key.Equals("udp", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
            {
                gf.UdpPorts = value;
            }
        }

        if (!IsValidRange(gf.TcpPorts)) gf.TcpPorts = "1024-65535";
        if (!IsValidRange(gf.UdpPorts)) gf.UdpPorts = "1024-65535";
        return gf;
    }

    public void Save(ZapretPaths paths)
    {
        Directory.CreateDirectory(paths.UtilsDir);
        File.WriteAllText(paths.GameFilterFile,
            $"mode={ModeName(Mode)}\r\ntcp={TcpPorts}\r\nudp={UdpPorts}\r\n");
    }

    public static string ModeName(GameFilterMode mode) => mode switch
    {
        GameFilterMode.All => "all",
        GameFilterMode.Tcp => "tcp",
        GameFilterMode.Udp => "udp",
        _ => "disabled"
    };

    /// <summary>Validates "1024", "1024-2048", "80,443,1000-2000" with ports 1..65535.</summary>
    public static bool IsValidRange(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return false;

        foreach (var item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = item.IndexOf('-');
            if (dash < 0)
            {
                if (!TryPort(item, out _)) return false;
                continue;
            }

            if (!TryPort(item[..dash], out var from) || !TryPort(item[(dash + 1)..], out var to)) return false;
            if (from > to) return false;
        }

        return true;
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
        port is >= 1 and <= 65535;
}
