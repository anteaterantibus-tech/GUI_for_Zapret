using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Zapret.Core;

namespace Zapret.App.Views;

public partial class GuideView : UserControl, IRefreshableView
{
    public GuideView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshView();
    }

    public void RefreshView()
    {
        var root = AppState.Paths?.Root ?? "папка сборки не выбрана";
        PathsText.Text =
            $"Сборка: {root}\r\n" +
            $"Настройки: {AppPaths.DataDir}\r\n" +
            $"Журналы winws: {AppPaths.LogsDir}\r\n" +
            $"Журнал приложения: {AppLog.FilePath}\r\n" +
            $"Резервные копии: рядом с папкой сборки, в zapret-backups";
    }

    private static void Go(string page) => (Application.Current.MainWindow as MainWindow)?.NavigateTo(page);

    private void NavigateTest_Click(object sender, RoutedEventArgs e) => Go("test");
    private void NavigateStrategies_Click(object sender, RoutedEventArgs e) => Go("strategies");
    private void NavigateUpdate_Click(object sender, RoutedEventArgs e) => Go("update");
    private void NavigateDiagnostics_Click(object sender, RoutedEventArgs e) => Go("diagnostics");
    private void NavigateSettings_Click(object sender, RoutedEventArgs e) => Go("settings");

    private void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Руководство.pdf"),
            Path.Combine(AppContext.BaseDirectory, "docs", "Руководство.pdf"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "Руководство.pdf"))
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            MainWindow.ShowError("Файл «Руководство.pdf» не найден рядом с приложением. Он лежит в папке docs репозитория.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainWindow.ShowError(ex.Message);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = AppState.Paths?.Root;
        if (path is null)
        {
            MainWindow.ShowError("Папка сборки не выбрана.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainWindow.ShowError(ex.Message);
        }
    }
}
