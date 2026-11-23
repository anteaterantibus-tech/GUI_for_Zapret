using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Zapret.Core;

namespace Zapret.App.Views;

public partial class DiagnosticsView : UserControl, IRefreshableView
{
    private bool _ran;
    private CancellationTokenSource? _cts;

    public DiagnosticsView()
    {
        InitializeComponent();
    }

    public void RefreshView()
    {
        if (_ran) return;
        _ran = true;
        _ = RunAsync();
    }

    private void Run_Click(object sender, RoutedEventArgs e) => _ = RunAsync();

    private async Task RunAsync()
    {
        if (!AppState.Ready)
        {
            ChecksPanel.Children.Clear();
            ChecksPanel.Children.Add(Message("Сборка zapret не найдена. Укажите папку в настройках."));
            return;
        }

        if (_cts is not null) return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        UiHeartbeat.SetStage("диагностика");
        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ChecksPanel.Children.Clear();
        ChecksPanel.Children.Add(Message("Проверка..."));
        SummaryText.Text = "Выполняется проверка системы...";

        try
        {
            // Diagnostics does blocking process calls and DNS lookups: keep them off the UI thread.
            var checks = await Task.Run(() => new Diagnostics(AppState.Paths!).RunAsync(cts.Token), cts.Token);

            Render(checks);

            var errors = checks.Count(c => c.Status is DiagStatus.Error);
            var warnings = checks.Count(c => c.Status is DiagStatus.Warning);
            SummaryText.Text = errors == 0 && warnings == 0
                ? $"Проверено {checks.Count} пунктов: проблем не найдено."
                : $"Проверено {checks.Count} пунктов: ошибок {errors}, предупреждений {warnings}.";
        }
        catch (OperationCanceledException)
        {
            ChecksPanel.Children.Clear();
            ChecksPanel.Children.Add(Message("Проверка отменена."));
            SummaryText.Text = "Проверка отменена.";
        }
        catch (Exception ex)
        {
            ChecksPanel.Children.Clear();
            ChecksPanel.Children.Add(Message("Ошибка диагностики: " + ex.Message));
            SummaryText.Text = "Ошибка диагностики.";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            RunButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            UiHeartbeat.SetStage("готово");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Render(List<DiagCheck> checks)
    {
        ChecksPanel.Children.Clear();

        foreach (var group in checks.GroupBy(c => c.Group))
        {
            ChecksPanel.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("MutedBrush"),
                Margin = new Thickness(2, 10, 0, 8)
            });

            foreach (var check in group)
            {
                ChecksPanel.Children.Add(BuildCard(check));
            }
        }
    }

    private Border BuildCard(DiagCheck check)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(5),
            Background = StatusBrush(check.Status),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 10, 0)
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = check.Title, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(check.Detail))
        {
            text.Children.Add(new TextBlock
            {
                Text = check.Detail,
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (check.HasFix)
        {
            var button = new Button
            {
                Content = check.FixLabel ?? "Исправить",
                Style = (Style)FindResource("SmallButton"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                button.Content = "Работаю...";
                try
                {
                    var message = await check.Fix!(CancellationToken.None);
                    (Application.Current.MainWindow as MainWindow)?.ShowStatus(message);
                    await RunAsync();
                }
                catch (Exception ex)
                {
                    MainWindow.ShowError(ex.Message);
                    button.IsEnabled = true;
                    button.Content = check.FixLabel ?? "Исправить";
                }
            };
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
    }

    private Brush StatusBrush(DiagStatus status) => (Brush)FindResource(status switch
    {
        DiagStatus.Ok => "AccentBrush",
        DiagStatus.Warning => "WarnBrush",
        DiagStatus.Error => "DangerBrush",
        _ => "InfoBrush"
    });

    private static TextBlock Message(string text) => new()
    {
        Text = text,
        Foreground = (Brush)Application.Current.Resources["MutedBrush"],
        Margin = new Thickness(2, 6, 0, 0),
        TextWrapping = TextWrapping.Wrap
    };
}
