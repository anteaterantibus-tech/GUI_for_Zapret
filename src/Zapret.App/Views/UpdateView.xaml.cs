using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Zapret.Core;

namespace Zapret.App.Views;

public partial class UpdateView : UserControl, IRefreshableView
{
    private ReleaseInfo? _latest;
    private bool _checking;
    private bool _updating;
    private bool _checkedOnce;
    private CancellationTokenSource? _checkCts;

    public UpdateView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshView();
    }

    public void RefreshView()
    {
        if (AppState.Paths is null)
        {
            InstalledVersionText.Text = "не установлена";
            InstalledPathText.Text = "при установке будет предложено выбрать папку";
        }
        else
        {
            InstalledVersionText.Text = AppState.InstalledVersion is { } version ? "zapret " + version : "версия неизвестна";
            InstalledPathText.Text = AppState.Paths.Root;
        }

        PreserveFakesCheck.IsChecked = AppState.Settings.PreserveActiveFakesOnUpdate;
        PreserveIpSetCheck.IsChecked = AppState.Settings.PreserveIpSetOnUpdate;
        ListModeCombo.SelectedIndex = AppState.Settings.ListUpdateMode switch
        {
            ListMergeMode.Replace => 1,
            ListMergeMode.KeepLocal => 2,
            _ => 0
        };

        if (!_checkedOnce && !_checking && !_updating) _ = CheckAsync(silent: true);
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync(silent: false);

    private async Task CheckAsync(bool silent)
    {
        if (_checking || _updating) return;
        _checking = true;
        UiHeartbeat.SetStage("проверка обновлений");
        CheckButton.IsEnabled = false;
        CancelCheckButton.IsEnabled = true;
        _checkCts = new CancellationTokenSource();
        var ct = _checkCts.Token;
        StageText.Text = "Проверка последнего релиза... (до 15 секунд)";

        try
        {
            var latest = await AppState.Updater.GetLatestReleaseAsync(ct);
            if (latest is null)
            {
                StageText.Text = "GitHub недоступен. Проверьте подключение к интернету или прокси.";
                return;
            }

            _latest = latest;
            _checkedOnce = true;
            LatestVersionText.Text = "zapret " + latest.Tag;
            LatestDateText.Text = latest.PublishedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
            OpenReleaseButton.IsEnabled = !string.IsNullOrEmpty(latest.HtmlUrl);

            var installed = AppState.InstalledVersion;
            var isNew = installed is null || UpdateService.CompareVersions(latest.Tag, installed) > 0;

            UpdateButton.IsEnabled = isNew && !_updating;
            SkipButton.IsEnabled = isNew;
            UpdateButton.Content = isNew ? "Скачать и установить" : "Установлена последняя версия";

            var releases = await AppState.Updater.GetReleasesSinceAsync(installed, 8, ct);
            var upToDate = releases.Count == 0;
            if (upToDate) releases = await AppState.Updater.GetRecentReleasesAsync(3, ct);

            ChangelogText.Text = (upToDate && releases.Count > 0
                    ? "Новых версий нет. Ниже последние релизы для справки.\n\n"
                    : "")
                + BuildChangelog(releases);

            StageText.Text = isNew
                ? $"Доступно обновление: {installed ?? "?"} -> {latest.Tag}"
                : $"Установлена последняя версия ({installed}).";

            AppState.Settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("O");
            AppState.Settings.Save();

            if (!silent && !isNew)
                MessageBox.Show(Window.GetWindow(this)!, "Установлена последняя версия zapret.",
                    "Zapret Control", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StageText.Text = "Проверка отменена.";
        }
        catch (Exception ex)
        {
            StageText.Text = "Ошибка проверки: " + ex.Message;
            if (!silent) MainWindow.ShowError(ex.Message);
        }
        finally
        {
            _checking = false;
            _checkCts.Dispose();
            _checkCts = null;
            CheckButton.IsEnabled = true;
            CancelCheckButton.IsEnabled = false;
            UiHeartbeat.SetStage("готово");
        }
    }

    private void CancelCheck_Click(object sender, RoutedEventArgs e) => _checkCts?.Cancel();

    private static string BuildChangelog(IReadOnlyList<ReleaseInfo> releases)
    {
        if (releases.Count == 0) return "Список изменений недоступен.";

        var builder = new StringBuilder();
        foreach (var release in releases)
        {
            builder.AppendLine($"=== {release.Tag}  ({release.PublishedAt.ToLocalTime():dd.MM.yyyy}) ===");
            builder.AppendLine(CleanMarkdown(release.Body));
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }

    private static string CleanMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(описание отсутствует)";

        text = text.Replace("\r\n", "\n");
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, "<[^>]+>", "");
        text = text.Replace("**", "").Replace("<ins>", "").Replace("</ins>", "");
        text = Regex.Replace(text, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^>\s?", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_latest is null) return;
        AppState.Settings.SkippedVersion = _latest.Tag;
        AppState.Settings.Save();
        UpdateButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        StageText.Text = $"Версия {_latest.Tag} пропущена. Напоминание появится со следующей версией.";
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_latest is null) return;

        if (AppState.Busy && !_updating)
        {
            MainWindow.ShowError("Дождитесь завершения текущей операции (тест или обновление).");
            return;
        }

        var paths = AppState.Paths;
        if (paths is null)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Куда установить zapret (папка будет создана при необходимости)"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            paths = new ZapretPaths(dialog.FolderName);
            try
            {
                Directory.CreateDirectory(paths.Root);
            }
            catch (Exception ex)
            {
                MainWindow.ShowError("Не удалось создать папку: " + ex.Message);
                return;
            }
        }

        AppState.Settings.PreserveActiveFakesOnUpdate = PreserveFakesCheck.IsChecked == true;
        AppState.Settings.PreserveIpSetOnUpdate = PreserveIpSetCheck.IsChecked == true;
        var listModeTag = (ListModeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        AppState.Settings.ListUpdateMode = listModeTag switch
        {
            "replace" => ListMergeMode.Replace,
            "keep" => ListMergeMode.KeepLocal,
            _ => ListMergeMode.Merge
        };
        AppState.Settings.Save();

        var installed = UpdateService.GetInstalledVersion(paths);
        var listModeText = AppState.Settings.ListUpdateMode switch
        {
            ListMergeMode.Replace => "Списки lists будут заменены файлами из релиза.",
            ListMergeMode.KeepLocal => "Списки lists останутся без изменений.",
            _ => "Списки lists будут объединены: ваши правки сохранятся, новые строки добавятся."
        };

        var confirm = MessageBox.Show(Window.GetWindow(this)!,
            $"Обновить сборку zapret {installed ?? "?"} -> {_latest.Tag}?\n\n" +
            listModeText + "\n" +
            "Будут заменены bin, utils и стратегии. Заменённые фейки и GameFilter сохранятся.\n" +
            "Текущая стратегия и служба автозапуска будут восстановлены после обновления.",
            "Обновление zapret", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _updating = true;
        AppState.Busy = true;
        UiHeartbeat.SetStage("установка обновления");
        CheckButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        UpdateProgress.Value = 0;
        ProgressText.Text = "";

        try
        {
            var downloadProgress = new Progress<DownloadProgress>(p =>
            {
                if (p.Percent is { } percent)
                {
                    UpdateProgress.IsIndeterminate = false;
                    UpdateProgress.Value = percent;
                    ProgressText.Text = $"{p.Received / 1024} / {p.Total!.Value / 1024} КБ";
                }
                else
                {
                    UpdateProgress.IsIndeterminate = true;
                    ProgressText.Text = $"{p.Received / 1024} КБ";
                }
            });

            var log = new Progress<string>(line => StageText.Text = line);
            var outcome = await UpdateCoordinator.InstallAsync(_latest, paths, UpdateCoordinator.BuildOptions(), log, downloadProgress);

            if (!outcome.Success)
            {
                UpdateProgress.Value = 0;
                MainWindow.ShowError("Обновление не установлено: " + outcome.Message);
                return;
            }

            UpdateProgress.Value = 100;
            ProgressText.Text = "SHA256: " + (outcome.Sha256 ?? "")[..Math.Min(16, (outcome.Sha256 ?? "").Length)] + "...";
            StageText.Text = $"Обновлено до {outcome.NewVersion}. Резервная копия: {outcome.BackupPath}";
            BackupText.Text = "Последняя копия: " + outcome.BackupPath;

            MessageBox.Show(Window.GetWindow(this)!,
                $"Сборка обновлена: {outcome.OldVersion ?? "?"} -> {outcome.NewVersion}.\n\n" +
                $"Резервная копия: {outcome.BackupPath}\n" +
                (outcome.ServiceRestored ? "Служба автозапуска восстановлена.\n" : "") +
                (outcome.StrategyRestarted ? "Стратегия запущена заново." : ""),
                "Zapret Control", MessageBoxButton.OK, MessageBoxImage.Information);

            _latest = null;
            _checkedOnce = false;
            await CheckAsync(silent: true);
        }
        catch (Exception ex)
        {
            StageText.Text = "Ошибка обновления: " + ex.Message;
            MainWindow.ShowError("Не удалось обновить сборку:\n" + ex.Message);
        }
        finally
        {
            _updating = false;
            AppState.Busy = false;
            UiHeartbeat.SetStage("готово");
            CheckButton.IsEnabled = true;
            UpdateButton.IsEnabled = _latest is not null;
            UpdateProgress.IsIndeterminate = false;
            AppState.RaiseStatus();
        }
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        var root = AppState.Paths?.Root;
        if (root is null) return;

        var parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)) ?? root;
        var backups = Path.Combine(parent, "zapret-backups");
        Directory.CreateDirectory(backups);

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{backups}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainWindow.ShowError(ex.Message);
        }
    }

    private void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_latest is null || string.IsNullOrEmpty(_latest.HtmlUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_latest.HtmlUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainWindow.ShowError(ex.Message);
        }
    }
}
