using System.Windows.Threading;
using Zapret.Core;

namespace Zapret.App;

/// <summary>
/// Light application log. Used for update, diagnostics and UI stall reports,
/// so a freeze can be investigated from %APPDATA%\ZapretControl\logs\app.log.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.LogsDir, "app.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : message + ": " + exception);

    private static void Write(string level, string message)
    {
        try
        {
            AppPaths.Ensure();
            lock (Gate)
            {
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 2 * 1024 * 1024)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break the app
        }
    }
}

/// <summary>
/// Watches the UI thread from a background thread. If the dispatcher stops
/// answering, the stall and the current stage are written to the log.
/// </summary>
public static class UiHeartbeat
{
    private static Dispatcher? _dispatcher;
    private static volatile string _stage = "запуск";

    public static void Start(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "ui-heartbeat"
        };
        thread.Start();
        AppLog.Info("Приложение запущено");
    }

    public static void SetStage(string stage)
    {
        _stage = stage;
        AppLog.Info("Этап: " + stage);
    }

    private static void Loop()
    {
        while (true)
        {
            Thread.Sleep(1000);
            var sent = DateTime.UtcNow;
            try
            {
                _dispatcher?.BeginInvoke(new Action(() =>
                {
                    var lagMs = (DateTime.UtcNow - sent).TotalMilliseconds;
                    if (lagMs > 2000)
                    {
                        AppLog.Warn($"Интерфейс не отвечал {lagMs:0} мс, этап: {_stage}");
                    }
                }));
            }
            catch
            {
                // dispatcher is shutting down
            }
        }
    }
}
