using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Zapret.Core;

namespace Zapret.App;

public partial class App : Application
{
    private const string MutexName = @"Local\ZapretControl.SingleInstance";
    private const string ShowEventName = @"Local\ZapretControl.ShowWindow";

    private Mutex? _instanceMutex;
    public static EventWaitHandle? ShowEvent { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppLog.Info($"Старт: args=[{string.Join(" ", e.Args)}], elevated={WindowsProcess.IsElevated}");
        var allowMultiple = e.Args.Contains("--allow-multiple", StringComparer.OrdinalIgnoreCase);
        var createdNew = true;

        if (!allowMultiple)
        {
            _instanceMutex = new Mutex(true, MutexName, out createdNew);
            AppLog.Info("Мьютекс: " + (createdNew ? "свободен" : "занят"));
        }

        if (!createdNew)
        {
            // Another copy is running: ask it to show its window instead of just refusing to start.
            try
            {
                EventWaitHandle.OpenExisting(ShowEventName).Set();
            }
            catch
            {
                MessageBox.Show("Zapret Control уже запущен: окно можно открыть из значка в трее.",
                    "Zapret Control", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        if (!allowMultiple)
        {
            ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }

        // The manifest already requires elevation; this is a safety net for launchers
        // that strip the manifest or start the process from a non-elevated host.
        if (!e.Args.Contains("--no-elevate", StringComparer.OrdinalIgnoreCase) &&
            !WindowsProcess.IsElevated && TryElevate())
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            AppLog.Error("Необработанная ошибка", ex);
            WriteCrashLog(ex);
            MessageBox.Show(ex?.ToString() ?? "Неизвестная ошибка", "Zapret Control", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }

    private static bool TryElevate()
    {
        AppLog.Info("Попытка перезапуска с правами администратора");
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1)) psi.ArgumentList.Add(arg);

            Process.Start(psi);
            return true;
        }
        catch
        {
            // user declined the UAC prompt: continue without admin rights
            return false;
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Необработанная ошибка UI", e.Exception);
        WriteCrashLog(e.Exception);
        MessageBox.Show(e.Exception.ToString(), "Zapret Control - ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info($"Выход, код {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private static void WriteCrashLog(Exception? exception)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "zapret-control-crash.txt");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // nothing else we can do
        }
    }
}
