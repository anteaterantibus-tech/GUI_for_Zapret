using System.Windows;
using System.Windows.Threading;
using Zapret.Core;

namespace Zapret.App;

public partial class LogWindow : Window
{
    private readonly DispatcherTimer _timer;

    public LogWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        var runner = AppState.Runner;
        if (runner is null) return;

        var lines = runner.RecentLog;
        if (LogBox.LineCount > 0 && lines.Count > 0 && LogBox.Text.EndsWith(lines[^1], StringComparison.Ordinal))
        {
            LogPathText.Text = runner.LogFile;
            return;
        }

        LogBox.Text = string.Join(Environment.NewLine, lines);
        LogBox.ScrollToEnd();
        LogPathText.Text = runner.LogFile;
        HintText.Text = runner.IsRunning
            ? $"winws работает, PID {runner.ProcessId}, стратегия {runner.CurrentStrategy}."
            : "winws не запущен.";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        AppState.Runner?.ClearLog();
        LogBox.Clear();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = AppState.Runner?.LogFile is { Length: > 0 } file
            ? Path.GetDirectoryName(file)!
            : AppPaths.LogsDir;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MainWindow.ShowError(ex.Message);
        }
    }
}
