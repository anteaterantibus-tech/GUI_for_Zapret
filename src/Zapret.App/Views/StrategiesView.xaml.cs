using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Zapret.Core;

namespace Zapret.App.Views;

public partial class StrategiesView : UserControl, IRefreshableView
{
    private bool _subscribed;
    private readonly HashSet<string> _marked = new(StringComparer.OrdinalIgnoreCase);

    public StrategiesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
        Subscribe();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        AppState.StrategiesReloaded += OnStateChanged;
        AppState.StatusChanged += OnStatusChanged;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        AppState.StrategiesReloaded -= OnStateChanged;
        AppState.StatusChanged -= OnStatusChanged;
    }

    private void OnStateChanged() => Dispatcher.BeginInvoke(RefreshView);

    private void OnStatusChanged() => Dispatcher.BeginInvoke(RefreshActions);

    public void RefreshView()
    {
        if (!AppState.Ready)
        {
            SubtitleText.Text = "Сборка zapret не найдена. Укажите папку со сборкой в настройках.";
            StrategyList.ItemsSource = null;
            return;
        }

        SubtitleText.Text = $"Найдено стратегий: {AppState.Strategies.Count}. Команды запуска читаются из .bat файлов сборки zapret {AppState.InstalledVersion}.";

        var selectedName = AppState.Selected?.Name;
        StrategyList.ItemsSource = AppState.Strategies;
        StrategyList.SelectedItem = AppState.Strategies.FirstOrDefault(s => s.Name == selectedName)
                                    ?? AppState.Strategies.FirstOrDefault();

        ShowDetails(StrategyList.SelectedItem as Strategy);
        RefreshActions();
    }

    private void RefreshActions()
    {
        var running = AppState.IsRunning;
        var serviceActive = AppState.ServiceStateValue is ServiceState.Running or ServiceState.Stopped;
        var foreignService = serviceActive && !AppState.ServiceBelongsToRoot;
        var serviceRunning = AppState.ServiceStateValue == ServiceState.Running;

        StartButton.Content = serviceRunning ? "Работает службой" : running ? "Перезапустить" : "Запустить";

        ServiceButton.Content = foreignService
            ? "Служба другой папки"
            : AppState.ServiceStateValue switch
            {
                ServiceState.Running => "Убрать службу",
                ServiceState.Stopped => "Убрать службу",
                ServiceState.Pending => "Служба занята",
                _ => "В автозапуск"
            };
        ServiceButton.IsEnabled = !foreignService && AppState.ServiceStateValue is not ServiceState.Pending;

        StartButton.IsEnabled = !serviceRunning;
        StartButton.ToolTip = foreignService
            ? "Служба zapret запущена из другой папки. Остановите её в той установке."
            : serviceRunning
                ? "Стратегия уже запущена как служба. Нажмите «Убрать службу», чтобы запускать вручную."
                : "Запустить выбранную стратегию";
    }

    private void ShowDetails(Strategy? strategy)
    {
        if (strategy is null)
        {
            SelectedName.Text = "-";
            SelectedFile.Text = "";
            SelectedNote.Text = "";
            ChipsPanel.Children.Clear();
            ProfilesPanel.Children.Clear();
            return;
        }

        SelectedName.Text = strategy.Name;
        SelectedFile.Text = strategy.FileName;
        SelectedNote.Text = strategy.Note;

        ChipsPanel.Children.Clear();
        AddChip("TCP: " + ExpandPorts(strategy.WfTcp), "TextBrush");
        AddChip("UDP: " + ExpandPorts(strategy.WfUdp), "TextBrush");
        AddChip("Методы: " + string.Join("+", strategy.Methods), "AccentBrush");
        AddChip($"Блоков: {strategy.Profiles.Count}", "MutedBrush");

        ProfilesPanel.Children.Clear();
        for (var i = 0; i < strategy.Profiles.Count; i++)
        {
            var profile = strategy.Profiles[i];
            ProfilesPanel.Children.Add(BuildProfileCard(i + 1, profile));
        }
    }

    private static string ExpandPorts(string ports) =>
        ports.Replace("%GameFilterTCP%", AppState.GameFilter.EffectiveTcpPorts, StringComparison.OrdinalIgnoreCase)
             .Replace("%GameFilterUDP%", AppState.GameFilter.EffectiveUdpPorts, StringComparison.OrdinalIgnoreCase);

    private void AddChip(string text, string brushKey)
    {
        var chip = new Border
        {
            Style = (Style)FindResource("Chip"),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                Foreground = (Brush)FindResource(brushKey),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 420
            }
        };
        ChipsPanel.Children.Add(chip);
    }

    private Border BuildProfileCard(int number, WinwsProfile profile)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"#{number}   {profile.Ports}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5
        });
        stack.Children.Add(new TextBlock
        {
            Text = profile.Scope,
            FontSize = 11.5,
            Foreground = (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"desync={profile.Desync}   repeats={profile.Repeats}   split-pos={profile.SplitPos}   seqovl={profile.SeqOvl}",
            Style = (Style)FindResource("Mono"),
            Margin = new Thickness(0, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"fooling={profile.Fooling}   cutoff={profile.Cutoff}   fakes={profile.Fakes}",
            Style = (Style)FindResource("Mono"),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1D, 0x28)),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack
        };
    }

    private void StrategyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StrategyList.SelectedItem is Strategy strategy)
        {
            AppState.Select(strategy);
            ShowDetails(strategy);
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!AppState.Ready)
        {
            MainWindow.ShowError("Сборка zapret не найдена. Укажите папку в настройках.");
            return;
        }

        var strategy = StrategyList.SelectedItem as Strategy ?? AppState.Selected;
        if (strategy is null) return;

        try
        {
            StartButton.IsEnabled = false;
            (Application.Current.MainWindow as MainWindow)?.ShowStatus("Запуск: " + strategy.Name);
            await AppState.StartAsync(strategy);
            (Application.Current.MainWindow as MainWindow)?.ShowStatus("Запущено: " + strategy.Name);
        }
        catch (Exception ex)
        {
            MainWindow.ShowError(ex.Message);
        }
        finally
        {
            StartButton.IsEnabled = true;
            RefreshActions();
        }
    }

    private async void Service_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Service is null || AppState.Selected is null)
        {
            MainWindow.ShowError("Сначала выберите стратегию и укажите папку сборки.");
            return;
        }

        try
        {
            ServiceButton.IsEnabled = false;

            if (AppState.ServiceStateValue is ServiceState.Running or ServiceState.Stopped)
            {
                await AppState.Service.UninstallAsync();
                MainWindow.ShowInfo("Служба zapret удалена. Автозапуск отключён.");
            }
            else
            {
                AppState.Stop();
                AppState.GameFilter.Save(AppState.Paths!);
                var ok = await AppState.Service.InstallAsync(AppState.Selected, AppState.GameFilter);
                if (!ok)
                {
                    MainWindow.ShowError("Не удалось установить службу. Проверьте права администратора и журнал.");
                }
            }
        }
        catch (Exception ex)
        {
            MainWindow.ShowError(ex.Message);
        }
        finally
        {
            await AppState.RefreshServiceStateAsync();
            ServiceButton.IsEnabled = true;
            RefreshActions();
        }
    }

    private void OpenBat_Click(object sender, RoutedEventArgs e)
    {
        if (StrategyList.SelectedItem is not Strategy strategy) return;
        try
        {
            Process.Start(new ProcessStartInfo(strategy.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainWindow.ShowError(ex.Message);
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Paths is null) return;
        AppState.GameFilter = GameFilter.Load(AppState.Paths);
        AppState.ReloadStrategies();
        RefreshView();
    }

    private void TransferCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.Tag is not Strategy strategy) return;
        if (box.IsChecked == true) _marked.Add(strategy.FilePath);
        else _marked.Remove(strategy.FilePath);
    }

    /// <summary>Checked strategies, or the selected one when nothing is checked.</summary>
    private List<Strategy> MarkedStrategies()
    {
        var list = AppState.Strategies.Where(s => _marked.Contains(s.FilePath)).ToList();
        if (list.Count == 0 && StrategyList.SelectedItem is Strategy selected) list.Add(selected);
        return list;
    }

    private void ExportFolder_Click(object sender, RoutedEventArgs e)
    {
        var strategies = MarkedStrategies();
        if (strategies.Count == 0)
        {
            MainWindow.ShowError("Отметьте галочками хотя бы одну стратегию.");
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Куда сохранить стратегии" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var path = StrategyTransfer.ExportToFolder(strategies, dialog.FolderName, DateTime.Now);
            MainWindow.ShowInfo($"Экспортировано стратегий: {strategies.Count}.\n\n{path}");
        }
        catch (Exception ex)
        {
            MainWindow.ShowError("Не удалось экспортировать: " + ex.Message);
        }
    }

    private void ExportZip_Click(object sender, RoutedEventArgs e)
    {
        var strategies = MarkedStrategies();
        if (strategies.Count == 0)
        {
            MainWindow.ShowError("Отметьте галочками хотя бы одну стратегию.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Сохранить стратегии в архив",
            Filter = "Архив (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"strategies-{DateTime.Now:yyyyMMdd-HHmm}.zip"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            StrategyTransfer.ExportToZip(strategies, dialog.FileName);
            MainWindow.ShowInfo($"Экспортировано стратегий: {strategies.Count}.\n\n{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MainWindow.ShowError("Не удалось экспортировать: " + ex.Message);
        }
    }

    private void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт стратегий",
            Filter = "Стратегии и архивы (*.bat;*.zip)|*.bat;*.zip|Стратегии (*.bat)|*.bat|Архивы (*.zip)|*.zip",
            Multiselect = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        ImportSources(dialog.FileNames);
    }

    private void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка со стратегиями" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(dialog.FolderName, "*.bat", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex)
        {
            MainWindow.ShowError(ex.Message);
            return;
        }

        if (files.Count == 0)
        {
            MainWindow.ShowInfo("В выбранной папке нет .bat файлов.");
            return;
        }

        ImportSources(files);
    }

    /// <summary>
    /// Copies strategy files into the zapret folder. Existing files are replaced only
    /// after a confirmation, the replaced originals are kept in strategies.backup-*.
    /// </summary>
    private void ImportSources(IReadOnlyCollection<string> sources)
    {
        if (AppState.Paths is null)
        {
            MainWindow.ShowError("Сборка zapret не найдена.");
            return;
        }

        IReadOnlyList<StrategyFile> files;
        int invalid;
        try
        {
            files = StrategyTransfer.Read(sources, out invalid);
        }
        catch (Exception ex)
        {
            MainWindow.ShowError("Не удалось прочитать файлы: " + ex.Message);
            return;
        }

        if (files.Count == 0)
        {
            MainWindow.ShowError("В выбранных файлах нет стратегий: нужен .bat с командой winws.");
            return;
        }

        var owner = Window.GetWindow(this);
        var root = AppState.Paths.Root;
        var added = 0;
        var replaced = 0;
        var skipped = 0;
        var cancelled = false;
        string? backupDir = null;

        MessageBoxResult Ask(string text) => owner is null
            ? MessageBox.Show(text, "Импорт стратегий", MessageBoxButton.YesNoCancel, MessageBoxImage.Question)
            : MessageBox.Show(owner, text, "Импорт стратегий", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        try
        {
            foreach (var file in files)
            {
                var target = Path.Combine(root, file.Name);
                if (!File.Exists(target))
                {
                    File.WriteAllBytes(target, file.Bytes);
                    added++;
                    continue;
                }

                var existing = File.ReadAllBytes(target);
                if (existing.AsSpan().SequenceEqual(file.Bytes))
                {
                    skipped++;
                    continue;
                }

                var answer = Ask($"Стратегия {file.Name} уже есть и отличается.\n\n" +
                                 "Да - заменить, Нет - пропустить, Отмена - прервать импорт.");
                if (answer == MessageBoxResult.Cancel)
                {
                    cancelled = true;
                    break;
                }

                if (answer == MessageBoxResult.No)
                {
                    skipped++;
                    continue;
                }

                backupDir ??= Path.Combine(root, "strategies.backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(backupDir);
                File.Copy(target, Path.Combine(backupDir, file.Name), true);
                File.WriteAllBytes(target, file.Bytes);
                replaced++;
            }
        }
        catch (Exception ex)
        {
            MainWindow.ShowError("Импорт прерван: " + ex.Message);
            return;
        }

        if (added + replaced > 0)
        {
            AppState.ReloadStrategies();
            RefreshView();
        }

        var summary = $"добавлено: {added}, заменено: {replaced}, пропущено: {skipped}";
        if (invalid > 0) summary += $", не стратегий: {invalid}";
        if (cancelled) summary += ". Импорт прерван";
        if (backupDir is not null) summary += $"\n\nРезервная копия:\n{backupDir}";
        if (replaced > 0 && AppState.ServiceStateValue == ServiceState.Running)
        {
            summary += "\n\nСлужба zapret уже запущена. Если заменена стратегия из автозапуска, " +
                       "нажмите «В автозапуск» заново.";
        }

        MainWindow.ShowInfo("Импорт стратегий: " + summary);
        (Application.Current.MainWindow as MainWindow)?.ShowStatus(
            $"Импорт стратегий: добавлено {added}, заменено {replaced}.");
    }
}
