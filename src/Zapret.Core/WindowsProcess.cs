using System.Diagnostics;
using System.Text;

namespace Zapret.Core;

public static class WindowsProcess
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public sealed record CommandResult(int ExitCode, string Output, string Error)
    {
        public bool Ok => ExitCode == 0;
        public string All => (Output + Environment.NewLine + Error).Trim();
    }

    /// <summary>Runs a console tool without a window and returns merged output.</summary>
    public static async Task<CommandResult> RunAsync(string fileName, IEnumerable<string> args, int timeoutMs = 20000, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return new CommandResult(-1, "", "не удалось запустить " + fileName);

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
            }

            return new CommandResult(process.HasExited ? process.ExitCode : -1, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, "", ex.Message);
        }
    }

    public static async Task<CommandResult> ScAsync(params string[] args) => await RunAsync("sc.exe", args).ConfigureAwait(false);

    public static async Task<CommandResult> RegQueryAsync(string key, string? value = null)
    {
        var args = new List<string> { "query", key };
        if (value is not null) { args.Add("/v"); args.Add(value); }
        return await RunAsync("reg.exe", args).ConfigureAwait(false);
    }

    /// <summary>State of a Windows service by short name: "RUNNING", "STOPPED", "STOP_PENDING", null when not installed.</summary>
    public static async Task<string?> GetServiceStateAsync(string name, CancellationToken ct = default)
    {
        var result = await ScAsync("query", name).ConfigureAwait(false);
        if (!result.Ok) return null;

        // sc.exe localizes the field labels ("СОСТОЯНИЕ" on Russian Windows),
        // but the state tokens themselves are always English.
        var match = System.Text.RegularExpressions.Regex.Match(
            result.Output,
            @"\b(RUNNING|STOPPED|STOP_PENDING|START_PENDING|PAUSED_PENDING|PAUSED)\b");
        return match.Success ? match.Groups[1].Value : "UNKNOWN";
    }

    public static async Task<bool> ServiceExistsAsync(string name, CancellationToken ct = default) =>
        (await GetServiceStateAsync(name, ct).ConfigureAwait(false)) is not null;

    /// <summary>All registered service names (short names), read from the registry so localization does not matter.</summary>
    public static async Task<HashSet<string>> ListServiceNamesAsync(CancellationToken ct = default)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = await RunAsync("reg.exe", new[] { "query", @"HKLM\SYSTEM\CurrentControlSet\Services" }, 30000, ct).ConfigureAwait(false);

        foreach (var line in result.Output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || !trimmed.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)) continue;
            var idx = trimmed.LastIndexOf('\\');
            if (idx > 0 && idx < trimmed.Length - 1) names.Add(trimmed[(idx + 1)..]);
        }
        return names;
    }

    public static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<Process> GetProcesses(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName);
        }
        catch
        {
            return Array.Empty<Process>();
        }
    }

    /// <summary>Kills every winws.exe instance, including ones started by the .bat files.</summary>
    public static int KillAll(string processName)
    {
        var killed = 0;
        foreach (var p in GetProcesses(processName))
        {
            try
            {
                p.Kill(true);
                p.WaitForExit(1200);
                killed++;
            }
            catch
            {
                // already gone or access denied
            }
            finally
            {
                p.Dispose();
            }
        }
        return killed;
    }

    /// <summary>Kills only instances started from the given executable, never another installation.</summary>
    public static int KillAllFrom(string exePath)
    {
        var killed = 0;
        foreach (var p in GetProcesses("winws"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
                    continue;

                p.Kill(true);
                p.WaitForExit(1200);
                killed++;
            }
            catch
            {
                // access denied / already gone
            }
            finally
            {
                p.Dispose();
            }
        }
        return killed;
    }
}
