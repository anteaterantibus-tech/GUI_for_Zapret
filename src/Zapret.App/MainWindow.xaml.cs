using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Zapret.App.Views;
using Zapret.Core;
using Forms = System.Windows.Forms;

namespace Zapret.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _views = new();
    private Forms.NotifyIcon? _tray;
    private bool _exiting;
    private bool _trayHintShown;
    private string _current = "strategies";

    public MainWindow()
    {
        InitializeComponent();
        Title = AppInfo.DisplayName;

        AppState.Init();
        Icon = LoadImageSource();
        BuildTrayIcon();
        UiHeartbeat.Start(Dispatcher);

        AppState.StatusChanged += OnStatusChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        StartShowWindowListener();
    }

    /// <summary>Lets a second launch bring this window back from the tray instead of doing nothing.</summary>
    private void StartShowWindowListener()
    {
        var handle = App.ShowEvent;
        if (handle is null) return;

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!handle.WaitOne(TimeSpan.FromSeconds(5))) continue;
                    Dispatcher.BeginInvoke(new Action(RestoreWindow));
                }
                catch
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "show-window-listener"
        };
        thread.Start();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppLog.Info("OnLoaded: старт");
        VersionText.Text = $"{AppInfo.DisplayName} {AppInfo.Version}";

        if (AppState.Paths is null)
        {
            var choice = MessageBox.Show(
                "Папка со сборкой zapret не найдена.\n\n" +
                "Да - скачать последнюю сборку автоматически (около 60 МБ).\n" +
                "Нет - указать папку вручную.\n" +
                "Отмена - сделать позже.",
                "Zapret Control", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (choice == MessageBoxResult.Yes) await DownloadZapretAsync();
            else if (choice == MessageBoxResult.No) AskForFolder();
        }

        Navigate(AppState.Paths is null ? "settings" : "strategies");
        AppLog.Info("OnLoaded: навигация выполнена");
        RefreshStatus();

        if (AppState.Paths is not null && !AppState.Settings.HasSeenGuide && !Environment.GetCommandLineArgs().Contains("--minimized"))
        {
            Navigate("guide");
        }

        var pageArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
        if (pageArg is not null) Navigate(pageArg["--page=".Length..]);

        if (Environment.GetCommandLineArgs().Contains("--minimized") && AppState.Settings.MinimizeToTray)
        {
            Hide();
        }

        _ = AppState.RefreshServiceStateAsync();

        if (AppState.Settings.AutoStartStrategy && !AppState.IsRunning && AppState.Selected is not null)
        {
            try
            {
                await AppState.StartAsync();
                RefreshStatus();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        }

        if (AppState.Settings.CheckUpdatesOnStart)
        {
            _ = CheckUpdatesInBackgroundAsync();
        }
    }

    private async Task CheckUpdatesInBackgroundAsync()
    {
        try
        {
            var release = await AppState.Updater.GetLatestReleaseAsync();
            if (release is null) return;

            AppState.Settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("O");
            AppState.Settings.Save();

            var installed = AppState.InstalledVersion;
            if (installed is null) return;
            if (UpdateService.CompareVersions(release.Tag, installed) <= 0) return;
            if (string.Equals(AppState.Settings.SkippedVersion, release.Tag, StringComparison.OrdinalIgnoreCase)) return;

            var canAutoInstall = AppState.Settings.AutoInstallUpdates &&
                                 AppState.Paths is not null &&
                                 !AppState.Busy &&
                                 AppState.ServiceStateValue != ServiceState.Pending;

            if (canAutoInstall)
            {
                ShowStatus($"Найдена версия {release.Tag}: автоматическая установка...");
                var log = new Progress<string>(ShowStatus);
                var outcome = await UpdateCoordinator.InstallAsync(release, AppState.Paths!, UpdateCoordinator.BuildOptions(), log);

                if (outcome.Success)
                {
                    ShowStatus($"Сборка обновлена до {outcome.NewVersion}. Резервная копия: {outcome.BackupPath}");
                    _tray?.ShowBalloonTip(4000, "Zapret Control", $"Сборка zapret обновлена до {outcome.NewVersion}.", Forms.ToolTipIcon.Info);
                    _views.Clear();
                    Navigate(_current);
                    RefreshStatus();
                }
                else
                {
                    ShowStatus("Обновление не установлено: " + outcome.Message);
                    _tray?.ShowBalloonTip(4000, "Zapret Control", "Не удалось установить обновление: " + outcome.Message, Forms.ToolTipIcon.Warning);
                }
                return;
            }

            NavUpdate.Content = "Обновления  •";
            ShowStatus($"Доступна новая версия zapret {release.Tag} (установлена {installed}). Откройте «Обновления».");
        }
        catch
        {
            // offline is not an error worth interrupting the user
        }
    }

    // ---------- navigation ----------

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string key }) Navigate(key);
    }

    private void Navigate(string key)
    {
        _current = key;

        foreach (var toggle in NavPanel.Children.OfType<ToggleButton>())
            toggle.IsChecked = string.Equals(toggle.Tag as string, key, StringComparison.Ordinal);

        if (!_views.TryGetValue(key, out var view))
        {
            view = key switch
            {
                "test" => new TestView(),
                "diagnostics" => new DiagnosticsView(),
                "lists" => new ListsView(),
                "update" => new UpdateView(),
                "guide" => new GuideView(),
                "settings" => new SettingsView(),
                _ => new StrategiesView()
            };
            _views[key] = view;
        }

        ContentHost.Content = view;
        if (view is IRefreshableView refreshable) refreshable.RefreshView();

        if (key == "guide" && !AppState.Settings.HasSeenGuide)
        {
            AppState.Settings.HasSeenGuide = true;
            AppState.Settings.Save();
        }

        if (key != "update") NavUpdate.Content = "Обновления";
    }

    public void NavigateTo(string key) => Navigate(key);

    // ---------- status ----------

    private void OnStatusChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshStatus);
            return;
        }
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var manualRunning = AppState.IsRunning;
        var serviceRunning = AppState.ServiceStateValue == ServiceState.Running && AppState.ServiceBelongsToRoot;
        var foreignService = AppState.ServiceStateValue == ServiceState.Running && !AppState.ServiceBelongsToRoot;
        var active = manualRunning || serviceRunning || foreignService;

        StatusDot.Fill = (Brush)FindResource(active ? "AccentBrush" : "DangerBrush");
        StatusText.Text = active ? "Работает" : "Остановлено";

        var activeName = serviceRunning
            ? AppState.ServiceStrategyName ?? AppState.Selected?.Name
            : manualRunning
                ? AppState.Runner?.CurrentStrategy
                : AppState.Selected?.Name;

        StatusStrategy.Text = foreignService && !manualRunning
            ? "Стратегия: другая установка"
            : "Стратегия: " + (activeName ?? "не выбрана");
        if (AppState.Busy && manualRunning && activeName is not null)
            StatusStrategy.Text = "Тест: " + activeName;
        StatusStrategy.ToolTip = AppState.Selected?.Note;

        StatusExtra.Text = serviceRunning
            ? "Служба: работает"
            : foreignService
                ? "Служба: другая папка"
                : manualRunning
                    ? "Ручной запуск"
                    : AppState.ServiceStateValue switch
                    {
                        ServiceState.Stopped => "Служба остановлена",
                        ServiceState.Pending => "Служба перезапускается",
                        ServiceState.NotInstalled => "Служба не установлена",
                        _ => "Служба: неизвестное состояние"
                    };

        ToggleRunButton.Content = serviceRunning
            ? "Работает службой"
            : manualRunning
                ? "Остановить"
                : "Запустить";
        ToggleRunButton.IsEnabled = !serviceRunning && !foreignService;
        ToggleRunButton.ToolTip = serviceRunning
            ? "Обход запущен службой. Чтобы запускать вручную, нажмите «Убрать службу» в карточке стратегии."
            : foreignService
                ? "Служба zapret запущена из другой папки. Остановите её в той установке."
                : null;

        FooterService.Text = serviceRunning || foreignService ? "Автозапуск включён" : "";

        if (AppState.Paths is null)
        {
            FooterText.Text = "Сборка zapret не найдена: укажите папку в настройках";
        }
        else
        {
            var uptime = AppState.Runner?.StartedAt is { } started
                ? $"   |   работает {(DateTime.Now - started).ToString(@"hh\:mm\:ss")}"
                : "";
            FooterText.Text = $"zapret {AppState.InstalledVersion ?? "?"}   |   {AppState.Paths.Root}{uptime}";
        }

        if (!WindowsProcess.IsElevated) FooterService.Text = "Нет прав администратора";

        if (AppState.Runner is not null)
            Title = $"{AppInfo.DisplayName} - {(active ? activeName ?? "работает" : "остановлено")}";
    }

    public void ShowStatus(string message) => FooterText.Text = message;

    // ---------- actions ----------

    private async void ToggleRun_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Busy)
        {
            AppState.CancelCurrentOperation?.Invoke();
            ShowStatus("Операция отменяется...");
            return;
        }

        if (AppState.IsRunning)
        {
            UiHeartbeat.SetStage("остановка обхода");
            await AppState.StopAsync();
            ShowStatus("Обход остановлен.");
            RefreshStatus();
            return;
        }

        if (!EnsureReady()) return;

        try
        {
            ToggleRunButton.IsEnabled = false;
            ShowStatus("Запуск стратегии...");
            UiHeartbeat.SetStage("запуск стратегии");
            await AppState.StartAsync();
            ShowStatus("Обход запущен: " + AppState.Selected?.Name);
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось запустить стратегию", ex);
            ShowError(ex.Message);
            ShowStatus("Не удалось запустить стратегию.");
        }
        finally
        {
            ToggleRunButton.IsEnabled = true;
            UiHeartbeat.SetStage("готово");
            AppLog.Info("Обход: " + (AppState.Selected?.Name ?? "-"));
            RefreshStatus();
        }
    }

    private bool EnsureReady()
    {
        if (!AppState.Ready)
        {
            ShowError("Сборка zapret не найдена. Укажите папку в настройках.");
            Navigate("settings");
            return false;
        }
        return true;
    }

    public static void ShowError(string message) =>
        MessageBox.Show(message, "Zapret Control", MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void ShowInfo(string message) =>
        MessageBox.Show(message, "Zapret Control", MessageBoxButton.OK, MessageBoxImage.Information);

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var window = new LogWindow { Owner = this };
        window.Show();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = AppState.Paths?.Root ?? AppPaths.DataDir;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>
    /// First run without any zapret folder: download the latest upstream release into
    /// %LOCALAPPDATA%\ZapretControl\zapret, the same place the installer uses.
    /// </summary>
    private async Task DownloadZapretAsync()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZapretControl", "zapret");
        var paths = new ZapretPaths(folder);

        try
        {
            Directory.CreateDirectory(paths.Root);
            Navigate("settings");
            ShowStatus("Поиск последней сборки zapret...");

            var release = await AppState.Updater.GetLatestReleaseAsync();
            if (release is null)
            {
                ShowError("Не удалось получить последнюю сборку zapret. Проверьте интернет и попробуйте ещё раз.");
                return;
            }

            ShowStatus($"Скачивание zapret {release.Tag}...");
            var progress = new Progress<DownloadProgress>(p =>
                ShowStatus(p.Percent is { } percent
                    ? $"Скачивание zapret {release.Tag}: {percent}%"
                    : $"Скачивание zapret {release.Tag}: {p.Received / 1024} КБ"));
            var log = new Progress<string>(line => ShowStatus(line));

            var outcome = await UpdateCoordinator.InstallAsync(
                release, paths, UpdateCoordinator.BuildOptions(), log, progress);

            if (!outcome.Success)
            {
                ShowError("Сборка не установлена: " + outcome.Message);
                return;
            }

            ShowStatus($"Сборка zapret {outcome.NewVersion} готова.");
            Navigate("strategies");
            RefreshStatus();
            ShowInfo($"Сборка zapret {outcome.NewVersion} скачана:\n{paths.Root}\n\nТеперь можно подобрать рабочую стратегию.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Скачивание zapret", ex);
            ShowError("Не удалось скачать сборку: " + ex.Message);
        }
    }

    public void AskForFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Папка со сборкой zapret (где лежит bin\\winws.exe)",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true) return;

        if (AppState.TrySetRoot(dialog.FolderName, out var message))
        {
            ShowStatus(message);
            AppState.Settings.Save();
            _views.Clear();
            Navigate("strategies");
            RefreshStatus();
        }
        else
        {
            ShowError(message);
        }
    }

    // ---------- tray & window chrome ----------

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        AppLog.Info($"Closing: exiting={_exiting}, tray={AppState.Settings.MinimizeToTray}");
        if (!_exiting && AppState.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown && _tray is not null)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(2500, "Zapret Control", "Приложение свёрнуто в трей. Двойной щелчок откроет окно.", Forms.ToolTipIcon.Info);
            }
            return;
        }

        _tray?.Dispose();
        _tray = null;
        AppState.Runner?.Dispose();
    }

    public void ExitApplication()
    {
        _exiting = true;
        Close();
        Application.Current.Shutdown();
    }

    private void BuildTrayIcon()
    {
        var icon = LoadTrayIcon();
        _tray = new Forms.NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = AppInfo.DisplayName
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => RestoreWindow());
        menu.Items.Add("Запустить", null, async (_, _) =>
        {
            try
            {
                await AppState.StartAsync();
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
        });
        menu.Items.Add("Остановить", null, (_, _) =>
        {
            AppState.Stop();
            Dispatcher.Invoke(RefreshStatus);
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreWindow();
    }

    private void RestoreWindow()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))?.Stream;
            if (stream is not null)
            {
                using (stream)
                {
                    return new System.Drawing.Icon(stream, 32, 32);
                }
            }
        }
        catch
        {
            // fall through to the system icon
        }
        return System.Drawing.SystemIcons.Application;
    }

    private static ImageSource LoadImageSource()
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri("pack://application:,,,/app.ico");
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return Imaging.CreateBitmapSourceFromHIcon(
                System.Drawing.SystemIcons.Application.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void ApplyDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var value = 1;
            DwmSetWindowAttribute(handle, 20, ref value, sizeof(int));
        }
        catch
        {
            // older Windows builds keep the light title bar
        }
    }
}
