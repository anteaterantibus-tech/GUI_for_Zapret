using System.Security.Cryptography;
using Zapret.Core;

namespace Zapret.App;

public sealed class AppUpdateOutcome
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public string? OldVersion { get; init; }
    public string? NewVersion { get; init; }
    public string? BackupPath { get; init; }
    public string? Sha256 { get; init; }
    public bool ServiceRestored { get; init; }
    public bool StrategyRestarted { get; init; }
}

/// <summary>
/// One update pipeline shared by the "Обновления" page and the background auto-install,
/// so both paths behave identically: download, verify, install, restore service and strategy.
/// </summary>
public static class UpdateCoordinator
{
    public static async Task<AppUpdateOutcome> InstallAsync(
        ReleaseInfo release,
        ZapretPaths paths,
        UpdateOptions options,
        IProgress<string>? log = null,
        IProgress<DownloadProgress>? download = null,
        CancellationToken ct = default)
    {
        var oldVersion = UpdateService.GetInstalledVersion(paths);
        var wasRunning = AppState.IsRunning;
        var runningStrategy = AppState.Runner?.CurrentStrategy;

        log?.Report("Скачивание архива...");
        var zip = await AppState.Updater.DownloadReleaseAsync(release, download, ct).ConfigureAwait(false);
        var hash = await ComputeSha256Async(zip).ConfigureAwait(false);

        log?.Report("Установка...");
        var result = await AppState.Updater.InstallAsync(zip, paths, options, log, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            return new AppUpdateOutcome
            {
                Success = false,
                Message = result.Message,
                OldVersion = oldVersion,
                BackupPath = result.BackupPath,
                Sha256 = hash
            };
        }

        // Pick up the new strategy files and paths.
        AppState.TrySetRoot(paths.Root, out _);

        var strategyToRestore = AppState.Find(runningStrategy)
                                ?? AppState.Strategies.FirstOrDefault(s => s.FileName == result.RestartStrategy)
                                ?? AppState.Selected;

        var serviceRestored = false;
        if (result.ServiceWasInstalled && strategyToRestore is not null)
        {
            log?.Report("Восстановление службы автозапуска...");
            try
            {
                await new ServiceManager(paths).InstallAsync(strategyToRestore, AppState.GameFilter).ConfigureAwait(false);
                serviceRestored = true;
            }
            catch (Exception ex)
            {
                log?.Report("Служба не восстановлена: " + ex.Message);
            }
            await AppState.RefreshServiceStateAsync().ConfigureAwait(false);
        }

        var restarted = false;
        if (wasRunning && !serviceRestored && strategyToRestore is not null && options.RestartAfterUpdate)
        {
            log?.Report("Перезапуск стратегии...");
            try
            {
                await AppState.StartAsync(strategyToRestore).ConfigureAwait(false);
                restarted = true;
            }
            catch (Exception ex)
            {
                log?.Report("Стратегия не запущена: " + ex.Message);
            }
        }

        return new AppUpdateOutcome
        {
            Success = true,
            Message = "Обновление установлено.",
            OldVersion = result.OldVersion ?? oldVersion,
            NewVersion = result.NewVersion,
            BackupPath = result.BackupPath,
            Sha256 = hash,
            ServiceRestored = serviceRestored,
            StrategyRestarted = restarted
        };
    }

    public static UpdateOptions BuildOptions()
    {
        return new UpdateOptions
        {
            PreserveActiveFakes = AppState.Settings.PreserveActiveFakesOnUpdate,
            PreserveIpSet = AppState.Settings.PreserveIpSetOnUpdate,
            PreserveUserFiles = true,
            ListMode = AppState.Settings.ListUpdateMode,
            RestartAfterUpdate = true
        };
    }

    public static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
