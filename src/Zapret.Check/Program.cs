using Zapret.Core;

var failures = new List<string>();
var root = args.Length > 0 ? args[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "zapret"));
var paths = new ZapretPaths(root);

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"Zapret root: {paths.Root}");
Console.WriteLine($"Valid: {paths.IsValid}, installed version: {UpdateService.GetInstalledVersion(paths) ?? "?"}");

void Check(string name, bool condition, string detail = "")
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
    if (!condition) failures.Add(name);
}

if (args.Contains("--youtube-off"))
{
    var youtube = YouTubeExclusion.Apply(paths);
    Console.WriteLine(youtube.Message);
    return youtube.Success ? 0 : 1;
}

// ---- version comparison ----
Check("version 1.10.4 > 1.9.9d", UpdateService.CompareVersions("1.10.4", "1.9.9d") > 0);
Check("version 1.9.9d > 1.9.9", UpdateService.CompareVersions("1.9.9d", "1.9.9") > 0);
Check("version 1.10.4 == v1.10.4", UpdateService.CompareVersions("1.10.4", "v1.10.4") == 0);
Check("version 1.10.4 < 1.10.5", UpdateService.CompareVersions("1.10.4", "1.10.5") < 0);

// ---- tokenizer ----
var tokens = BatStrategyParser.Tokenize("--wf-tcp=80,443 --hostlist=\"C:\\a b\\list.txt\" --new --dpi-desync=fake");
Check("tokenizer count", tokens.Count == 4, string.Join(" | ", tokens));
Check("tokenizer quoted path", tokens[1] == "--hostlist=C:\\a b\\list.txt");

// ---- game filter range ----
Check("range 1024-65535", GameFilter.IsValidRange("1024-65535"));
Check("range list", GameFilter.IsValidRange("1024-1934,1936-65535"));
Check("range rejects 0", !GameFilter.IsValidRange("0-100"));
Check("range rejects reversed", !GameFilter.IsValidRange("2000-1000"));

// ---- three-way merge of list files ----
{
    string Merge(string ours, string theirs, string? baseText, out ListMergeStats stats) =>
        ListMerger.Merge(ours, theirs, baseText, out stats);

    var merged = Merge("a\r\nb\r\nc\r\nuser1\r\n", "a\r\nc\r\nd\r\n", "a\r\nb\r\nc\r\n", out var stats1);
    var lines = merged.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
    Check("merge adds new release lines", lines.Contains("d"), string.Join(",", lines));
    Check("merge applies upstream removals", !lines.Contains("b"), string.Join(",", lines));
    Check("merge keeps user lines", lines.Contains("user1"), string.Join(",", lines));
    Check("merge has no duplicates", lines.Length == lines.Distinct().Count(), string.Join(",", lines));

    var edited = Merge("x\r\ny-custom\r\n", "x\r\ny\r\nz\r\n", "x\r\ny\r\n", out _);
    var editedLines = edited.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
    Check("merge keeps edited line", editedLines.Contains("y-custom"), string.Join(",", editedLines));
    Check("merge adds release line next to edit", editedLines.Contains("z"), string.Join(",", editedLines));

    var union = Merge("a\r\nuser1\r\n", "a\r\nb\r\n", null, out var unionStats);
    var unionLines = union.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
    Check("merge union without base", unionLines.SequenceEqual(new[] { "a", "user1", "b" }),
        string.Join(",", unionLines) + $" (+{unionStats.Added})");

    var removedAll = Merge("a\r\nb\r\n", "a\r\n", "a\r\nb\r\n", out var removalStats);
    Check("merge removes only dropped lines", removedAll.Trim() == "a", removedAll.Trim() + $" (-{removalStats.Removed})");

    var fresh = Merge("", "a\r\nb\r\n", "a\r\n", out var freshStats);
    Check("merge into empty file", fresh.Trim() == "a\r\nb", fresh + $" (+{freshStats.Added})");
}

if (!paths.IsValid)
{
    Console.WriteLine("Distribution not found, parsing checks skipped.");
    return failures.Count == 0 ? 0 : 1;
}

// ---- parse all strategies ----
var strategyFiles = paths.StrategyFiles().ToList();
Console.WriteLine($"\nStrategy files found: {strategyFiles.Count}");
try
{
    var probe = BatStrategyParser.Parse(strategyFiles.First(), paths);
    Console.WriteLine($"Probe parse: {probe.Name}, profiles={probe.Profiles.Count}");
}
catch (Exception ex)
{
    Console.WriteLine("Probe parse failed: " + ex);
}

var strategies = BatStrategyParser.ParseAll(paths);
Console.WriteLine($"\nStrategies parsed: {strategies.Count}");
Check("22+ strategy files", strategies.Count >= 22, strategies.Count.ToString());

Console.WriteLine($"{"name",-22} {"files",5} {"blk",3} {"methods",-46} {"repeats",7}");
foreach (var s in strategies.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
{
    var repeats = string.Join("/", s.Profiles.Select(p => p.Repeats).Distinct());
    Console.WriteLine($"{s.Name,-22} {s.Profiles.Sum(p => p.Args.Count),5} {s.Profiles.Count,3} {string.Join("+", s.Methods),-46} {repeats,7}");
    if (s.Profiles.Count == 0) failures.Add("no profiles: " + s.Name);
    if (s.Profiles.Any(p => p.Args.Count == 0)) failures.Add("empty profile: " + s.Name);
}

// Round-trip fidelity: parsed options must rebuild into the same option sequence.
foreach (var s in strategies)
{
    var original = BatStrategyParser.Tokenize(BatStrategyParser.BuildCommandText(s.FilePath))
        .Where(t => t.StartsWith("--"))
        .Select(t => t.Split('=')[0])
        .ToList();
    var rebuilt = WinwsCommandBuilder.BuildArgs(s, new GameFilter(), paths)
        .Select(t => t.Split('=')[0])
        .ToList();

    if (!original.SequenceEqual(rebuilt))
        failures.Add("round-trip mismatch: " + s.Name +
                     $" (original {original.Count}, rebuilt {rebuilt.Count})");
}

// every strategy must produce a command with --wf-tcp/--wf-udp and at least one --new
foreach (var s in strategies)
{
    var gf = new GameFilter { Mode = GameFilterMode.All, TcpPorts = "1024-65535", UdpPorts = "1024-65535" };
    var cmd = WinwsCommandBuilder.BuildCommandLine(s, gf, paths);
    if (!cmd.Contains("--wf-tcp=") || !cmd.Contains("--wf-udp=")) failures.Add("missing wf: " + s.Name);
    if (!cmd.Contains("--new")) failures.Add("missing --new: " + s.Name);
    if (cmd.Contains('%')) failures.Add("unexpanded placeholder: " + s.Name + " -> " + cmd[..Math.Min(120, cmd.Length)]);
}

// ALT11 must resolve the bin/lists variables and the game filter placeholders
var alt11 = strategies.FirstOrDefault(s => s.Name == "ALT11");
if (alt11 is not null)
{
    var disabled = WinwsCommandBuilder.BuildCommandLine(alt11, new GameFilter(), paths);
    Check("ALT11 disabled game filter uses port 12", disabled.Contains("--wf-tcp=80,443,2053,2083,2087,2096,8443,12"), "");
    var enabled = WinwsCommandBuilder.BuildCommandLine(alt11, new GameFilter { Mode = GameFilterMode.Tcp, TcpPorts = "1024-65535" }, paths);
    Check("ALT11 enabled game filter resolved", enabled.Contains("8443,1024-65535"));
    Check("ALT11 bin path resolved", enabled.Contains(Path.Combine(paths.BinDir, "tls_clienthello_www_sferum_ru.bin")));

    Console.WriteLine("\nALT11 profiles:");
    foreach (var p in alt11.Profiles)
        Console.WriteLine($"  ports={p.Ports,-34} desync={p.Desync,-28} repeats={p.Repeats,-3} fooling={p.Fooling,-6} fakes={p.Fakes}");
}

// ---- ipset and lists state (read only) ----
Console.WriteLine($"\nIPSet mode: {IpSetManager.Describe(IpSetManager.GetMode(paths))}");
Console.WriteLine($"Game filter: {GameFilter.Load(paths).Status}");
Console.WriteLine($"Targets: {AppSettings.LoadTargetsFromFile(paths).Count}");
Console.WriteLine($"User lists: {File.Exists(paths.ListGeneralUser)}/{File.Exists(paths.ListExcludeUser)}/{File.Exists(paths.IpSetExcludeUser)}");

// ---- summary of what makes each strategy different ----
Console.WriteLine("\nStrategy fingerprint (first profile):");
foreach (var s in strategies.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
{
    var p = s.Profiles[0];
    Console.WriteLine($"  {s.Name,-22} {p.Ports,-30} {p.Desync,-22} fake={p.Fakes}");
}

// ---- optional end-to-end updater test (downloads the latest release) ----
if (args.Contains("--update-test"))
{
    Console.WriteLine("\n=== UPDATE TEST ===");
    var testRoot = Path.Combine(Path.GetTempPath(), "zapret-update-test", "zapret");
    if (Directory.Exists(Path.GetDirectoryName(testRoot)!)) Directory.Delete(Path.GetDirectoryName(testRoot)!, true);
    CopyTree(paths.Root, testRoot);

    var testPaths = new ZapretPaths(testRoot);
    var updater = new UpdateService();

    // user data that must survive the update
    File.WriteAllText(testPaths.ListGeneralUser, "# custom\r\nmy-domain.example\r\n");
    File.WriteAllText(testPaths.ListExcludeUser, "keep-me.example\r\n");
    File.WriteAllText(testPaths.IpSetAll, "10.20.30.0/24\r\n");
    File.WriteAllText(testPaths.GameFilterFile, "mode=tcp\r\ntcp=1024-2000\r\nudp=1024-65535\r\n");
    var fakeBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
    File.WriteAllBytes(Path.Combine(testPaths.BinDir, "ACTIVE_DISCORD_UDP.bin"), fakeBytes);

    // manual edit of a stock list: a custom domain plus a removed upstream line
    var manualDomain = "my-manual-domain.example";
    var stockLines = File.ReadAllLines(testPaths.ListGeneral).Where(l => l.Trim().Length > 0).ToList();
    var droppedLine = stockLines[0];
    var editedLines = stockLines.Skip(1).Append(manualDomain).ToList();
    File.WriteAllLines(testPaths.ListGeneral, editedLines);

    // the user disabled update checks by deleting the flag
    var updateFlag = Path.Combine(testPaths.UtilsDir, "check_updates.enabled");
    File.Delete(updateFlag);

    var serviceBefore = await WindowsProcess.GetServiceStateAsync("zapret");

    var release = await updater.GetLatestReleaseAsync();
    if (release is null)
    {
        Console.WriteLine("FAIL  cannot fetch release");
        return 1;
    }
    Console.WriteLine($"Latest release: {release.Tag}, zip: {release.ZipUrl}");

    var zip = await updater.DownloadReleaseAsync(release, null);
    Console.WriteLine($"Downloaded {new FileInfo(zip).Length / 1024} KB to {zip}");

    var log = new Progress<string>(line => Console.WriteLine("  " + line));
    var result = await updater.InstallAsync(zip, testPaths, new UpdateOptions(), log);

    Check("update success", result.Success, result.Message);
    Check("update version", result.NewVersion == release.Tag, $"{result.OldVersion} -> {result.NewVersion}");
    Check("backup created", result.BackupPath is not null && Directory.Exists(result.BackupPath), result.BackupPath ?? "");
    Check("winws present", File.Exists(testPaths.WinwsExe));
    Check("user list preserved", File.ReadAllText(testPaths.ListGeneralUser).Contains("my-domain.example"));
    Check("exclude list preserved", File.ReadAllText(testPaths.ListExcludeUser).Contains("keep-me.example"));
    Check("ipset preserved", File.ReadAllText(testPaths.IpSetAll).Contains("10.20.30.0/24"));
    Check("game filter preserved", File.ReadAllText(testPaths.GameFilterFile).Contains("tcp=1024-2000"));
    Check("active fake preserved", File.ReadAllBytes(Path.Combine(testPaths.BinDir, "ACTIVE_DISCORD_UDP.bin")).SequenceEqual(fakeBytes));

    var mergedGeneral = File.ReadAllLines(testPaths.ListGeneral).Where(l => l.Trim().Length > 0).ToList();
    Check("manual list edit survives update", mergedGeneral.Contains(manualDomain));
    Check("removed upstream line comes back", mergedGeneral.Contains(droppedLine), droppedLine);
    Check("merged list has no duplicates",
        mergedGeneral.Count == mergedGeneral.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        mergedGeneral.Count.ToString());
    Check("disabled update check stays disabled", !File.Exists(updateFlag));

    var strategiesAfter = BatStrategyParser.ParseAll(testPaths);
    Check("strategies after update", strategiesAfter.Count >= 20, strategiesAfter.Count.ToString());
    Check("strategy parse after update", strategiesAfter.All(s => s.Profiles.Count >= 5));

    var serviceAfter = await WindowsProcess.GetServiceStateAsync("zapret");
    Check("foreign service untouched", serviceBefore == serviceAfter, $"{serviceBefore} -> {serviceAfter}");
}

// ---- optional diagnostics timing check ----
if (args.Contains("--diag"))
{
    Console.WriteLine("\n=== DIAGNOSTICS ===");
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var checks = await new Diagnostics(paths).RunAsync();
    stopwatch.Stop();
    foreach (var check in checks)
        Console.WriteLine($"  [{check.Status}] {check.Title}: {check.Detail}");
    Console.WriteLine($"Diagnostics finished in {stopwatch.Elapsed.TotalSeconds:0.0} s, {checks.Count} checks");
    Check("diagnostics completes under 60 s", stopwatch.Elapsed.TotalSeconds < 60, stopwatch.Elapsed.TotalSeconds.ToString("0.0"));
}

// ---- scoring model: errors must dominate, warnings must be classified ----
{
    var strategy = new Strategy { FilePath = "x.bat", FileName = "x.bat", Name = "X" };

    StrategyTestResult Result(bool full, DpiVerdict? dpi = null, bool? udpOk = null, MediaVerdict? media = null)
    {
        var result = new StrategyTestResult { Strategy = strategy };
        var outcome = new ProbeOutcome
        {
            Name = "target",
            HasUrl = true,
            HasPing = true,
            Http11 = true,
            Tls12 = true,
            Tls13 = full,
            Ping = full ? "10 ms" : "Timeout"
        };
        result.Outcomes.Add(outcome);
        if (dpi is not null)
            result.Dpi.Add(new DpiResult { Host = "discord.com", Protocol = "TLS", Verdict = dpi.Value });
        if (udpOk is not null)
            result.Udp = new StunResult { Ok = udpOk.Value, Attempts = 3, Successes = udpOk.Value ? 3 : 0 };
        if (media is not null)
            result.Media = new MediaResult { Verdict = media.Value, Bytes = 1000 };
        result.AvgLatencyMs = 100;
        result.Score = StrategyTester.ComputeScore(result);
        return result;
    }

    var clean = Result(true, DpiVerdict.Ok, true, MediaVerdict.Ok);
    Check("clean result is class 0", clean.QualityClass == 0, clean.QualityClass.ToString());
    Check("clean result verdict", clean.Verdict == "работает", clean.Verdict);
    Check("clean result no errors", clean.Errors == 0, clean.OkRatio);

    var withError = Result(false, DpiVerdict.Ok, true, MediaVerdict.Ok);
    Check("failed checks are errors", withError.Errors == 2, withError.OkRatio);
    Check("failed checks are class 2", withError.QualityClass == 2, withError.QualityClass.ToString());
    Check("failed checks dominate latency", clean.Score - withError.Score >= 200,
        $"{clean.Score} vs {withError.Score}");
    Check("failed checks verdict", withError.Verdict.Contains("отказов 2"), withError.Verdict);

    var blocked = Result(true, DpiVerdict.LikelyBlocked, true, MediaVerdict.Ok);
    Check("blocked dpi is class 1", blocked.QualityClass == 1, blocked.QualityClass.ToString());
    Check("blocked dpi verdict", blocked.Verdict.Contains("блок"), blocked.Verdict);
    Check("blocked dpi costs at least two checks", clean.Score - blocked.Score >= 300,
        $"{clean.Score} vs {blocked.Score}");

    var noUdp = Result(true, DpiVerdict.Ok, false, MediaVerdict.Ok);
    Check("failed udp is class 1", noUdp.QualityClass == 1, noUdp.QualityClass.ToString());
    Check("failed udp verdict", noUdp.Verdict.Contains("UDP"), noUdp.Verdict);

    var noMedia = Result(true, DpiVerdict.Ok, true, MediaVerdict.LikelyBlocked);
    Check("failed media is class 1", noMedia.QualityClass == 1, noMedia.QualityClass.ToString());
    Check("failed media verdict", noMedia.Verdict.Contains("видео"), noMedia.Verdict);

    var max = StrategyTester.MaxScore(new[] { new ProbeTarget("t", "https://example.com", "example.com") }, true);
    Check("max score accounts for all tests", Math.Abs(max - 860) < 0.1, max.ToString("0"));
}

// ---- caret escaping, dead targets, YouTube exclusion ----
{
    var fakeTls = strategies.FirstOrDefault(s => s.Name == "FAKE TLS AUTO");
    var bang = fakeTls?.Profiles
        .SelectMany(p => p.Args)
        .FirstOrDefault(a => a.Name == "--dpi-desync-fake-tls" && a.Value == "!");
    Check("caret-escaped ! is parsed as !", bang is not null,
        fakeTls is null ? "strategy not found" : string.Join(" | ", fakeTls.Profiles.SelectMany(p => p.Args)
            .Where(a => a.Name == "--dpi-desync-fake-tls").Select(a => a.Value)));

    var testStrategy = new Strategy { FilePath = "x.bat", FileName = "x.bat", Name = "X" };
    var testResult = new StrategyTestResult { Strategy = testStrategy };
    testResult.Outcomes.Add(new ProbeOutcome
    {
        Name = "Cloudflare",
        HasUrl = true,
        HasPing = true,
        Ping = "Timeout"
    });
    testResult.Outcomes.Add(new ProbeOutcome
    {
        Name = "Discord",
        HasUrl = true,
        HasPing = true,
        Http11 = true,
        Tls12 = true,
        Tls13 = true,
        Ping = "10 ms"
    });
    testResult.Score = StrategyTester.ComputeScore(testResult);

    Check("dead target is counted as 4 errors", testResult.Errors == 4, testResult.OkRatio);
    testResult.DropTargets(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cloudflare" });
    Check("dead target is removed from scoring", testResult.Errors == 0 && testResult.AttemptCount == 4,
        $"{testResult.OkRatio}, errors {testResult.Errors}");
    Check("score is recalculated after dropping", testResult.Score > 0, testResult.Score.ToString("0"));

    var temp = Path.Combine(Path.GetTempPath(), "zc-youtube-test");
    if (Directory.Exists(temp)) Directory.Delete(temp, true);
    Directory.CreateDirectory(Path.Combine(temp, "lists"));
    var ytPaths = new ZapretPaths(temp);
    File.Copy(paths.ListGoogle, ytPaths.ListGoogle);
    File.WriteAllText(ytPaths.ListExcludeUser, "domain.example.abc\r\n");

    var apply = YouTubeExclusion.Apply(ytPaths);
    Check("youtube exclusion applied", apply.Success, apply.Message);
    Check("youtube marker present", YouTubeExclusion.IsApplied(ytPaths));

    var google = File.ReadAllText(ytPaths.ListGoogle);
    Check("youtube removed from list-google",
        !google.Contains("youtube.com") && !google.Contains("googlevideo.com") && !google.Contains("ytimg"),
        google.Replace("\r\n", ", "));
    Check("non-youtube domains kept", google.Contains("play.google.com") && google.Contains("www.gstatic.com"));
    Check("youtube backup created", File.Exists(YouTubeExclusion.BackupPath(ytPaths)));

    var exclude = File.ReadAllText(ytPaths.ListExcludeUser);
    Check("youtube exclusions added", exclude.Contains("youtube.com") && exclude.Contains("googlevideo.com"));

    var revert = YouTubeExclusion.Revert(ytPaths);
    Check("youtube revert applied", revert.Success, revert.Message);
    Check("list-google restored", File.ReadAllText(ytPaths.ListGoogle).Contains("youtube.com"));
    Check("youtube marker removed", !YouTubeExclusion.IsApplied(ytPaths));
}

if (args.Contains("--net-tests"))
{
    Console.WriteLine("\n=== NET TESTS ===");
    var stun = await StunTester.TestAsync();
    Console.WriteLine("  " + stun.Text + " " + stun.Detail);
    var media = await MediaTester.TestAsync();
    Console.WriteLine("  " + media.Text + " " + media.Detail);
    Check("media downloads at least some bytes", media.Bytes > 0, media.Bytes + " bytes");
    Check("stun is reported", stun.Attempts == 3, stun.Text);
}

// ---- strategy import/export ----
{
    var temp = Path.Combine(Path.GetTempPath(), "zapret-transfer-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        var sample = BatStrategyParser.ParseAll(paths).Take(3).ToList();

        var folder = StrategyTransfer.ExportToFolder(sample, temp, new DateTime(2026, 11, 23, 10, 0, 0));
        Check("export folder created", Directory.Exists(folder) && Directory.GetFiles(folder, "*.bat").Length == 3, folder);

        var zipPath = Path.Combine(temp, "strategies.zip");
        StrategyTransfer.ExportToZip(sample, zipPath);
        Check("export zip created", File.Exists(zipPath) && new FileInfo(zipPath).Length > 0);

        var fromZip = StrategyTransfer.Read(new[] { zipPath }, out var invalidZip);
        Check("read zip returns strategies", fromZip.Count == sample.Count && invalidZip == 0,
            $"{fromZip.Count} files, invalid {invalidZip}");
        Check("read zip keeps names",
            fromZip.Select(f => f.Name).OrderBy(n => n).SequenceEqual(sample.Select(s => s.FileName).OrderBy(n => n)));

        var fromFolder = StrategyTransfer.Read(Directory.GetFiles(folder), out _);
        Check("read folder returns strategies", fromFolder.Count == sample.Count, fromFolder.Count.ToString());
        Check("read keeps file bytes",
            fromFolder.All(f => sample.Any(s => s.FileName.Equals(f.Name, StringComparison.OrdinalIgnoreCase) &&
                                                File.ReadAllBytes(s.FilePath).SequenceEqual(f.Bytes))));

        var junk = Path.Combine(temp, "junk.bat");
        File.WriteAllText(junk, "@echo off\r\necho hello\r\n");
        var junkRead = StrategyTransfer.Read(new[] { junk }, out var junkInvalid);
        Check("junk bat is rejected", junkRead.Count == 0 && junkInvalid == 1, $"{junkRead.Count}/{junkInvalid}");
    }
    finally
    {
        try { Directory.Delete(temp, true); } catch { }
    }
}

Console.WriteLine();
Console.WriteLine(failures.Count == 0 ? "ALL CHECKS PASSED" : $"{failures.Count} FAILURES: " + string.Join("; ", failures));
return failures.Count == 0 ? 0 : 1;

static void CopyTree(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
    foreach (var dir in Directory.EnumerateDirectories(source))
        CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
}
