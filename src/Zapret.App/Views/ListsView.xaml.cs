using System.Windows;
using System.Windows.Controls;
using Zapret.Core;

namespace Zapret.App.Views;

public partial class ListsView : UserControl, IRefreshableView
{
    private bool _loading;

    public ListsView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshView();
    }

    public void RefreshView()
    {
        if (!AppState.Ready || AppState.Lists is null || AppState.Paths is null) return;

        _loading = true;
        if (MyGeneralUserRadio.IsChecked != true && MyExcludeUserRadio.IsChecked != true &&
            MyIpSetExcludeUserRadio.IsChecked != true && BuildGeneralRadio.IsChecked != true &&
            BuildGoogleRadio.IsChecked != true && BuildExcludeRadio.IsChecked != true &&
            BuildIpSetExcludeRadio.IsChecked != true && BuildIpSetAllRadio.IsChecked != true &&
            BuildIpSetBackupRadio.IsChecked != true)
        {
            MyGeneralUserRadio.IsChecked = true;
        }
        _loading = false;

        LoadEditor();
        LoadIpSet();
        LoadHosts();
        LoadYouTube();
        LoadDiscord();
        LoadGameFilter();
    }

    private void LoadDiscord()
    {
        if (AppState.Paths is null) return;

        var applied = DiscordExclusion.IsApplied(AppState.Paths);
        DiscordStatusText.Text = applied ? "текст напрямую" : "под обходом";
        DiscordStatusText.ToolTip = applied
            ? "Домены Discord исключены из обхода. Голосовой UDP-профиль остаётся."
            : "Домены Discord сейчас обходятся.";
        DiscordExcludeButton.IsEnabled = !applied;
        DiscordIncludeButton.IsEnabled = applied;
    }

    private async void DiscordExclude_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Paths is null) return;

        var confirm = MessageBox.Show(
            "Выключить обход доменов Discord (текст и сайт)?\n\n" +
            "Списки Discord будут исключены, резервные копии создаются автоматически.\n" +
            "Голосовой UDP-профиль останется: он работает по портам, а не по доменам.",
            "Discord и обход", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var result = DiscordExclusion.Apply(AppState.Paths);
        if (!result.Success)
        {
            MainWindow.ShowError(result.Message);
            return;
        }

        DiscordExcludeButton.IsEnabled = false;
        DiscordIncludeButton.IsEnabled = false;
        var restart = await AppState.RestartBypassAsync();
        MainWindow.ShowInfo(result.Message + "\n\n" + restart);
        LoadDiscord();
        LoadEditor();
        LoadYouTube();
    }

    private async void DiscordInclude_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Paths is null) return;

        var confirm = MessageBox.Show(
            "Вернуть обход доменов Discord?\n\n" +
            "list-general.txt и list-google.txt будут восстановлены из резервных копий, исключения удалены.",
            "Discord и обход", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var result = DiscordExclusion.Revert(AppState.Paths);
        if (!result.Success)
        {
            MainWindow.ShowError(result.Message);
            return;
        }

        DiscordExcludeButton.IsEnabled = false;
        DiscordIncludeButton.IsEnabled = false;
        var restart = await AppState.RestartBypassAsync();
        MainWindow.ShowInfo(result.Message + "\n\n" + restart);
        LoadDiscord();
        LoadEditor();
        LoadYouTube();
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Файлы списков",
            Multiselect = true,
            Filter = "Списки (*.txt)|*.txt|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await ImportListsAsync(dialog.FileNames);
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Папка со списками"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var files = Directory.EnumerateFiles(dialog.FolderName, "*.txt", SearchOption.TopDirectoryOnly).ToList();
        await ImportListsAsync(files);
    }

    private async Task ImportListsAsync(IEnumerable<string> files)
    {
        if (AppState.Paths is null) return;

        var result = ListImporter.Import(AppState.Paths, files);
        if (!result.Success)
        {
            MainWindow.ShowError(result.Message);
            return;
        }

        ImportFilesButton.IsEnabled = false;
        ImportFolderButton.IsEnabled = false;
        try
        {
            var restart = await AppState.RestartBypassAsync();
            MainWindow.ShowInfo(result.Message + "\n\n" + restart);
            LoadEditor();
            LoadIpSet();
            LoadYouTube();
            LoadDiscord();
        }
        finally
        {
            ImportFilesButton.IsEnabled = true;
            ImportFolderButton.IsEnabled = true;
        }
    }

    private void LoadGameFilter()
    {
        if (AppState.Paths is null) return;

        var filter = GameFilter.Load(AppState.Paths);
        AppState.GameFilter = filter;

        foreach (var item in GameFilterModeBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, GameFilter.ModeName(filter.Mode), StringComparison.Ordinal))
            {
                GameFilterModeBox.SelectedItem = item;
                break;
            }
        }

        TcpPortsBox.Text = filter.TcpPorts;
        UdpPortsBox.Text = filter.UdpPorts;
        var enabled = filter.Mode != GameFilterMode.Disabled;
        GameFilterStatusText.Text = enabled ? "включён" : "выключен";
        GameFilterStatusText.ToolTip = filter.Status;

        GameFilterToggleButton.Content = enabled ? "Выключить GameFilter" : "Включить GameFilter";
        GameFilterToggleButton.Style = (Style)FindResource(enabled ? "DangerButton" : "PrimaryButton");
        GameFilterHint.Text = enabled ? "перезапуск автоматически" : "порт 12 = выключен";
        GameFilterHint.ToolTip = enabled
            ? "GameFilter включён. Если игры не используют порты выше 1023, его лучше выключить."
            : "GameFilter выключен. Порт-заглушка 12 не влияет на трафик.";
    }

    private async void GameFilterToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = AppState.GameFilter.Mode != GameFilterMode.Disabled;
        var filter = new GameFilter
        {
            Mode = enabled ? GameFilterMode.Disabled : GameFilterMode.All,
            TcpPorts = string.IsNullOrWhiteSpace(TcpPortsBox.Text) ? "1024-65535" : TcpPortsBox.Text.Trim(),
            UdpPorts = string.IsNullOrWhiteSpace(UdpPortsBox.Text) ? "1024-65535" : UdpPortsBox.Text.Trim()
        };

        if (!ValidateGameFilter(filter)) return;
        await ApplyGameFilterAsync(filter, enabled ? "GameFilter выключен." : "GameFilter включён (TCP и UDP).");
    }

    private async void ApplyGameFilter_Click(object sender, RoutedEventArgs e)
    {
        var tag = (GameFilterModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "disabled";
        var filter = new GameFilter
        {
            Mode = tag switch
            {
                "all" => GameFilterMode.All,
                "tcp" => GameFilterMode.Tcp,
                "udp" => GameFilterMode.Udp,
                _ => GameFilterMode.Disabled
            },
            TcpPorts = string.IsNullOrWhiteSpace(TcpPortsBox.Text) ? "1024-65535" : TcpPortsBox.Text.Trim(),
            UdpPorts = string.IsNullOrWhiteSpace(UdpPortsBox.Text) ? "1024-65535" : UdpPortsBox.Text.Trim()
        };

        if (!ValidateGameFilter(filter)) return;
        await ApplyGameFilterAsync(filter, "GameFilter: " + filter.Status);
    }

    private static bool ValidateGameFilter(GameFilter filter)
    {
        if (filter.Mode is GameFilterMode.All or GameFilterMode.Tcp && !GameFilter.IsValidRange(filter.TcpPorts))
        {
            MainWindow.ShowError("Некорректный диапазон TCP портов. Пример: 1024-65535 или 1024-1934,1936-65535");
            return false;
        }

        if (filter.Mode is GameFilterMode.All or GameFilterMode.Udp && !GameFilter.IsValidRange(filter.UdpPorts))
        {
            MainWindow.ShowError("Некорректный диапазон UDP портов. Пример: 1024-65535");
            return false;
        }

        return true;
    }

    private async Task ApplyGameFilterAsync(GameFilter filter, string message)
    {
        if (AppState.Paths is null) return;

        filter.Save(AppState.Paths);
        AppState.GameFilter = filter;
        GameFilterToggleButton.IsEnabled = false;
        try
        {
            var restart = await AppState.RestartBypassAsync();
            LoadGameFilter();
            (Application.Current.MainWindow as MainWindow)?.ShowStatus(message + " " + restart);
        }
        finally
        {
            GameFilterToggleButton.IsEnabled = true;
        }
    }

    private void LoadYouTube()
    {
        if (AppState.Paths is null) return;

        var applied = YouTubeExclusion.IsApplied(AppState.Paths);
        YouTubeStatusText.Text = applied ? "напрямую" : "под обходом";
        YouTubeStatusText.ToolTip = applied
            ? "YouTube исключён из обхода: видео идёт напрямую, Discord обходится."
            : "YouTube сейчас под обходом.";
        YouTubeExcludeButton.IsEnabled = !applied;
        YouTubeIncludeButton.IsEnabled = applied;
    }

    private async void YouTubeExclude_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Paths is null) return;

        var confirm = MessageBox.Show(
            "Исключить YouTube из обхода?\n\n" +
            "YouTube будет открываться напрямую, без фейков и разрезания пакетов. Discord останется под обходом.\n" +
            "Будет изменён list-google.txt, резервная копия создаётся автоматически.",
            "YouTube и обход", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var result = YouTubeExclusion.Apply(AppState.Paths);
        if (!result.Success)
        {
            MainWindow.ShowError(result.Message);
            return;
        }

        YoutubeButtonsState(false);
        var restart = await AppState.RestartBypassAsync();
        (Application.Current.MainWindow as MainWindow)?.ShowStatus(result.Message + " " + restart);
        MainWindow.ShowInfo(result.Message + "\n\n" + restart);
        LoadEditor();
        LoadYouTube();
    }

    private async void YouTubeInclude_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Paths is null) return;

        var confirm = MessageBox.Show(
            "Вернуть YouTube под обход?\n\n" +
            "list-google.txt будет восстановлен из резервной копии, добавленные исключения удалены.",
            "YouTube и обход", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var result = YouTubeExclusion.Revert(AppState.Paths);
        if (!result.Success)
        {
            MainWindow.ShowError(result.Message);
            return;
        }

        YoutubeButtonsState(false);
        var restart = await AppState.RestartBypassAsync();
        (Application.Current.MainWindow as MainWindow)?.ShowStatus(result.Message + " " + restart);
        MainWindow.ShowInfo(result.Message + "\n\n" + restart);
        LoadEditor();
        LoadYouTube();
    }

    private void YoutubeButtonsState(bool enabled)
    {
        YouTubeExcludeButton.IsEnabled = enabled;
        YouTubeIncludeButton.IsEnabled = enabled;
    }

    private string? CurrentKey
    {
        get
        {
            if (MyExcludeUserRadio.IsChecked == true) return "exclude-user";
            if (MyIpSetExcludeUserRadio.IsChecked == true) return "ipset-exclude-user";
            if (BuildGeneralRadio.IsChecked == true) return "general";
            if (BuildGoogleRadio.IsChecked == true) return "google";
            if (BuildExcludeRadio.IsChecked == true) return "exclude";
            if (BuildIpSetExcludeRadio.IsChecked == true) return "ipset-exclude";
            if (BuildIpSetAllRadio.IsChecked == true) return "ipset-all";
            if (BuildIpSetBackupRadio.IsChecked == true) return "ipset-all-backup";
            return "general-user";
        }
    }

    private string CurrentPath()
    {
        var paths = AppState.Paths!;
        return CurrentKey switch
        {
            "exclude-user" => paths.ListExcludeUser,
            "ipset-exclude-user" => paths.IpSetExcludeUser,
            "general" => paths.ListGeneral,
            "google" => paths.ListGoogle,
            "exclude" => paths.ListExclude,
            "ipset-exclude" => paths.IpSetExclude,
            "ipset-all" => paths.IpSetAll,
            "ipset-all-backup" => paths.IpSetBackup,
            _ => paths.ListGeneralUser
        };
    }

    private void LoadEditor()
    {
        var path = CurrentPath();
        var key = CurrentKey;
        var readOnly = key is "ipset-all" or "ipset-all-backup";
        var exists = File.Exists(path);

        EditorBox.Text = exists ? File.ReadAllText(path) : "";
        EditorBox.IsReadOnly = readOnly;
        SaveButton.IsEnabled = !readOnly && exists;
        DomainBox.IsEnabled = !readOnly;

        var note = key switch
        {
            "ipset-all" => "Служебный файл режима IPSet: none, any или loaded. Режим меняется кнопками IPSet.",
            "ipset-all-backup" => "Полный список подсетей из репозитория. Только просмотр, обновляется кнопкой «Обновить IPSet».",
            "general-user" or "exclude-user" or "ipset-exclude-user" =>
                "Ваш файл: обновление сборки его не трогает.",
            _ => "Базовый файл сборки: правки сохраняются, при обновлении объединяются с новыми."
        };

        EditorHint.Text = $"{(exists ? path : "файл не найден")}\n{note}";
    }

    private void LoadIpSet()
    {
        var mode = IpSetManager.GetMode(AppState.Paths!);
        _loading = true;
        IpNoneRadio.IsChecked = mode == IpSetMode.None;
        IpAnyRadio.IsChecked = mode == IpSetMode.Any;
        IpLoadedRadio.IsChecked = mode == IpSetMode.Loaded;
        _loading = false;

        var size = File.Exists(AppState.Paths!.IpSetAll) ? new FileInfo(AppState.Paths.IpSetAll).Length / 1024 : 0;
        var backup = File.Exists(AppState.Paths.IpSetBackup) ? new FileInfo(AppState.Paths.IpSetBackup).Length / 1024 : 0;
        IpStatusText.Text = mode switch
        {
            IpSetMode.Any => "any",
            IpSetMode.Loaded => "loaded",
            _ => "none"
        };
        IpStatusText.ToolTip = $"{IpSetManager.Describe(mode)}. Файл: {size} КБ, резервная копия: {backup} КБ.";
    }

    private void LoadHosts()
    {
        var hasBlock = UpdateService.HostsHasBlock();
        HostsStatusText.Text = hasBlock ? "записи есть" : "записей нет";
        HostsStatusText.ToolTip = hasBlock
            ? "Записи zapret-control присутствуют в hosts."
            : "Записей zapret-control в hosts нет.";
    }

    private void FileSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        LoadEditor();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Lists is null || CurrentKey is "ipset-all" or "ipset-all-backup") return;

        try
        {
            AppState.Lists.Write(CurrentPath(), EditorBox.Text);
            var restart = await AppState.RestartBypassAsync();
            (Application.Current.MainWindow as MainWindow)?.ShowStatus("Список сохранён. " + restart);
        }
        catch (Exception ex)
        {
            MainWindow.ShowError(ex.Message);
        }
    }

    private void AddDomain_Click(object sender, RoutedEventArgs e)
    {
        var domain = DomainBox.Text.Trim();
        if (domain.Length == 0) return;

        var lines = EditorBox.Text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        if (lines.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            DomainBox.Clear();
            return;
        }

        EditorBox.Text = string.Join("\r\n", lines.Where(l => l.Length > 0).Append(domain)) + "\r\n";
        DomainBox.Clear();
    }

    private void ApplyIpSet_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Paths is null) return;

        var mode = IpLoadedRadio.IsChecked == true ? IpSetMode.Loaded
            : IpAnyRadio.IsChecked == true ? IpSetMode.Any
            : IpSetMode.None;

        if (IpSetManager.SetMode(AppState.Paths, mode, out var message))
        {
            LoadIpSet();
            (Application.Current.MainWindow as MainWindow)?.ShowStatus(message + " Перезапустите стратегию.");
        }
        else
        {
            MainWindow.ShowError(message);
        }
    }

    private async void UpdateIpSet_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Paths is null) return;

        UpdateIpSetButton.IsEnabled = false;
        try
        {
            var ok = await AppState.Updater.UpdateIpSetAsync(AppState.Paths);
            (Application.Current.MainWindow as MainWindow)?.ShowStatus(ok
                ? "IPSet обновлён из репозитория."
                : "Не удалось получить список. Проверьте подключение.");
            LoadIpSet();
        }
        catch (Exception ex)
        {
            MainWindow.ShowError(ex.Message);
        }
        finally
        {
            UpdateIpSetButton.IsEnabled = true;
        }
    }

    private async void HostsUpdate_Click(object sender, RoutedEventArgs e)
    {
        HostsUpdateButton.IsEnabled = false;
        try
        {
            var content = await AppState.Updater.DownloadHostsAsync();
            if (content is null)
            {
                MainWindow.ShowError("Не удалось скачать файл hosts из репозитория.");
                return;
            }

            var result = UpdateService.ApplyHosts(content);
            if (result.Success)
            {
                (Application.Current.MainWindow as MainWindow)?.ShowStatus($"hosts обновлён: {result.Entries} записей.");
            }
            else
            {
                MainWindow.ShowError("Не удалось обновить hosts: " + result.Message);
            }
            LoadHosts();
        }
        finally
        {
            HostsUpdateButton.IsEnabled = true;
        }
    }

    private void HostsRemove_Click(object sender, RoutedEventArgs e)
    {
        var ok = UpdateService.RemoveHostsBlock(out var message);
        if (ok)
            (Application.Current.MainWindow as MainWindow)?.ShowStatus(message);
        else
            MainWindow.ShowError(message);
        LoadHosts();
    }
}
