using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Zapret.Core;

public enum DiagStatus
{
    Ok,
    Warning,
    Error,
    Info
}

public sealed class DiagCheck
{
    public required string Group { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public DiagStatus Status { get; init; } = DiagStatus.Info;

    public string? FixLabel { get; init; }
    public Func<CancellationToken, Task<string>>? Fix { get; init; }

    public bool HasFix => Fix is not null;
}

/// <summary>
/// Re-implementation of service.bat diagnostics plus a few checks that the
/// legacy script cannot do (binaries consistency, live connectivity, service state).
/// Every check can be executed independently from the UI.
/// </summary>
public sealed class Diagnostics
{
    private readonly ZapretPaths _paths;

    public Diagnostics(ZapretPaths paths) => _paths = paths;

    public async Task<List<DiagCheck>> RunAsync(CancellationToken ct = default)
    {
        var checks = new List<DiagCheck>();
        checks.Add(CheckAdmin());
        checks.AddRange(CheckDistribution());
        checks.Add(CheckPath());
        checks.Add(CheckOneDrive());

        ct.ThrowIfCancellationRequested();
        checks.Add(await GuardAsync(CheckTimestampsAsync(ct), "TCP timestamps", "Сеть", ct).ConfigureAwait(false));
        checks.Add(CheckProxy());

        ct.ThrowIfCancellationRequested();
        checks.Add(await GuardAsync(CheckBfeAsync(ct), "Base Filtering Engine", "Сеть", ct).ConfigureAwait(false));
        checks.Add(await GuardAsync(CheckWinDivertAsync(ct), "Драйвер WinDivert", "Сеть", ct).ConfigureAwait(false));

        ct.ThrowIfCancellationRequested();
        checks.AddRange(await GuardListAsync(CheckBypassConflictsAsync(ct), "Конфликты", "Другие обходы", ct).ConfigureAwait(false));
        checks.AddRange(await GuardListAsync(CheckConflictingSoftwareAsync(ct), "Конфликты", "Конфликтующие программы", ct).ConfigureAwait(false));
        checks.Add(await GuardAsync(CheckVpnAsync(ct), "VPN-службы", "Конфликты", ct).ConfigureAwait(false));

        checks.Add(CheckHosts());
        checks.Add(CheckDoh());
        checks.Add(CheckIpSet());
        checks.Add(await GuardAsync(CheckServiceStateAsync(ct), "Служба автозапуска", "Служба", ct).ConfigureAwait(false));

        ct.ThrowIfCancellationRequested();
        checks.Add(await GuardAsync(CheckDiscordAsync(ct), "Discord", "Приложения", ct).ConfigureAwait(false));

        ct.ThrowIfCancellationRequested();
        checks.AddRange(await CheckDnsAsync(ct).ConfigureAwait(false));

        return checks;
    }

    /// <summary>Every networked or external check gets a hard time limit, so the UI never waits forever.</summary>
    private static async Task<DiagCheck> GuardAsync(Task<DiagCheck> task, string title, string group, CancellationToken ct, int seconds = 10)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new DiagCheck
            {
                Group = group,
                Title = title,
                Status = DiagStatus.Warning,
                Detail = $"Проверка не ответила за {seconds} секунд."
            };
        }
    }

    private static async Task<List<DiagCheck>> GuardListAsync(Task<List<DiagCheck>> task, string group, string title, CancellationToken ct, int seconds = 15)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new List<DiagCheck>
            {
                new()
                {
                    Group = group,
                    Title = title,
                    Status = DiagStatus.Warning,
                    Detail = $"Проверка не ответила за {seconds} секунд."
                }
            };
        }
    }

    private DiagCheck CheckAdmin() => new()
    {
        Group = "Система",
        Title = "Права администратора",
        Status = WindowsProcess.IsElevated ? DiagStatus.Ok : DiagStatus.Error,
        Detail = WindowsProcess.IsElevated
            ? "Приложение запущено с повышенными правами."
            : "Без прав администратора нельзя запустить winws и управлять службами."
    };

    private IEnumerable<DiagCheck> CheckDistribution()
    {
        var missing = new List<string>();
        if (!File.Exists(_paths.WinwsExe)) missing.Add("bin\\winws.exe");
        if (!File.Exists(_paths.WinDivertDll)) missing.Add("bin\\WinDivert.dll");
        if (!File.Exists(_paths.WinDivertSys)) missing.Add("bin\\WinDivert64.sys");
        if (!File.Exists(Path.Combine(_paths.BinDir, "cygwin1.dll"))) missing.Add("bin\\cygwin1.dll");

        yield return new DiagCheck
        {
            Group = "Система",
            Title = "Файлы обхода",
            Status = missing.Count == 0 ? DiagStatus.Ok : DiagStatus.Error,
            Detail = missing.Count == 0
                ? $"Все компоненты на месте ({_paths.Root})."
                : "Отсутствуют: " + string.Join(", ", missing) + ". Переустановите сборку через вкладку «Обновления»."
        };

        var version = UpdateService.GetInstalledVersion(_paths);
        yield return new DiagCheck
        {
            Group = "Система",
            Title = "Версия сборки",
            Status = version is null ? DiagStatus.Warning : DiagStatus.Info,
            Detail = version is null
                ? "Не удалось определить версию: в service.bat нет LOCAL_VERSION."
                : $"Установлена версия {version}."
        };

        // WinDivert.dll and WinDivert64.sys must come from the same build.
        string detail;
        var status = DiagStatus.Ok;
        try
        {
            var dll = FileVersionInfo.GetVersionInfo(_paths.WinDivertDll).FileVersion;
            var sys = FileVersionInfo.GetVersionInfo(_paths.WinDivertSys).FileVersion;

            if (string.IsNullOrEmpty(dll) || string.IsNullOrEmpty(sys))
            {
                status = DiagStatus.Info;
                detail = "Версию WinDivert определить не удалось (в файлах нет сведений о версии). Проверьте целостность bin.";
            }
            else
            {
                var same = dll == sys;
                status = same ? DiagStatus.Ok : DiagStatus.Warning;
                detail = same
                    ? $"WinDivert {dll}: библиотека и драйвер совпадают."
                    : $"Версии WinDivert различаются (dll {dll}, sys {sys}). Это частая причина отказа драйвера.";
            }
        }
        catch (Exception ex)
        {
            status = DiagStatus.Warning;
            detail = "Не удалось прочитать версию WinDivert: " + ex.Message;
        }

        yield return new DiagCheck
        {
            Group = "Система",
            Title = "Совместимость WinDivert",
            Status = status,
            Detail = detail
        };
    }

    private DiagCheck CheckPath()
    {
        var cyrillic = Regex.IsMatch(_paths.Root, @"[\u0400-\u04FF]");
        var special = _paths.Root.Contains('&') || _paths.Root.Contains(';');
        var status = cyrillic ? DiagStatus.Warning : special ? DiagStatus.Warning : DiagStatus.Ok;
        var detail = cyrillic
            ? "Путь содержит кириллицу. Если обход не работает, перенесите папку, например в C:\\zapret."
            : special
                ? "Путь содержит символы & или ;, cmd-скрипты могут работать некорректно."
                : "Путь содержит только допустимые символы.";
        return new DiagCheck { Group = "Система", Title = "Путь установки", Status = status, Detail = detail };
    }

    private DiagCheck CheckOneDrive()
    {
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        var inside = !string.IsNullOrEmpty(oneDrive) &&
                     _paths.Root.StartsWith(oneDrive!, StringComparison.OrdinalIgnoreCase);
        return new DiagCheck
        {
            Group = "Система",
            Title = "Расположение в OneDrive",
            Status = inside ? DiagStatus.Error : DiagStatus.Ok,
            Detail = inside
                ? "Папка синхронизируется OneDrive: файлы могут блокироваться. Перенесите сборку в C:\\zapret."
                : "Синхронизация OneDrive не мешает."
        };
    }

    private async Task<DiagCheck> CheckTimestampsAsync(CancellationToken ct)
    {
        var result = await WindowsProcess.RunAsync("netsh.exe", new[] { "interface", "tcp", "show", "global" }, 10000, ct).ConfigureAwait(false);
        var line = result.Output.Split('\n').FirstOrDefault(l => l.Contains("timestamps", StringComparison.OrdinalIgnoreCase));
        var enabled = line is not null && line.Contains("enabled", StringComparison.OrdinalIgnoreCase);

        return new DiagCheck
        {
            Group = "Сеть",
            Title = "TCP timestamps",
            Status = enabled ? DiagStatus.Ok : DiagStatus.Warning,
            Detail = enabled
                ? "TCP timestamps включены."
                : "TCP timestamps выключены, часть стратегий с fooling=ts работать не будет.",
            FixLabel = enabled ? null : "Включить",
            Fix = enabled
                ? null
                : async c =>
                {
                    var set = await WindowsProcess.RunAsync("netsh.exe", new[] { "interface", "tcp", "set", "global", "timestamps=enabled" }, 10000, c).ConfigureAwait(false);
                    return set.Ok ? "TCP timestamps включены." : "Не удалось включить: " + set.All;
                }
        };
    }

    private static DiagCheck CheckProxy()
    {
        var path = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        var output = RunSync("reg.exe", "query", path, "/v", "ProxyEnable");
        var enabled = output.Contains("0x1", StringComparison.OrdinalIgnoreCase);
        var server = enabled ? RunSync("reg.exe", "query", path, "/v", "ProxyServer") : "";

        return new DiagCheck
        {
            Group = "Сеть",
            Title = "Системный прокси",
            Status = enabled ? DiagStatus.Warning : DiagStatus.Ok,
            Detail = enabled
                ? "Прокси включён" + (string.IsNullOrWhiteSpace(server) ? "." : ": " + LastValue(server) + ". Убедитесь, что он рабочий.")
                : "Прокси не используется."
        };
    }

    private async Task<DiagCheck> CheckBfeAsync(CancellationToken ct)
    {
        var state = await WindowsProcess.GetServiceStateAsync("BFE", ct).ConfigureAwait(false);
        return new DiagCheck
        {
            Group = "Сеть",
            Title = "Base Filtering Engine",
            Status = state == "RUNNING" ? DiagStatus.Ok : DiagStatus.Error,
            Detail = state == "RUNNING"
                ? "Служба BFE работает, фильтрация пакетов доступна."
                : $"Служба BFE в состоянии {state ?? "не найдена"}. Без неё winws не сможет перехватывать трафик."
        };
    }

    private async Task<DiagCheck> CheckWinDivertAsync(CancellationToken ct)
    {
        var state = await WindowsProcess.GetServiceStateAsync("WinDivert", ct).ConfigureAwait(false);
        var winwsRunning = WindowsProcess.IsProcessRunning("winws");

        if (state is null)
        {
            return new DiagCheck
            {
                Group = "Сеть",
                Title = "Драйвер WinDivert",
                Status = DiagStatus.Ok,
                Detail = "Служба WinDivert не зарегистрирована (нормально, пока обход не запущен)."
            };
        }

        if (winwsRunning)
        {
            return new DiagCheck
            {
                Group = "Сеть",
                Title = "Драйвер WinDivert",
                Status = state is "RUNNING" or "STOPPED" ? DiagStatus.Ok : DiagStatus.Warning,
                Detail = $"WinDivert зарегистрирован ({state}), winws работает."
            };
        }

        return new DiagCheck
        {
            Group = "Сеть",
            Title = "Драйвер WinDivert",
            Status = state == "STOPPED" ? DiagStatus.Warning : DiagStatus.Info,
            Detail = $"WinDivert зарегистрирован ({state}), но winws не запущен. Остаточная служба может мешать запуску.",
            FixLabel = "Удалить службу",
            Fix = async c =>
            {
                var ok = await new ServiceManager(_paths).RemoveWinDivertServiceAsync(c).ConfigureAwait(false);
                return ok ? "Служба WinDivert удалена." : "Не удалось удалить службу WinDivert.";
            }
        };
    }

    private async Task<List<DiagCheck>> CheckBypassConflictsAsync(CancellationToken ct)
    {
        var checks = new List<DiagCheck>();
        var services = await WindowsProcess.ListServiceNamesAsync(ct).ConfigureAwait(false);
        var known = new[] { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" };
        var found = known.Where(services.Contains).ToList();

        checks.Add(new DiagCheck
        {
            Group = "Конфликты",
            Title = "Другие обходы",
            Status = found.Count == 0 ? DiagStatus.Ok : DiagStatus.Error,
            Detail = found.Count == 0
                ? "Конфликтующих служб обхода не найдено."
                : "Найдены: " + string.Join(", ", found) + ". Они перехватывают тот же трафик.",
            FixLabel = found.Count == 0 ? null : "Остановить и удалить",
            Fix = found.Count == 0
                ? null
                : async c =>
                {
                    var log = new List<string>();
                    foreach (var name in found)
                    {
                        await WindowsProcess.ScAsync("stop", name).ConfigureAwait(false);
                        var del = await WindowsProcess.ScAsync("delete", name).ConfigureAwait(false);
                        log.Add($"{name}: {(del.Ok ? "удалена" : "ошибка")}");
                    }
                    await new ServiceManager(_paths).RemoveWinDivertServiceAsync(c).ConfigureAwait(false);
                    WindowsProcess.KillAll("winws");
                    return string.Join("; ", log);
                }
        });

        return checks;
    }

    private async Task<List<DiagCheck>> CheckConflictingSoftwareAsync(CancellationToken ct)
    {
        var checks = new List<DiagCheck>();
        var services = await WindowsProcess.ListServiceNamesAsync(ct).ConfigureAwait(false);
        var adguard = WindowsProcess.IsProcessRunning("AdguardSvc");

        checks.Add(new DiagCheck
        {
            Group = "Конфликты",
            Title = "Adguard",
            Status = adguard ? DiagStatus.Error : DiagStatus.Ok,
            Detail = adguard
                ? "Adguard перехватывает DNS и трафик, Discord может не работать."
                : "Не обнаружен."
        });

        var killer = services.Where(s => s.Contains("Killer", StringComparison.OrdinalIgnoreCase)).ToList();
        checks.Add(new DiagCheck
        {
            Group = "Конфликты",
            Title = "Killer (Rivet Networks)",
            Status = killer.Count > 0 ? DiagStatus.Error : DiagStatus.Ok,
            Detail = killer.Count > 0
                ? "Найдены службы Killer: " + string.Join(", ", killer) + ". Конфликтуют с zapret."
                : "Не обнаружен."
        });

        var intel = services.Any(s => s.Contains("Intel", StringComparison.OrdinalIgnoreCase) &&
                                      s.Contains("Connectivity", StringComparison.OrdinalIgnoreCase));
        checks.Add(new DiagCheck
        {
            Group = "Конфликты",
            Title = "Intel Connectivity Network",
            Status = intel ? DiagStatus.Error : DiagStatus.Ok,
            Detail = intel
                ? "Служба Intel Connectivity Network конфликтует с перехватом трафика."
                : "Не обнаружена."
        });

        var checkpoint = services.Any(s => s.Contains("TracSrvWrapper", StringComparison.OrdinalIgnoreCase) ||
                                           s.Contains("EPWD", StringComparison.OrdinalIgnoreCase));
        checks.Add(new DiagCheck
        {
            Group = "Конфликты",
            Title = "Check Point",
            Status = checkpoint ? DiagStatus.Error : DiagStatus.Ok,
            Detail = checkpoint ? "Службы Check Point конфликтуют с zapret." : "Не обнаружен."
        });

        var smartByte = services.Any(s => s.Contains("SmartByte", StringComparison.OrdinalIgnoreCase));
        checks.Add(new DiagCheck
        {
            Group = "Конфликты",
            Title = "SmartByte",
            Status = smartByte ? DiagStatus.Error : DiagStatus.Ok,
            Detail = smartByte ? "SmartByte конфликтует с zapret, отключите его в services.msc." : "Не обнаружен."
        });

        return checks;
    }

    private async Task<DiagCheck> CheckVpnAsync(CancellationToken ct)
    {
        var services = await WindowsProcess.ListServiceNamesAsync(ct).ConfigureAwait(false);
        var vpn = services.Where(s =>
            s.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("Happ", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Tun", StringComparison.OrdinalIgnoreCase)).Take(6).ToList();

        return new DiagCheck
        {
            Group = "Конфликты",
            Title = "VPN-службы",
            Status = vpn.Count == 0 ? DiagStatus.Ok : DiagStatus.Warning,
            Detail = vpn.Count == 0
                ? "VPN-служб не найдено."
                : "Найдены: " + string.Join(", ", vpn) + ". Выключите VPN, если обход не работает."
        };
    }

    private DiagCheck CheckHosts()
    {
        var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        var entries = 0;
        var hasZapretBlock = UpdateService.HostsHasBlock();

        try
        {
            if (File.Exists(hostsPath))
            {
                entries = File.ReadAllLines(hostsPath)
                    .Count(l => l.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("youtu.be", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch
        {
            // ignore
        }

        return new DiagCheck
        {
            Group = "Сеть",
            Title = "Файл hosts",
            Status = entries > 0 ? DiagStatus.Warning : DiagStatus.Ok,
            Detail = entries > 0
                ? $"В hosts есть {entries} записей youtube.com/youtu.be: это может ломать YouTube."
                : hasZapretBlock
                    ? "hosts содержит блок zapret-control, записей YouTube нет."
                    : "Записей YouTube в hosts нет."
        };
    }

    private DiagCheck CheckDoh()
    {
        var output = RunSync("reg.exe", "query", @"HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters", "/s", "/v", "DohFlags");
        var hasDoh = Regex.IsMatch(output, @"DohFlags\s+REG_\w+\s+0x[1-9a-fA-F]", RegexOptions.IgnoreCase);

        return new DiagCheck
        {
            Group = "Сеть",
            Title = "Secure DNS (DoH)",
            Status = hasDoh ? DiagStatus.Ok : DiagStatus.Warning,
            Detail = hasDoh
                ? "Шифрованный DNS настроен в системе."
                : "DoH в Windows не настроен. Включите безопасный DNS в браузере или в настройках Windows 11."
        };
    }

    private DiagCheck CheckIpSet()
    {
        var mode = IpSetManager.GetMode(_paths);
        var size = File.Exists(_paths.IpSetAll) ? new FileInfo(_paths.IpSetAll).Length : 0;
        return new DiagCheck
        {
            Group = "Списки",
            Title = "IPSet",
            Status = mode == IpSetMode.Loaded || mode == IpSetMode.Any ? DiagStatus.Ok : DiagStatus.Warning,
            Detail = mode switch
            {
                IpSetMode.Loaded => $"Режим loaded, {size / 1024} КБ адресов.",
                IpSetMode.Any => "Режим any: фильтр применяется ко всем адресам.",
                _ => "Режим none: обход работает только по спискам доменов."
            }
        };
    }

    private async Task<DiagCheck> CheckServiceStateAsync(CancellationToken ct)
    {
        var state = await new ServiceManager(_paths).GetStateAsync(ct).ConfigureAwait(false);
        return new DiagCheck
        {
            Group = "Служба",
            Title = "Служба автозапуска zapret",
            Status = DiagStatus.Info,
            Detail = state switch
            {
                ServiceState.Running => "Служба установлена и работает.",
                ServiceState.Stopped => "Служба установлена, но остановлена.",
                ServiceState.NotInstalled => "Служба не установлена (ручной запуск).",
                _ => "Состояние службы: " + state
            }
        };
    }

    private async Task<DiagCheck> CheckDiscordAsync(CancellationToken ct)
    {
        var running = WindowsProcess.IsProcessRunning("Discord") || WindowsProcess.IsProcessRunning("DiscordPTB");
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "Cache");
        var cacheSize = 0L;
        if (Directory.Exists(cacheDir))
        {
            try
            {
                cacheSize = new DirectoryInfo(cacheDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch
            {
                // ignore
            }
        }

        return await Task.FromResult(new DiagCheck
        {
            Group = "Приложения",
            Title = "Discord",
            Status = running ? DiagStatus.Info : DiagStatus.Ok,
            Detail = (running ? "Discord запущен." : "Discord не запущен.") +
                     (cacheSize > 0 ? $" Кэш: {cacheSize / 1024 / 1024} МБ." : ""),
            FixLabel = running || cacheSize > 0 ? "Очистить кэш" : null,
            Fix = async c =>
            {
                var closed = 0;
                foreach (var name in new[] { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" })
                    closed += WindowsProcess.KillAll(name);

                var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var cleared = 0;
                foreach (var dir in new[] { "discord", "discordptb", "discordcanary", "discorddevelopment" })
                {
                    foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
                    {
                        var path = Path.Combine(baseDir, dir, sub);
                        try
                        {
                            if (Directory.Exists(path)) { Directory.Delete(path, true); cleared++; }
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
                return await Task.FromResult($"Закрыто процессов: {closed}, очищено папок кэша: {cleared}.");
            }
        }).ConfigureAwait(false);
    }

    private async Task<List<DiagCheck>> CheckDnsAsync(CancellationToken ct)
    {
        var checks = new List<DiagCheck>();
        foreach (var host in new[] { "discord.com", "www.youtube.com", "www.google.com" })
        {
            var sw = Stopwatch.StartNew();
            string? error = null;
            var ok = false;
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct)
                    .WaitAsync(TimeSpan.FromSeconds(4), ct)
                    .ConfigureAwait(false);
                ok = addresses.Length > 0;
            }
            catch (TimeoutException)
            {
                error = "нет ответа за 4 секунды";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            sw.Stop();

            checks.Add(new DiagCheck
            {
                Group = "Сеть",
                Title = "DNS " + host,
                Status = ok ? DiagStatus.Ok : DiagStatus.Error,
                Detail = ok
                    ? sw.ElapsedMilliseconds < 1
                        ? "Разрешается мгновенно: ответ взят из кэша DNS."
                        : $"Разрешается за {sw.ElapsedMilliseconds} мс."
                    : "Не разрешается: " + error
            });
        }
        return checks;
    }

    private static string RunSync(string file, params string[] args)
    {
        try
        {
            return WindowsProcess.RunAsync(file, args, 8000).GetAwaiter().GetResult().Output;
        }
        catch
        {
            return "";
        }
    }

    private static string LastValue(string regOutput)
    {
        var line = regOutput.Split('\n').LastOrDefault(l => l.Contains("REG_", StringComparison.OrdinalIgnoreCase));
        if (line is null) return "";
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? string.Join(" ", parts.Skip(2)).Trim() : line.Trim();
    }
}
