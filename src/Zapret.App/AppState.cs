using Zapret.Core;

namespace Zapret.App;

/// <summary>Shared application state: one distribution folder, one winws process, one settings file.</summary>
public static class AppState
{
    public static AppSettings Settings { get; private set; } = new();
    public static ZapretPaths? Paths { get; private set; }
    public static List<Strategy> Strategies { get; private set; } = new();
    public static GameFilter GameFilter { get; set; } = new();
    public static WinwsRunner? Runner { get; private set; }
    public static ServiceManager? Service { get; private set; }
    public static UserLists? Lists { get; private set; }
    public static UpdateService Updater { get; } = new();
    public static Strategy? Selected { get; private set; }
    public static ServiceState ServiceStateValue { get; private set; } = ServiceState.NotInstalled;

    /// <summary>True when the installed service belongs to this distribution folder.</summary>
    public static bool ServiceBelongsToRoot { get; private set; }

    /// <summary>Display name of the strategy recorded for the installed service.</summary>
    public static string? ServiceStrategyName { get; private set; }

    public static event Action? StatusChanged;
    public static event Action? StrategiesReloaded;

    public static bool Ready => Paths is not null && Runner is not null;
    public static bool IsRunning => Runner?.IsRunning == true;

    /// <summary>True while a strategy test or an update is in progress (they stop winws).</summary>
    public static bool Busy { get; set; }

    /// <summary>Set by the active view so the main window's Stop button cancels a running operation.</summary>
    public static Action? CancelCurrentOperation { get; set; }
    public static string? InstalledVersion => Paths is null ? null : UpdateService.GetInstalledVersion(Paths);

    public static void Init()
    {
        AppPaths.Ensure();
        Settings = AppSettings.Load();

        if (!string.IsNullOrWhiteSpace(Settings.ZapretRoot) && !TrySetRoot(Settings.ZapretRoot!, out _))
        {
            Settings.ZapretRoot = null;
        }

        if (Paths is null)
        {
            var found = ZapretPaths.Discover();
            if (found is not null) TrySetRoot(found.Root, out _);
        }

        if (Settings.Targets.Count == 0)
            Settings.Targets = AppSettings.DefaultTargets();
    }

    public static bool TrySetRoot(string root, out string message)
    {
        var paths = new ZapretPaths(root);
        if (!paths.IsValid)
        {
            message = "В выбранной папке нет bin\\winws.exe. Укажите папку, куда распакована сборка zapret.";
            return false;
        }

        Runner?.Dispose();
        Paths = paths;
        Runner = new WinwsRunner(paths, AppPaths.LogsDir);
        Runner.LogLine += _ => StatusChanged?.Invoke();
        Service = new ServiceManager(paths);
        Lists = new UserLists(paths);
        try { Lists.EnsureDefaults(); } catch { /* read-only folder */ }
        GameFilter = GameFilter.Load(paths);

        Settings.ZapretRoot = paths.Root;
        Settings.Save();
        ReloadStrategies();
        message = "Папка сборки: " + paths.Root;
        return true;
    }

    public static void ReloadStrategies()
    {
        if (Paths is null) return;

        Strategies = BatStrategyParser.ParseAll(Paths).ToList();
        Selected = Strategies.FirstOrDefault(s => s.Name.Equals(Settings.LastStrategy, StringComparison.OrdinalIgnoreCase))
                   ?? Strategies.FirstOrDefault();

        if (Selected is not null)
        {
            Settings.LastStrategy = Selected.Name;
        }

        StrategiesReloaded?.Invoke();
        StatusChanged?.Invoke();
    }

    public static void Select(Strategy strategy)
    {
        Selected = strategy;
        Settings.LastStrategy = strategy.Name;
        Settings.Save();
        StatusChanged?.Invoke();
    }

    public static Strategy? Find(string? name) =>
        name is null ? null : Strategies.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static async Task RefreshServiceStateAsync()
    {
        if (Service is null || Paths is null) return;
        try
        {
            ServiceStateValue = await Service.GetStateAsync().ConfigureAwait(false);
            ServiceBelongsToRoot = await Service.IsInstalledForAsync(Paths).ConfigureAwait(false);

            var file = ServiceBelongsToRoot
                ? await Service.GetInstalledStrategyAsync().ConfigureAwait(false)
                : null;

            ServiceStrategyName = file is null
                ? null
                : Strategies.FirstOrDefault(s => s.FileName.Equals(file, StringComparison.OrdinalIgnoreCase))?.Name ?? file;
        }
        catch
        {
            ServiceStateValue = ServiceState.Unknown;
            ServiceBelongsToRoot = false;
            ServiceStrategyName = null;
        }
        StatusChanged?.Invoke();
    }

    public static async Task StartAsync(Strategy? strategy = null)
    {
        if (Runner is null) throw new InvalidOperationException("Сборка zapret не найдена.");
        strategy ??= Selected ?? Strategies.FirstOrDefault();
        if (strategy is null) throw new InvalidOperationException("Нет доступных стратегий.");

        Selected = strategy;
        Settings.LastStrategy = strategy.Name;
        Settings.Save();
        await Runner.StartAsync(strategy, GameFilter).ConfigureAwait(false);
        StatusChanged?.Invoke();
    }

    public static void Stop()
    {
        var runner = Runner;
        if (runner is null) return;

        // Killing winws and waiting for WinDivert can take seconds: never do it on the UI thread.
        _ = Task.Run(() =>
        {
            runner.Stop();
            runner.StopAllExternal();
            StatusChanged?.Invoke();
        });
        StatusChanged?.Invoke();
    }

    /// <summary>Stops the bypass and waits for the process to exit. Safe to await from the UI.</summary>
    public static async Task StopAsync()
    {
        var runner = Runner;
        if (runner is null) return;

        await Task.Run(() =>
        {
            runner.Stop();
            runner.StopAllExternal();
        }).ConfigureAwait(true);

        StatusChanged?.Invoke();
    }

    public static async Task<string> RestartBypassAsync()
    {
        if (Paths is null) return "Папка сборки не выбрана.";

        var service = new ServiceManager(Paths);
        if (await service.IsInstalledForAsync(Paths).ConfigureAwait(false))
        {
            var ok = await service.RestartAsync().ConfigureAwait(false);
            await RefreshServiceStateAsync().ConfigureAwait(false);
            return ok ? "Служба перезапущена." : "Не удалось перезапустить службу.";
        }

        if (Runner?.IsRunning == true)
        {
            var name = Runner.CurrentStrategy;
            await StopAsync().ConfigureAwait(false);
            var strategy = Find(name) ?? Selected;
            if (strategy is not null)
            {
                await StartAsync(strategy).ConfigureAwait(false);
                return "Стратегия перезапущена.";
            }
        }

        return "Обход не запущен: изменения применятся при следующем запуске.";
    }

    public static IReadOnlyList<ProbeTarget> ActiveTargets() =>
        Settings.Targets.Where(t => t.Enabled).Select(t => t.ToProbeTarget()).ToList();

    public static void RaiseStatus() => StatusChanged?.Invoke();
}
