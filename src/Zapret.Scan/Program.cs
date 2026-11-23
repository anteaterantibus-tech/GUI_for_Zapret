using System.Text;
using Zapret.Core;

// Console runner for the same checks the GUI does on the "Подбор и тест" page.
// It starts every strategy from the distribution in turn, probes real endpoints
// and writes one line per strategy. Used from an elevated PowerShell.

var root = args.Length > 0 ? args[0] : @"D:\PROJECT\OpenCode\Zapret\src\zapret";
var outPath = args.Length > 1 ? args[1] : Path.Combine(root, "utils", "scan-result.txt");

var paths = new ZapretPaths(root);
if (!paths.IsValid)
{
    Console.WriteLine("Не найден winws.exe в " + root);
    return 2;
}

if (!WindowsProcess.IsElevated)
{
    Console.WriteLine("Нужны права администратора.");
    return 3;
}

var text = new StringBuilder();
void Log(string line)
{
    Console.WriteLine(line);
    text.AppendLine(line);
    try { File.WriteAllText(outPath, text.ToString(), new UTF8Encoding(false)); } catch { }
}

var gameFilter = GameFilter.Load(paths);

// Original order of the distribution: general first, then ALT, ALT2..ALT13,
// EXP, then the FAKE TLS AUTO and SIMPLE FAKE families.
string[] order =
{
    "general",
    "ALT",
    "ALT2",
    "ALT3",
    "ALT4",
    "ALT5",
    "ALT6",
    "ALT7",
    "ALT8",
    "ALT9",
    "ALT10",
    "ALT11",
    "ALT12",
    "ALT13",
    "EXP",
    "FAKE TLS AUTO",
    "FAKE TLS AUTO ALT",
    "FAKE TLS AUTO ALT2",
    "FAKE TLS AUTO ALT3",
    "SIMPLE FAKE",
    "SIMPLE FAKE ALT",
    "SIMPLE FAKE ALT2"
};

var parsed = BatStrategyParser.ParseAll(paths);
var byName = parsed.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
var strategies = new List<Strategy>();
foreach (var name in order)
{
    if (byName.TryGetValue(name, out var s)) strategies.Add(s);
}
strategies.AddRange(parsed.Where(s => !order.Contains(s.Name, StringComparer.OrdinalIgnoreCase)));

var targets = new List<ProbeTarget>
{
    new("Discord", "https://discord.com", "discord.com"),
    new("Discord CDN", "https://cdn.discordapp.com", "cdn.discordapp.com"),
    new("YouTube", "https://www.youtube.com", "www.youtube.com"),
    new("YouTube видео", "https://redirector.googlevideo.com", "redirector.googlevideo.com"),
    new("Google", "https://www.google.com", "www.google.com"),
    new("Cloudflare", "https://www.cloudflare.com", "www.cloudflare.com"),
    new("Telegram Web", "https://web.telegram.org", "web.telegram.org"),
    new("DNS Google", null, "8.8.8.8")
};

Log($"Корень: {paths.Root}");
Log($"Стратегий: {strategies.Count}, целей: {targets.Count}, GameFilter: {gameFilter.Status}");
Log("");

await WindowsProcess.ScAsync("stop", "zapret");
for (var i = 0; i < 20; i++)
{
    await Task.Delay(250);
    if (await WindowsProcess.GetServiceStateAsync("zapret") is not ("RUNNING" or "STOP_PENDING")) break;
}
await Task.Delay(1000);

try
{
    var baselineOnly = args.Skip(2).Any(a => a.Equals("baseline", StringComparison.OrdinalIgnoreCase));
    var only = args.Length > 2 && !baselineOnly ? args[2] : null;
    if (only is not null)
    {
        strategies = strategies.Where(s => s.Name.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
        if (strategies.Count == 0)
        {
            Log("Стратегия не найдена: " + only);
            return 4;
        }
    }

    if (baselineOnly)
    {
        using var prober = new Prober(4);
        var outcomes = await Task.WhenAll(targets.Select(t => prober.ProbeAsync(t)));
        Log("BASELINE_TARGETS\t" + string.Join(" ", outcomes.Select(o =>
            $"{Short(o.Name)}={o.OkCount}/{o.Attempts}")));
        foreach (var host in new[] { "discord.com", "www.youtube.com" })
        {
            var dpi = await DpiTester.TestAsync(host, 5000);
            Log($"BASELINE_DPI\t{host}\t{dpi.Text}");
        }
        return 0;
    }

    using var runner = new WinwsRunner(paths, Path.Combine(paths.UtilsDir, "logs"));
    var tester = new StrategyTester(paths, runner, gameFilter);

    foreach (var strategy in strategies)
    {
        Log($"=== {strategy.Name} ===");
        var result = await tester.RunAsync(strategy, targets);

        var perTarget = string.Join(" ", result.Outcomes.Select(o =>
            $"{Short(o.Name)}={o.OkCount}/{o.Attempts}"));
        var dpi = result.Dpi.Count == 0
            ? "n/a"
            : string.Join("+", result.Dpi.Select(d => d.Verdict == DpiVerdict.Ok ? "ok" : d.Verdict.ToString()));
        var udp = result.Udp is null ? "n/a" : (result.Udp.Ok ? "ok" : "fail");
        var media = result.Media is null ? "n/a" : (result.Media.Verdict == MediaVerdict.Ok ? "ok" : result.Media.Verdict.ToString());

        Log($"RESULT\t{strategy.Name}\t{result.Verdict}\t{result.OkRatio}\t" +
            $"lat={result.AvgLatencyMs:0}\tdpi={dpi}\tudp={udp}\tvideo={media}\t" +
            $"score={result.Score:0}\tclass={result.QualityClass}\terror={result.Error}");
        Log("TARGETS\t" + perTarget);
        Log("");
    }
}
catch (Exception ex)
{
    Log("ОШИБКА: " + ex);
}
finally
{
    WindowsProcess.KillAll("winws");
    await Task.Delay(1000);
    await WindowsProcess.ScAsync("start", "zapret");
    for (var i = 0; i < 40; i++)
    {
        await Task.Delay(500);
        if (await WindowsProcess.GetServiceStateAsync("zapret") is "RUNNING") break;
    }
    Log("Служба zapret: " + (await WindowsProcess.GetServiceStateAsync("zapret") ?? "нет"));
}

return 0;

static string Short(string name) => name switch
{
    "Discord CDN" => "CDN",
    "YouTube видео" => "YTvideo",
    "YouTube" => "YT",
    "Telegram Web" => "TG",
    "DNS Google" => "DNS",
    _ => name.Replace(" ", "")
};
