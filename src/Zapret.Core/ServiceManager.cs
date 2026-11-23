namespace Zapret.Core;

public enum ServiceState
{
    NotInstalled,
    Stopped,
    Running,
    Pending,
    Unknown
}

/// <summary>
/// Installs the selected strategy as the auto-start Windows service "zapret".
/// Uses the same service name and registry label as upstream service.bat, so
/// both tools see each other, but builds the binPath from structured arguments
/// instead of re-parsing batch text.
/// </summary>
public sealed class ServiceManager
{
    public const string ServiceName = "zapret";
    public const string DriverName = "WinDivert";
    public const string StrategyValueName = "zapret-discord-youtube";

    private readonly ZapretPaths _paths;

    public ServiceManager(ZapretPaths paths) => _paths = paths;

    public async Task<ServiceState> GetStateAsync(CancellationToken ct = default)
    {
        var state = await WindowsProcess.GetServiceStateAsync(ServiceName, ct).ConfigureAwait(false);
        return state switch
        {
            null => ServiceState.NotInstalled,
            "RUNNING" => ServiceState.Running,
            "STOPPED" => ServiceState.Stopped,
            "STOP_PENDING" or "START_PENDING" => ServiceState.Pending,
            _ => ServiceState.Unknown
        };
    }

    /// <summary>Strategy file name recorded for the installed service, if any.</summary>
    public async Task<string?> GetInstalledStrategyAsync(CancellationToken ct = default)
    {
        var result = await WindowsProcess.RegQueryAsync($@"HKLM\System\CurrentControlSet\Services\{ServiceName}", StrategyValueName).ConfigureAwait(false);
        if (!result.Ok) return null;
        foreach (var line in result.Output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(StrategyValueName, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3) return string.Join(' ', parts.Skip(2));
        }
        return null;
    }

    public async Task<bool> InstallAsync(Strategy strategy, GameFilter gameFilter, CancellationToken ct = default)
    {
        if (!WindowsProcess.IsElevated)
            throw new InvalidOperationException("Установка службы требует прав администратора.");

        await UninstallAsync(ct).ConfigureAwait(false);

        var args = WinwsCommandBuilder.BuildArgs(strategy, gameFilter, _paths);
        var binPath = WinwsCommandBuilder.Quote(_paths.WinwsExe) + " " + WinwsCommandBuilder.Join(args);

        var create = await WindowsProcess.ScAsync("create", ServiceName,
            "binPath=", binPath,
            "DisplayName=", "zapret",
            "start=", "auto").ConfigureAwait(false);
        if (!create.Ok) return false;

        await WindowsProcess.ScAsync("description", ServiceName, "Zapret DPI bypass software (Zapret Control)").ConfigureAwait(false);
        await WindowsProcess.RunAsync("reg.exe", new[]
        {
            "add", $@"HKLM\System\CurrentControlSet\Services\{ServiceName}",
            "/v", StrategyValueName, "/t", "REG_SZ", "/d", strategy.FileName, "/f"
        }).ConfigureAwait(false);

        var start = await WindowsProcess.ScAsync("start", ServiceName).ConfigureAwait(false);
        return start.Ok || start.Output.Contains("1056", StringComparison.Ordinal); // already running
    }

    /// <summary>ImagePath of the installed service, e.g. "\"C:\zapret\bin\winws.exe\" --wf-tcp=...".</summary>
    public async Task<string?> GetImagePathAsync(CancellationToken ct = default)
    {
        var result = await WindowsProcess.RegQueryAsync($@"HKLM\System\CurrentControlSet\Services\{ServiceName}", "ImagePath").ConfigureAwait(false);
        if (!result.Ok) return null;

        foreach (var line in result.Output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("ImagePath", StringComparison.OrdinalIgnoreCase)) continue;
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"REG_(?:EXPAND_)?SZ\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        return null;
    }

    /// <summary>True when the service exists and points to this distribution.</summary>
    public async Task<bool> IsInstalledForAsync(ZapretPaths paths, CancellationToken ct = default)
    {
        var imagePath = await GetImagePathAsync(ct).ConfigureAwait(false);
        return imagePath is not null &&
               imagePath.Contains(paths.WinwsExe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Restarts the installed service, used after changing list files.</summary>
    public async Task<bool> RestartAsync(CancellationToken ct = default)
    {
        var state = await GetStateAsync(ct).ConfigureAwait(false);
        if (state is ServiceState.NotInstalled or ServiceState.Unknown) return false;

        await WindowsProcess.ScAsync("stop", ServiceName).ConfigureAwait(false);
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            if (await GetStateAsync(ct).ConfigureAwait(false) is ServiceState.Stopped) break;
        }

        var start = await WindowsProcess.ScAsync("start", ServiceName).ConfigureAwait(false);
        return start.Ok;
    }

    public async Task<bool> UninstallAsync(CancellationToken ct = default)
    {        if (!WindowsProcess.IsElevated)
            throw new InvalidOperationException("Удаление службы требует прав администратора.");

        var state = await WindowsProcess.GetServiceStateAsync(ServiceName, ct).ConfigureAwait(false);
        if (state is not null)
        {
            await WindowsProcess.ScAsync("stop", ServiceName).ConfigureAwait(false);
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                var s = await WindowsProcess.GetServiceStateAsync(ServiceName, ct).ConfigureAwait(false);
                if (s is null or "STOPPED") break;
            }
            await WindowsProcess.ScAsync("delete", ServiceName).ConfigureAwait(false);
        }

        WindowsProcess.KillAll("winws");
        return await WindowsProcess.GetServiceStateAsync(ServiceName, ct).ConfigureAwait(false) is null;
    }

    /// <summary>Removes a leftover WinDivert driver service, a common cause of "nothing works".</summary>
    public async Task<bool> RemoveWinDivertServiceAsync(CancellationToken ct = default)
    {
        foreach (var name in new[] { DriverName, "WinDivert14" })
        {
            await WindowsProcess.ScAsync("stop", name).ConfigureAwait(false);
            await WindowsProcess.ScAsync("delete", name).ConfigureAwait(false);
        }
        await Task.Delay(300, ct).ConfigureAwait(false);
        return await WindowsProcess.GetServiceStateAsync(DriverName, ct).ConfigureAwait(false) is null;
    }
}
