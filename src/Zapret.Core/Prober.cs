using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Zapret.Core;

public sealed record ProbeTarget(string Name, string? Url, string? PingHost = null)
{
    public string Host =>
        Url is not null
            ? new Uri(Url).Host
            : PingHost ?? Name;
}

public enum ProbeStatus
{
    Ok,
    Partial,
    Fail,
    Unsupported
}

public sealed class ProbeOutcome
{
    public required string Name { get; init; }
    public bool HasUrl { get; init; }
    public bool HasPing { get; init; }
    public bool Http11 { get; set; }
    public bool Tls12 { get; set; }
    public bool Tls13 { get; set; }
    public bool Dns { get; set; }
    public string Ping { get; set; } = "n/a";
    public double LatencyMs { get; set; }
    public string Detail { get; set; } = "";

    public int OkCount => (Http11 ? 1 : 0) + (Tls12 ? 1 : 0) + (Tls13 ? 1 : 0) + (Ping != "n/a" && Ping != "Timeout" ? 1 : 0);

    /// <summary>How many checks were planned for this target, regardless of the result.</summary>
    public int Attempts => (HasUrl ? 3 : 0) + (HasPing ? 1 : 0);

    public ProbeStatus Status =>
        Attempts == 0 ? ProbeStatus.Fail :
        OkCount == 0 ? ProbeStatus.Fail :
        OkCount < Attempts ? ProbeStatus.Partial :
        ProbeStatus.Ok;

    public string Tokens
    {
        get
        {
            var parts = new List<string>();
            if (Http11) parts.Add("H1:OK"); else parts.Add("H1:--");
            if (Tls12) parts.Add("TLS1.2:OK"); else parts.Add("TLS1.2:--");
            if (Tls13) parts.Add("TLS1.3:OK"); else parts.Add("TLS1.3:--");
            if (Ping != "n/a") parts.Add("ping:" + Ping);
            return string.Join("  ", parts);
        }
    }
}

/// <summary>
/// Connectivity probes used to compare strategies. They report only whether a
/// server answered (any HTTP status counts), because a blocked TLS handshake is
/// what a strategy is supposed to fix, not the page content.
/// </summary>
public sealed class Prober : IDisposable
{
    private readonly HttpClient _default;
    private readonly HttpClient _tls12;
    private readonly HttpClient _tls13;

    public Prober(int timeoutSeconds = 4)
    {
        _default = Create(timeoutSeconds, null);
        _tls12 = Create(timeoutSeconds, SslProtocols.Tls12);
        _tls13 = Create(timeoutSeconds, SslProtocols.Tls13);
    }

    private static HttpClient Create(int timeoutSeconds, SslProtocols? protocols)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(3, timeoutSeconds)),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            AllowAutoRedirect = false
        };
        if (protocols is not null) handler.SslOptions.EnabledSslProtocols = protocols.Value;

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ZapretControl/1.0");
        client.DefaultRequestHeaders.ConnectionClose = true;
        return client;
    }

    public async Task<ProbeOutcome> ProbeAsync(ProbeTarget target, CancellationToken ct = default)
    {
        var outcome = new ProbeOutcome
        {
            Name = target.Name,
            HasUrl = !string.IsNullOrEmpty(target.Url),
            HasPing = !string.IsNullOrEmpty(target.PingHost) || !string.IsNullOrEmpty(target.Url)
        };
        var latencies = new List<double>();

        if (!string.IsNullOrEmpty(target.Url))
        {
            outcome.Dns = await ResolveAsync(target.Host, ct).ConfigureAwait(false);

            var (h11, ms1, err1) = await CheckAsync(_default, target.Url!, ct).ConfigureAwait(false);
            outcome.Http11 = h11;
            if (ms1 > 0) latencies.Add(ms1);

            var (t12, ms2, err2) = await CheckAsync(_tls12, target.Url!, ct).ConfigureAwait(false);
            outcome.Tls12 = t12;
            if (ms2 > 0) latencies.Add(ms2);

            var (t13, ms3, err3) = await CheckAsync(_tls13, target.Url!, ct).ConfigureAwait(false);
            outcome.Tls13 = t13;
            if (ms3 > 0) latencies.Add(ms3);

            outcome.Detail = string.Join(" | ", new[] { err1, err2, err3 }.Where(e => !string.IsNullOrEmpty(e)));
        }

        if (!string.IsNullOrEmpty(target.PingHost) || !string.IsNullOrEmpty(target.Url))
        {
            var host = target.PingHost ?? target.Host;
            outcome.Ping = await PingAsync(host, ct).ConfigureAwait(false);
        }

        outcome.LatencyMs = latencies.Count > 0 ? Math.Round(latencies.Average(), 0) : 0;
        return outcome;
    }

    private static async Task<(bool ok, double ms, string error)> CheckAsync(HttpClient client, string url, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            return (true, sw.Elapsed.TotalMilliseconds, "");
        }
        catch (TaskCanceledException)
        {
            return (false, 0, "timeout");
        }
        catch (HttpRequestException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            return (false, 0, Classify(message));
        }
        catch (Exception ex)
        {
            return (false, 0, Classify(ex.Message));
        }
    }

    private static string Classify(string message)
    {
        if (message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            return "ssl";
        if (message.Contains("resolve", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No such host", StringComparison.OrdinalIgnoreCase))
            return "dns";
        return "fail";
    }

    private static async Task<bool> ResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string> PingAsync(string host, CancellationToken ct = default)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 1200).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? $"{reply.RoundtripTime} ms" : "Timeout";
        }
        catch
        {
            return "Timeout";
        }
    }

    public void Dispose()
    {
        _default.Dispose();
        _tls12.Dispose();
        _tls13.Dispose();
    }
}

public enum DpiVerdict
{
    Ok,
    LikelyBlocked,
    Failed,
    Unsupported
}

public sealed class DpiResult
{
    public required string Host { get; init; }
    public required string Protocol { get; init; }
    public DpiVerdict Verdict { get; init; }
    public long UpBytes { get; init; }
    public long DownBytes { get; init; }
    public double ElapsedMs { get; init; }
    public string Detail { get; init; } = "";

    public string Text =>
        $"{Protocol}: {Verdict}, отправлено {UpBytes / 1024} КБ, получено {DownBytes / 1024} КБ, {ElapsedMs:0} мс";
}

/// <summary>
/// "TCP 16-20" check from test zapret.ps1 / dpi-checkers: POST 64 KB and look for
/// the typical freeze after ~16 KB, which means the censor cuts the strategy.
/// Implemented on a raw socket so the byte counters are exact.
/// </summary>
public static class DpiTester
{
    public static async Task<DpiResult> TestAsync(string host, int timeoutMs = 5000, int payloadSize = 65536, CancellationToken ct = default)
    {
        var payload = new byte[payloadSize];
        Random.Shared.NextBytes(payload);

        var sw = Stopwatch.StartNew();
        long up = 0, down = 0;

        using var tcp = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(timeoutMs);
            await tcp.ConnectAsync(host, 443, connectCts.Token).ConfigureAwait(false);

            await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);

            try
            {
                var options = new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                };
                using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                authCts.CancelAfter(timeoutMs);
                await ssl.AuthenticateAsClientAsync(options, authCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var unsupported = ex.Message.Contains("protocol", StringComparison.OrdinalIgnoreCase) ||
                                  ex is AuthenticationException;
                return new DpiResult
                {
                    Host = host,
                    Protocol = "TLS",
                    Verdict = unsupported ? DpiVerdict.Unsupported : DpiVerdict.Failed,
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                    Detail = ex.Message
                };
            }

            var protocol = ssl.SslProtocol.ToString();
            var header = Encoding.ASCII.GetBytes(
                $"POST / HTTP/1.1\r\nHost: {host}\r\n" +
                $"Range: bytes=0-{payloadSize - 1}\r\n" +
                $"Content-Length: {payloadSize}\r\n" +
                "Content-Type: application/octet-stream\r\n" +
                "Accept: */*\r\nConnection: close\r\n\r\n");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            await ssl.WriteAsync(header, cts.Token).ConfigureAwait(false);
            await ssl.WriteAsync(payload, cts.Token).ConfigureAwait(false);
            up = payloadSize;

            var buffer = new byte[16384];
            try
            {
                while (true)
                {
                    var read = await ssl.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    down += read;
                    if (down >= 64 * 1024) break;
                }
            }
            catch (OperationCanceledException)
            {
                // expected for the freeze case
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                // connection reset after the fake also counts as data being sent
            }

            sw.Stop();

            var elapsed = sw.Elapsed.TotalMilliseconds;
            var verdict = down > 0 ? DpiVerdict.Ok :
                up >= 16 * 1024 && elapsed >= timeoutMs * 0.8 ? DpiVerdict.LikelyBlocked :
                DpiVerdict.Failed;

            return new DpiResult
            {
                Host = host,
                Protocol = protocol,
                Verdict = verdict,
                UpBytes = up,
                DownBytes = down,
                ElapsedMs = elapsed,
                Detail = verdict == DpiVerdict.LikelyBlocked ? "Сервер не ответил после ~16 КБ: стратегию режут." : ""
            };
        }
        catch (Exception ex)
        {
            return new DpiResult
            {
                Host = host,
                Protocol = "TCP",
                Verdict = DpiVerdict.Failed,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Detail = ex.Message
            };
        }
    }
}
