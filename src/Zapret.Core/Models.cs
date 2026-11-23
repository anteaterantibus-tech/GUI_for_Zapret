using System.Text;

namespace Zapret.Core;

/// <summary>One raw winws option, e.g. <c>--dpi-desync=multisplit</c> or <c>--new</c>.</summary>
public sealed class WinwsArg
{
    public WinwsArg(string name, string? value = null)
    {
        Name = name;
        Value = value;
    }

    /// <summary>Option name including the leading dashes, lower-cased.</summary>
    public string Name { get; }

    public string? Value { get; set; }

    public override string ToString() => Value is null ? Name : $"{Name}={Value}";
}

/// <summary>A single winws "profile" (one filter block delimited by --new).</summary>
public sealed class WinwsProfile
{
    public List<WinwsArg> Args { get; } = new();

    public string? Get(string name) =>
        Args.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    public IEnumerable<string> GetAll(string name) =>
        Args.Where(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!);

    public bool Has(string name) => Args.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Ports of the filter block, in the form winws prints them.</summary>
    public string Ports
    {
        get
        {
            var tcp = Get("--filter-tcp");
            var udp = Get("--filter-udp");
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(tcp)) parts.Add("tcp " + tcp);
            if (!string.IsNullOrEmpty(udp)) parts.Add("udp " + udp);
            return parts.Count == 0 ? "any" : string.Join(", ", parts);
        }
    }

    /// <summary>What this block applies to: hostlist / ipset / ip-id / l7 / everything.</summary>
    public string Scope
    {
        get
        {
            var parts = new List<string>();
            if (Get("--filter-l7") is { Length: > 0 } l7) parts.Add("l7:" + l7);
            var hl = GetAll("--hostlist").Select(DisplayName.Of).ToList();
            if (hl.Count > 0) parts.Add("hostlist:" + string.Join("+", hl));
            if (GetAll("--hostlist-domains").ToList() is { Count: > 0 } hd) parts.Add("domains:" + string.Join(",", hd));
            if (Get("--ipset") is { Length: > 0 }) parts.Add("ipset");
            if (Get("--hostlist-auto") is { Length: > 0 }) parts.Add("auto-hostlist");
            if (parts.Count == 0) parts.Add("all traffic");
            return string.Join("; ", parts);
        }
    }

    /// <summary>dpi-desync chain (fake,multisplit, ...).</summary>
    public string Desync
    {
        get
        {
            var list = new List<string>();
            foreach (var value in GetAll("--dpi-desync"))
            {
                foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!list.Contains(token)) list.Add(token);
                }
            }
            return list.Count == 0 ? "-" : string.Join(",", list);
        }
    }

    public string Repeats => Get("--dpi-desync-repeats") ?? "-";
    public string SplitPos => Get("--dpi-desync-split-pos") ?? "-";
    public string SeqOvl => Get("--dpi-desync-split-seqovl") ?? "-";
    public string Fooling => Get("--dpi-desync-fooling") ?? "-";
    public string Cutoff => Get("--dpi-desync-cutoff") ?? "-";

    public IEnumerable<string> FakeFiles =>
        Args.Where(a => a.Name.StartsWith("--dpi-desync-fake", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => DisplayName.Of(v!))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>File name out of "%BIN%tls_clienthello_www_google_com.bin" or a full path.</summary>
    private static class DisplayName
    {
        public static string Of(string value)
        {
            var name = value;
            var slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) name = name[(slash + 1)..];
            var percent = name.LastIndexOf('%');
            if (percent >= 0) name = name[(percent + 1)..];
            return name;
        }
    }

    public string Fakes
    {
        get
        {
            var files = FakeFiles.ToList();
            return files.Count == 0 ? "-" : string.Join(", ", files);
        }
    }
}

/// <summary>A complete winws command line: global options plus one or more profiles.</summary>
public sealed class Strategy
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }

    /// <summary>Short name shown in the UI, e.g. "ALT11".</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Verbatim winws segments: segment 0 holds the global options (--wf-tcp/--wf-udp)
    /// followed by the first filter block, every next segment starts after --new.
    /// Keeping them verbatim guarantees the command line is rebuilt byte for byte.
    /// </summary>
    public List<WinwsProfile> Segments { get; } = new();

    /// <summary>Filter blocks as shown in the UI (the first one includes the global options).</summary>
    public IReadOnlyList<WinwsProfile> Profiles => Segments;

    /// <summary>Human readable note (from the curated catalog, may be empty).</summary>
    public string Note { get; init; } = "";

    public string? GetGlobal(string name) =>
        Segments.Count > 0 ? Segments[0].Get(name) : null;

    public string WfTcp => GetGlobal("--wf-tcp") ?? "-";
    public string WfUdp => GetGlobal("--wf-udp") ?? "-";

    public IReadOnlyList<string> WfTcpPorts => SplitPorts(WfTcp);
    public IReadOnlyList<string> WfUdpPorts => SplitPorts(WfUdp);

    private static IReadOnlyList<string> SplitPorts(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>All distinct desync methods used by the strategy.</summary>
    public IReadOnlyList<string> Methods
    {
        get
        {
            var result = new List<string>();
            foreach (var p in Profiles)
            {
                foreach (var m in p.Desync.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (m != "-" && !result.Contains(m)) result.Add(m);
                }
            }
            return result;
        }
    }

    public int TotalRepeats => Profiles
        .Select(p => int.TryParse(p.Repeats, out var r) ? r : 0)
        .DefaultIfEmpty(0)
        .Sum();

    /// <summary>One line technical summary, used by the strategy list.</summary>
    public string Summary
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Profiles.Count).Append(" профил");
            sb.Append(Profiles.Count == 1 ? "ь" : Profiles.Count is >= 2 and <= 4 ? "я" : "ей");
            if (Methods.Count > 0) sb.Append(" · ").Append(string.Join("+", Methods));
            var fakes = Profiles.SelectMany(p => p.FakeFiles).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (fakes > 0) sb.Append(" · фейков: ").Append(fakes);
            return sb.ToString();
        }
    }

    public override string ToString() => Name;
}

/// <summary>Curated notes for the strategy files shipped with 1.10.4.</summary>
public static class StrategyCatalog
{
    private static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["general"] = "Базовая стратегия: fake для QUIC/UDP, multisplit по SNI для TCP. Начинать проверку стоит с неё.",
        ["ALT"] = "Вариант на fake + fakedsplit: другой размер перекрытия последовательности, помогает при обрезке TLS.",
        ["ALT2"] = "multisplit без фейка TLS: когда фейк ломает соединение, а простой сплит проходит.",
        ["ALT3"] = "hostfakesplit + fake: фейковый хост в первом пакете, обходит часть DPI по SNI.",
        ["ALT4"] = "fake + multisplit с другим перекрытием, средний по агрессивности вариант.",
        ["ALT5"] = "syndata + multidisorder на TCP-блоках: для провайдеров, которые режут по порядку пакетов.",
        ["ALT6"] = "чистый multisplit без fake, минимальное вмешательство в трафик.",
        ["ALT7"] = "multisplit для общего списка и syndata для ipset: комбинированный профиль.",
        ["ALT8"] = "только fake, без сплита: для провайдеров, которые не реагируют на разрезание пакета.",
        ["ALT9"] = "hostfakesplit на большинстве блоков, агрессивнее ALT3 по числу профилей.",
        ["ALT10"] = "fake-only профили с увеличенным числом повторов.",
        ["ALT11"] = "fake + multisplit с fooling=ts и повторами 8: рабочая лошадка для многих провайдеров.",
        ["ALT12"] = "ALT11 с hostfakesplit на части блоков (TLS 1.3 через фейковый хост).",
        ["ALT13"] = "fake + hostfakesplit почти на всех TCP-блоках, самый агрессивный из ALT-серии.",
        ["EXP"] = "Экспериментальная сборка, повторяет ALT12 с другими фейками.",
        ["FAKE TLS AUTO"] = "Автоматический подбор TLS-фейка, fake+multidisorder.",
        ["FAKE TLS AUTO ALT"] = "Автоподбор фейка + fakedsplit.",
        ["FAKE TLS AUTO ALT2"] = "Автоподбор фейка + multisplit.",
        ["FAKE TLS AUTO ALT3"] = "Автоподбор фейка + multisplit с другим набором блоков.",
        ["SIMPLE FAKE"] = "Простые fake-профили без сплита, самый лёгкий вариант.",
        ["SIMPLE FAKE ALT"] = "Простые fake-профили с другими фейками и повторами.",
        ["SIMPLE FAKE ALT2"] = "SIMPLE FAKE с обновлённым TLS-фейком (в 1.10.4).",
        ["ALT_Custom"] = "Моя стратегия для этой сети: fake+multisplit с ts, hostfakesplit для Google, укороченные повторы."
    };

    public static string Get(string name) =>
        Notes.TryGetValue(name, out var note) ? note : "";
}
