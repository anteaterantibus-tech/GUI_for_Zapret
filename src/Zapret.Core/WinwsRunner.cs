using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Zapret.Core;

/// <summary>Starts and supervises a single winws.exe process started by the GUI.</summary>
public sealed class WinwsRunner : IDisposable
{
    private readonly ZapretPaths _paths;
    private readonly string _logDir;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<string> _ring = new();
    private volatile Process? _process;
    private StreamWriter? _logWriter;

    public WinwsRunner(ZapretPaths paths, string logDir)
    {
        _paths = paths;
        _logDir = logDir;
    }

    public const int RingSize = 800;

    // Never lock here: the UI asks for the state on every status update, and a wedged
    // process must not be able to freeze the interface.
    public bool IsRunning
    {
        get
        {
            var process = _process;
            if (process is null) return false;
            try
            {
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            var process = _process;
            if (process is null) return null;
            try
            {
                return process.HasExited ? null : process.Id;
            }
            catch
            {
                return null;
            }
        }
    }

    public DateTime? StartedAt { get; private set; }
    public string CurrentStrategy { get; private set; } = "";
    public string LogFile { get; private set; } = "";

    public event Action<string>? LogLine;
    public event Action<int>? Exited;

    public IReadOnlyList<string> RecentLog => _ring.ToArray();

    public void ClearLog()
    {
        while (_ring.TryDequeue(out _)) { }
    }

    /// <summary>Kills leftover winws.exe processes started from this distribution.</summary>
    public int StopAllExternal() => WindowsProcess.KillAll("winws");

    public async Task StartAsync(Strategy strategy, GameFilter gameFilter, CancellationToken ct = default)
    {
        if (!_paths.IsValid)
            throw new InvalidOperationException("Не найден winws.exe. Проверьте папку zapret.");
        if (!WindowsProcess.IsElevated)
            throw new InvalidOperationException("Нужны права администратора.");

        // winws can only run once: a service instance would be killed by the manual start.
        var serviceState = await WindowsProcess.GetServiceStateAsync("zapret", ct).ConfigureAwait(false);
        if (serviceState is "RUNNING" or "START_PENDING" or "STOP_PENDING")
            throw new InvalidOperationException(
                "Стратегия уже запущена как служба Windows. Нажмите «Убрать службу», чтобы запускать вручную.");

        Stop();
        StopAllExternal();

        var args = WinwsCommandBuilder.BuildArgs(strategy, gameFilter, _paths);
        Directory.CreateDirectory(_logDir);
        var logFile = Path.Combine(_logDir, $"winws-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        LogFile = logFile;
        _logWriter = new StreamWriter(new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true
        };

        var psi = new ProcessStartInfo(_paths.WinwsExe)
        {
            WorkingDirectory = _paths.BinDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        AppendLog("=== " + strategy.Name + " ===");
        AppendLog("> winws.exe " + WinwsCommandBuilder.Join(args));

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) AppendLog(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) AppendLog(e.Data); };
        process.Exited += (_, _) =>
        {
            int code;
            try { code = process.ExitCode; } catch { code = -1; }
            AppendLog($"=== winws завершился, код {code} ===");
            lock (_gate)
            {
                if (ReferenceEquals(_process, process))
                {
                    StartedAt = null;
                }
            }
            Exited?.Invoke(code);
        };

        lock (_gate)
        {
            _process = process;
            StartedAt = DateTime.Now;
            CurrentStrategy = strategy.Name;
        }

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка запуска: " + ex.Message);
            throw;
        }

        await WaitReadyAsync(process, ct).ConfigureAwait(false);
    }

    private static async Task WaitReadyAsync(Process process, CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(150, ct).ConfigureAwait(false);
            if (process.HasExited)
                throw new InvalidOperationException($"winws завершился сразу после запуска (код {process.ExitCode}). Включите журнал и проверьте аргументы.");
        }
    }

    /// <summary>
    /// Stops the process without ever blocking the caller: a stuck winws can keep a
    /// kernel handle for a long time, so killing is fire-and-forget.
    /// </summary>
    public void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        lock (_gate)
        {
            StartedAt = null;
        }

        if (process is not null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(true);
                }
                catch
                {
                    // already gone or access denied
                }

                try
                {
                    process.WaitForExit(1500);
                }
                catch
                {
                    // ignore
                }

                try
                {
                    process.Dispose();
                }
                catch
                {
                    // ignore
                }
            });
        }

        var writer = _logWriter;
        _logWriter = null;
        try
        {
            writer?.Dispose();
        }
        catch
        {
            // ignore
        }
    }

    private void AppendLog(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        _ring.Enqueue(stamped);
        while (_ring.Count > RingSize) _ring.TryDequeue(out _);

        try { _logWriter?.WriteLine(stamped); } catch { }
        LogLine?.Invoke(stamped);
    }

    public void Dispose()
    {
        Stop();
    }
}
