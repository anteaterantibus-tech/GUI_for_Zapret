namespace Zapret.Core;

public sealed class StrategyTestResult
{
    public required Strategy Strategy { get; init; }
    public List<ProbeOutcome> Outcomes { get; } = new();
    public List<DpiResult> Dpi { get; } = new();
    public StunResult? Udp { get; set; }
    public MediaResult? Media { get; set; }
    public string Error { get; set; } = "";
    public double AvgLatencyMs { get; set; }
    public double Score { get; set; }

    public int OkCount => Outcomes.Sum(o => o.OkCount);
    public int AttemptCount => Outcomes.Sum(o => o.Attempts);
    public int Errors => Math.Max(0, AttemptCount - OkCount);
    public int FailedTargets => Outcomes.Count(o => o.Status == ProbeStatus.Fail);

    public bool DpiBlocked => Dpi.Any(d => d.Verdict is DpiVerdict.LikelyBlocked or DpiVerdict.Failed);
    public bool UdpFailed => Udp is { Ok: false };
    public bool MediaFailed => Media is { Verdict: not MediaVerdict.Ok };

    /// <summary>Lower is better: 0 are the strategies with no visible problems.</summary>
    public int QualityClass
    {
        get
        {
            if (!string.IsNullOrEmpty(Error)) return 4;
            if (OkCount == 0) return 3;
            if (Errors > 0) return 2;
            if (DpiBlocked || UdpFailed || MediaFailed) return 1;
            return 0;
        }
    }

    public string Verdict
    {
        get
        {
            if (!string.IsNullOrEmpty(Error)) return "ошибка";
            if (AttemptCount == 0) return "нет данных";
            if (OkCount == 0) return "не работает";

            var problems = new List<string>();
            if (Errors > 0) problems.Add($"отказов {Errors}");
            if (DpiBlocked) problems.Add("блок 16-20 КБ");
            if (UdpFailed) problems.Add("UDP");
            if (MediaFailed) problems.Add("видео");

            return problems.Count == 0 ? "работает" : string.Join(", ", problems);
        }
    }

    public string OkRatio => AttemptCount == 0 ? "0/0" : $"{OkCount}/{AttemptCount}";

    /// <summary>
    /// Removes targets that failed for every strategy: they say nothing about the
    /// strategy, they are simply unreachable on this network.
    /// </summary>
    public void DropTargets(IReadOnlySet<string> names)
    {
        if (names.Count == 0) return;

        Outcomes.RemoveAll(o => names.Contains(o.Name));
        AvgLatencyMs = Outcomes.Where(o => o.LatencyMs > 0).Select(o => o.LatencyMs).DefaultIfEmpty(0).Average();
        Score = StrategyTester.ComputeScore(this);
    }
}

/// <summary>
/// Starts each candidate strategy in turn and measures real connectivity.
/// This replaces service.bat -> Run Tests with an in-process run that reports
/// progress and returns structured results instead of console text.
/// </summary>
public sealed class StrategyTester
{
    private readonly ZapretPaths _paths;
    private readonly WinwsRunner _runner;
    private readonly GameFilter _gameFilter;

    public StrategyTester(ZapretPaths paths, WinwsRunner runner, GameFilter gameFilter)
    {
        _paths = paths;
        _runner = runner;
        _gameFilter = gameFilter;
    }

    public async Task<StrategyTestResult> RunAsync(
        Strategy strategy,
        IReadOnlyList<ProbeTarget> targets,
        IProgress<string>? log = null,
        bool runDpi = true,
        bool runUdp = true,
        bool runMedia = true,
        CancellationToken ct = default)
    {
        var result = new StrategyTestResult { Strategy = strategy };

        // One strategy can hang on a broken network: never let the test run forever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(150));
        var token = timeout.Token;

        try
        {
            log?.Report($"Запуск {strategy.Name}...");
            _runner.Stop();
            await _runner.StartAsync(strategy, _gameFilter, token).ConfigureAwait(false);

            // give WinDivert a moment to attach and the first packets to flow
            await Task.Delay(400, token).ConfigureAwait(false);

            using var prober = new Prober(Math.Max(2, 4));
            var gate = new SemaphoreSlim(6);
            var tasks = targets.Select(async target =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    return await prober.ProbeAsync(target, token).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
            result.Outcomes.AddRange(outcomes);
            result.AvgLatencyMs = outcomes.Where(o => o.LatencyMs > 0).Select(o => o.LatencyMs).DefaultIfEmpty(0).Average();

            if (runDpi && !token.IsCancellationRequested)
            {
                foreach (var host in new[] { "discord.com", "www.youtube.com" })
                {
                    token.ThrowIfCancellationRequested();
                    var dpi = await DpiTester.TestAsync(host, 5000, ct: token).ConfigureAwait(false);
                    result.Dpi.Add(dpi);
                    log?.Report($"{strategy.Name}: {dpi.Text}");
                }
            }

            if (runUdp && !token.IsCancellationRequested)
            {
                result.Udp = await StunTester.TestAsync(ct: token).ConfigureAwait(false);
                log?.Report($"{strategy.Name}: {result.Udp.Text}");
            }

            if (runMedia && !token.IsCancellationRequested)
            {
                result.Media = await MediaTester.TestAsync(ct: token).ConfigureAwait(false);
                log?.Report($"{strategy.Name}: {result.Media.Text}");
            }

            result.Score = ComputeScore(result);
            log?.Report($"{strategy.Name}: {result.OkRatio} проверок, ошибок {result.Errors}, отклик {result.AvgLatencyMs:0} мс, оценка {result.Score:0}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Error = "таймаут проверки (150 секунд)";
            log?.Report($"{strategy.Name}: таймаут проверки.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            log?.Report($"{strategy.Name}: {ex.Message}");
        }
        finally
        {
            _runner.Stop();
            await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }

    public static double ComputeScore(StrategyTestResult result)
    {
        if (!string.IsNullOrEmpty(result.Error)) return -1000;

        // A failed check costs as much as a passed one: correctness dominates.
        var score = result.OkCount * 100.0 - result.Errors * 100.0;
        score -= result.FailedTargets * 60.0;

        foreach (var dpi in result.Dpi)
        {
            score += dpi.Verdict switch
            {
                DpiVerdict.Ok => 120,
                DpiVerdict.LikelyBlocked => -250,
                DpiVerdict.Failed => -150,
                _ => 0
            };
        }

        if (result.Udp is not null)
            score += result.Udp.Ok ? 80 : -80;

        if (result.Media is not null)
        {
            score += result.Media.Verdict switch
            {
                MediaVerdict.Ok => 100,
                MediaVerdict.LikelyBlocked => -150,
                MediaVerdict.Partial => -40,
                _ => -100
            };
        }

        // Latency is a tie-breaker only: at most 40 points, less than one check.
        if (result.AvgLatencyMs > 0)
            score += Math.Max(0, 40 - result.AvgLatencyMs / 30.0);

        return Math.Round(score, 1);
    }

    /// <summary>
    /// The best score possible for the selected targets and checks. Used for the
    /// "Качество" percentage: one number that is comparable inside a single run.
    /// </summary>
    public static double MaxScore(IReadOnlyList<ProbeTarget> targets, bool runDpi, bool runUdp = true, bool runMedia = true)
    {
        var attempts = targets.Sum(t =>
            (string.IsNullOrEmpty(t.Url) ? 0 : 3) +
            (string.IsNullOrEmpty(t.PingHost) && string.IsNullOrEmpty(t.Url) ? 0 : 1));

        return MaxScoreForAttempts(attempts, runDpi, runUdp, runMedia);
    }

    public static double MaxScoreForAttempts(int attempts, bool runDpi, bool runUdp, bool runMedia) =>
        attempts * 100.0 + (runDpi ? 240 : 0) + (runUdp ? 80 : 0) + (runMedia ? 100 : 0) + 40;

    /// <summary>Sequential run over all candidates, best class and score first.</summary>
    public async Task<List<StrategyTestResult>> RunAllAsync(
        IReadOnlyList<Strategy> strategies,
        IReadOnlyList<ProbeTarget> targets,
        IProgress<string>? log = null,
        bool runDpi = true,
        bool runUdp = true,
        bool runMedia = true,
        CancellationToken ct = default)
    {
        var results = new List<StrategyTestResult>();
        for (var i = 0; i < strategies.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            log?.Report($"=== [{i + 1}/{strategies.Count}] {strategies[i].Name} ===");
            var result = await RunAsync(strategies[i], targets, log, runDpi, runUdp, runMedia, ct).ConfigureAwait(false);
            results.Add(result);
        }

        return results
            .OrderBy(r => r.QualityClass)
            .ThenByDescending(r => r.Score)
            .ToList();
    }
}
