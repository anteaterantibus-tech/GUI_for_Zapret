using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Zapret.Core;

namespace Zapret.App.Views;

public sealed class TestRow
{
    public required Strategy Strategy { get; init; }
    public string Name => Strategy.Name;
    public string Score { get; init; } = "";
    public string Quality { get; init; } = "";
    public string Checks { get; init; } = "";
    public string Errors { get; init; } = "";
    public string Latency { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string Detail { get; init; } = "";
}

public partial class TestView : UserControl, IRefreshableView
{
    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<TestRow> _progressRows = new();
    private List<TestRow> _rows = new();
    private List<CheckBox> _targetBoxes = new();
    private bool _busy;
    private bool _loading;

    public TestView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshView();
    }

    public void RefreshView()
    {
        if (!AppState.Ready)
        {
            StageText.Text = "Сборка zapret не найдена.";
            return;
        }

        if (_targetBoxes.Count != AppState.Settings.Targets.Count)
        {
            BuildTargets();
        }

        if (!_busy)
        {
            ApplyBestButton.IsEnabled = _rows.Count > 0;
            _loading = true;
            DpiCheck.IsChecked = AppState.Settings.RunDpiTest;
            UdpCheck.IsChecked = AppState.Settings.RunUdpTest;
            MediaCheck.IsChecked = AppState.Settings.RunMediaTest;
            _loading = false;
        }
    }

    private void BuildTargets()
    {
        TargetsPanel.Children.Clear();
        _targetBoxes.Clear();

        foreach (var target in AppState.Settings.Targets)
        {
            var box = new CheckBox
            {
                Content = target.Name,
                IsChecked = target.Enabled,
                Tag = target,
                Margin = new Thickness(0, 4, 0, 4)
            };
            box.Checked += (_, _) => SaveTargets();
            box.Unchecked += (_, _) => SaveTargets();
            TargetsPanel.Children.Add(box);
            _targetBoxes.Add(box);
        }
    }

    private void SaveTargets()
    {
        foreach (var box in _targetBoxes)
        {
            if (box.Tag is TargetSetting target) target.Enabled = box.IsChecked == true;
        }
        AppState.Settings.Save();
    }

    private void DpiCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _busy) return;

        AppState.Settings.RunDpiTest = DpiCheck.IsChecked == true;
        AppState.Settings.RunUdpTest = UdpCheck.IsChecked == true;
        AppState.Settings.RunMediaTest = MediaCheck.IsChecked == true;
        AppState.Settings.Save();
    }

    private IReadOnlyList<ProbeTarget> SelectedTargets() => AppState.ActiveTargets();

    private async void FindBest_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRun(out var targets)) return;

        var candidates = AllStrategiesCheck.IsChecked == true
            ? AppState.Strategies.OrderBy(s => s.Name.Length).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : AppState.Strategies.Where(s => s.Name == AppState.Selected?.Name).ToList();

        await RunAsync(candidates, targets);
    }

    private async void TestCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRun(out var targets)) return;
        var current = AppState.Selected;
        if (current is null)
        {
            MainWindow.ShowError("Стратегия не выбрана.");
            return;
        }
        await RunAsync(new List<Strategy> { current }, targets);
    }

    private bool CanRun(out IReadOnlyList<ProbeTarget> targets)
    {
        targets = Array.Empty<ProbeTarget>();

        if (!AppState.Ready)
        {
            MainWindow.ShowError("Сборка zapret не найдена.");
            return false;
        }

        targets = SelectedTargets();
        if (targets.Count == 0)
        {
            MainWindow.ShowError("Отметьте хотя бы один сервис для проверки.");
            return false;
        }

        if (_busy) return false;
        if (AppState.Busy)
        {
            MainWindow.ShowError("Дождитесь завершения текущей операции (тест или обновление).");
            return false;
        }
        return true;
    }

    private async Task RunAsync(IReadOnlyList<Strategy> candidates, IReadOnlyList<ProbeTarget> targets)
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        _busy = true;
        AppState.Busy = true;
        AppState.CancelCurrentOperation = () => cts.Cancel();
        UiHeartbeat.SetStage($"подбор стратегии ({candidates.Count})");
        FindBestButton.IsEnabled = false;
        TestCurrentButton.IsEnabled = false;
        ApplyBestButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.IsIndeterminate = false;
        Progress.Minimum = 0;
        Progress.Maximum = Math.Max(1, candidates.Count);
        Progress.Value = 0;
        LogBox.Clear();
        _rows.Clear();
        _progressRows.Clear();
        ResultsGrid.ItemsSource = _progressRows;

        var log = new Progress<string>(line =>
        {
            StageText.Text = line;
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        var runDpi = DpiCheck.IsChecked == true;
        var runUdp = UdpCheck.IsChecked == true;
        var runMedia = MediaCheck.IsChecked == true;
        var maxScore = StrategyTester.MaxScore(targets, runDpi, runUdp, runMedia);
        var excludedNote = "";

        try
        {
            var tester = new StrategyTester(AppState.Paths!, AppState.Runner!, AppState.GameFilter);
            var results = new List<StrategyTestResult>();

            for (var i = 0; i < candidates.Count; i++)
            {
                if (cts.IsCancellationRequested) break;
                var strategy = candidates[i];
                StageText.Text = $"[{i + 1}/{candidates.Count}] {strategy.Name}: проверка...";
                AppLog.Info($"Тест {i + 1}/{candidates.Count}: {strategy.Name}");

                StrategyTestResult result;
                try
                {
                    var task = Task.Run(() => tester.RunAsync(strategy, targets, log, runDpi, runUdp, runMedia, cts.Token), cts.Token);
                    var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(45), cts.Token));

                    if (finished == task)
                    {
                        result = await task;
                    }
                    else
                    {
                        AppLog.Warn($"Тест {strategy.Name}: нет ответа 45 секунд, стратегия пропущена");
                        result = new StrategyTestResult { Strategy = strategy, Error = "нет ответа 45 секунд" };
                        _ = AppState.StopAsync();
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    break;
                }

                results.Add(result);
                _progressRows.Add(ToRow(result, maxScore));
                Progress.Value = i + 1;
                StageText.Text = $"{strategy.Name}: {result.OkRatio} проверок, отклик {result.AvgLatencyMs:0} мс, оценка {result.Score:0} · {i + 1} из {candidates.Count}";
            }

            // Targets that failed for every strategy are unreachable on this network
            // rather than a strategy problem: drop them from the score.
            var deadTargets = results
                .SelectMany(r => r.Outcomes)
                .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.All(o => o.OkCount == 0))
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (deadTargets.Count > 0 && results.Count > 0)
            {
                foreach (var result in results) result.DropTargets(deadTargets);
                maxScore = StrategyTester.MaxScoreForAttempts(results.Max(r => r.AttemptCount), runDpi, runUdp, runMedia);
                excludedNote = $"Недоступны у всех стратегий и исключены из оценки: {string.Join(", ", deadTargets)}. ";
            }

            var sorted = results
                .OrderBy(r => r.QualityClass)
                .ThenByDescending(r => r.Score)
                .ToList();

            var best = sorted.FirstOrDefault();
            var ties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (best is not null)
            {
                foreach (var candidate in sorted
                             .Where(r => r.QualityClass == best.QualityClass && best.Score - r.Score <= 50)
                             .Take(3))
                {
                    ties.Add(candidate.Strategy.Name);
                }
            }

            _rows = sorted.Select(r => ToRow(r, maxScore, ties.Contains(r.Strategy.Name) && ties.Count > 1)).ToList();
            ResultsGrid.ItemsSource = _rows;

            if (best is not null)
            {
                ApplyBestButton.IsEnabled = true;
                ResultsGrid.SelectedItem = _rows.FirstOrDefault(r => r.Strategy.Name == best.Strategy.Name);
                var tieText = ties.Count > 1
                    ? ties.Count >= sorted.Count
                        ? "Все стратегии показали одинаковый результат на доступных сервисах. Проверьте 2-3 вручную."
                        : $"Равноценные кандидаты: {string.Join(", ", ties)}. Проверьте их вручную."
                    : $"Лучшая стратегия: {best.Strategy.Name} (качество {Quality(best, maxScore)}, проверки {best.OkRatio}, ошибок {best.Errors}).";
                StageText.Text = excludedNote + tieText;
            }
            else
            {
                StageText.Text = "Не удалось подобрать стратегию. Смотрите журнал.";
            }
        }
        catch (OperationCanceledException)
        {
            StageText.Text = "Проверка остановлена.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Ошибка подбора стратегии", ex);
            StageText.Text = "Ошибка: " + ex.Message;
        }
        finally
        {
            _busy = false;
            AppState.Busy = false;
            AppState.CancelCurrentOperation = null;
            FindBestButton.IsEnabled = true;
            TestCurrentButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            Progress.IsIndeterminate = false;
            _cts.Dispose();
            _cts = null;

            // The test must never leave a tested strategy running or "selected".
            _ = AppState.StopAsync();
            AppState.RaiseStatus();
            UiHeartbeat.SetStage("готово");
        }
    }

    private static string Quality(StrategyTestResult result, double maxScore) =>
        maxScore > 0 ? $"{Math.Clamp(result.Score / maxScore * 100, 0, 100):0}%" : "-";

    private static TestRow ToRow(StrategyTestResult result, double maxScore, bool tie = false)
    {
        var details = new List<string>();
        details.Add($"{result.OkRatio}, {result.AvgLatencyMs:0} мс");
        if (!string.IsNullOrEmpty(result.Error)) details.Add(result.Error);
        foreach (var dpi in result.Dpi)
            details.Add($"{dpi.Host}: {dpi.Verdict}");
        if (result.Udp is not null) details.Add(result.Udp.Text);
        if (result.Media is not null) details.Add(result.Media.Text);

        var failed = result.Outcomes.Where(o => o.Status != ProbeStatus.Ok)
            .Select(o => o.Name + " (" + o.Tokens + ")");
        details.AddRange(failed);

        return new TestRow
        {
            Strategy = result.Strategy,
            Score = result.Score.ToString("0"),
            Quality = Quality(result, maxScore),
            Checks = result.OkRatio,
            Errors = result.Errors.ToString(),
            Latency = result.AvgLatencyMs > 0 ? result.AvgLatencyMs.ToString("0") + " мс" : "-",
            Verdict = tie ? "равноценно: " + result.Verdict : result.Verdict,
            Detail = string.Join("; ", details)
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var cts = _cts;
        cts?.Cancel();
        _ = AppState.StopAsync();
    }

    private async void ApplyBest_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is TestRow row)
        {
            await StartStrategy(row.Strategy);
            return;
        }

        if (_rows.FirstOrDefault() is { } best)
        {
            await StartStrategy(best.Strategy);
        }
    }

    private async Task StartStrategy(Strategy strategy)
    {
        try
        {
            AppState.Select(strategy);
            await AppState.StartAsync(strategy);
            StageText.Text = "Запущено: " + strategy.Name;
            (Application.Current.MainWindow as MainWindow)?.ShowStatus("Запущено: " + strategy.Name);
        }
        catch (Exception ex)
        {
            MainWindow.ShowError(ex.Message);
        }
    }

    private async void ResultsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is TestRow row)
        {
            await StartStrategy(row.Strategy);
        }
    }
}
