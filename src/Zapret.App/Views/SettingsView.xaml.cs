using System.Windows;
using System.Windows.Controls;
using Zapret.Core;

namespace Zapret.App.Views;

public partial class SettingsView : UserControl, IRefreshableView
{
    private const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ZapretControl";
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshView();
    }

    public void RefreshView()
    {
        _loading = true;
        RootBox.Text = AppState.Paths?.Root ?? "не найдена";
        AutoStartCheck.IsChecked = AppState.Settings.AutoStartStrategy;
        TrayCheck.IsChecked = AppState.Settings.MinimizeToTray;
        UpdateCheckBox.IsChecked = AppState.Settings.CheckUpdatesOnStart;
        AutoInstallCheck.IsChecked = AppState.Settings.AutoInstallUpdates;
        _ = RefreshRunAtLogonAsync();

        foreach (var item in TimeoutCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag as string == AppState.Settings.ProbeTimeoutSeconds.ToString())
            {
                TimeoutCombo.SelectedItem = item;
                break;
            }
        }
        if (TimeoutCombo.SelectedItem is null) TimeoutCombo.SelectedIndex = 1;

        var enabled = AppState.Settings.Targets.Count(t => t.Enabled);
        TargetsText.Text = $"Целей в списке: {AppState.Settings.Targets.Count}, включено: {enabled}.";
        AboutText.Text = $"Zapret Control {AppInfo.Version}\nUpstream: github.com/{AppInfo.UpstreamRepo}\nwinws.exe: {AppState.Paths?.WinwsExe ?? "-"}";
        _loading = false;
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        (Application.Current.MainWindow as MainWindow)?.AskForFolder();
        RefreshView();
    }

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        var found = ZapretPaths.Discover(AppState.Settings.ZapretRoot);
        if (found is null)
        {
            MainWindow.ShowError("Не удалось найти папку со сборкой автоматически. Выберите её вручную.");
            return;
        }

        if (AppState.TrySetRoot(found.Root, out var message))
        {
            (Application.Current.MainWindow as MainWindow)?.ShowStatus(message);
            RefreshView();
        }
        else
        {
            MainWindow.ShowError(message);
        }
    }

    private async Task RefreshRunAtLogonAsync()
    {
        RunAtLogonCheck.IsChecked = await IsRunAtLogonEnabledAsync();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        AppState.Settings.AutoStartStrategy = AutoStartCheck.IsChecked == true;
        AppState.Settings.MinimizeToTray = TrayCheck.IsChecked == true;
        AppState.Settings.CheckUpdatesOnStart = UpdateCheckBox.IsChecked == true;
        AppState.Settings.AutoInstallUpdates = AutoInstallCheck.IsChecked == true;
        AppState.Settings.Save();

        _ = SetRunAtLogonAsync(RunAtLogonCheck.IsChecked == true);
    }

    private void TimeoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (TimeoutCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var seconds))
        {
            AppState.Settings.ProbeTimeoutSeconds = seconds;
            AppState.Settings.Save();
        }
    }

    private void LoadTargets_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Paths is null) return;

        var targets = AppSettings.LoadTargetsFromFile(AppState.Paths);
        if (targets.Count == 0)
        {
            MainWindow.ShowError("Файл utils\\targets.txt не найден или пуст.");
            return;
        }

        AppState.Settings.Targets = targets;
        AppState.Settings.Save();
        (Application.Current.MainWindow as MainWindow)?.ShowStatus($"Загружено целей: {targets.Count}.");
        RefreshView();
    }

    private void ResetTargets_Click(object sender, RoutedEventArgs e)
    {
        AppState.Settings.Targets = AppSettings.DefaultTargets();
        AppState.Settings.Save();
        (Application.Current.MainWindow as MainWindow)?.ShowStatus("Возвращён стандартный набор целей.");
        RefreshView();
    }

    private void OpenAppLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetDirectoryName(AppLog.FilePath)!;
            Directory.CreateDirectory(folder);
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

    private static async Task<bool> IsRunAtLogonEnabledAsync()
    {
        var result = await WindowsProcess.RunAsync("reg.exe", new[] { "query", RunKey, "/v", RunValue }, 10000);
        return result.Ok;
    }

    private static async Task SetRunAtLogonAsync(bool enabled)
    {
        var exe = Environment.ProcessPath ?? "";
        if (enabled)
        {
            await WindowsProcess.RunAsync("reg.exe", new[]
            {
                "add", RunKey, "/v", RunValue, "/t", "REG_SZ", "/d", $"\"{exe}\" --minimized", "/f"
            }, 10000);
        }
        else
        {
            await WindowsProcess.RunAsync("reg.exe", new[] { "delete", RunKey, "/v", RunValue, "/f" }, 10000);
        }
    }
}
