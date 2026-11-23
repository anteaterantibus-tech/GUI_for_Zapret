using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace Zapret.Core;

public enum MediaVerdict
{
    Ok,
    LikelyBlocked,
    Partial,
    Failed
}

public sealed class MediaResult
{
    public MediaVerdict Verdict { get; init; } = MediaVerdict.Failed;
    public long Bytes { get; init; }
    public double ElapsedMs { get; init; }
    public string Url { get; init; } = "";
    public string Detail { get; init; } = "";

    public string Text => Verdict switch
    {
        MediaVerdict.Ok => $"видео: OK, {Bytes / 1024} КБ, {ElapsedMs:0} мс",
        MediaVerdict.LikelyBlocked => $"видео: обрыв на {Bytes / 1024} КБ",
        MediaVerdict.Partial => $"видео: частично, {Bytes / 1024} КБ",
        _ => "видео: не загрузилось"
    };
}

/// <summary>
/// Downloads a real media file from the YouTube image CDN (i.ytimg.com).
/// This is closer to video playback than a plain HEAD request: the censor's
/// 16-20 KB freeze shows up as a stalled download.
/// </summary>
public static class MediaTester
{
    private static readonly string[] Urls =
    {
        "https://i.ytimg.com/vi/dQw4w9WgXcQ/maxresdefault.jpg",
        "https://i.ytimg.com/vi/dQw4w9WgXcQ/hq720.jpg",
        "https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg"
    };

    private const int MaxBytes = 262144;

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1)
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ZapretControl/1.0");
        return client;
    }

    public static async Task<MediaResult> TestAsync(int timeoutMs = 6000, CancellationToken ct = default)
    {
        MediaResult last = new() { Verdict = MediaVerdict.Failed, Detail = "нет подходящего файла" };

        foreach (var url in Urls)
        {
            var result = await TryAsync(url, timeoutMs, ct).ConfigureAwait(false);
            if (result.Verdict is not MediaVerdict.Failed || result.Bytes > 0) return result;
            last = result;
        }

        return last;
    }

    private static async Task<MediaResult> TryAsync(string url, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var stopwatch = Stopwatch.StartNew();
        long total = 0;
        long? length = null;
        var timedOut = false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, MaxBytes - 1);

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
            {
                return new MediaResult { Verdict = MediaVerdict.Failed, Url = url, Detail = $"HTTP {(int)response.StatusCode}" };
            }

            length = response.Content.Headers.ContentLength;
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);

            var buffer = new byte[16384];
            while (total < MaxBytes)
            {
                var read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MediaResult
            {
                Verdict = MediaVerdict.Failed,
                Bytes = total,
                Url = url,
                ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
                Detail = ex.Message
            };
        }

        stopwatch.Stop();

        var verdict = total == 0
            ? MediaVerdict.Failed
            : timedOut && total >= 8 * 1024
                ? MediaVerdict.LikelyBlocked
                : timedOut
                    ? MediaVerdict.Partial
                    : MediaVerdict.Ok;

        return new MediaResult
        {
            Verdict = verdict,
            Bytes = total,
            ElapsedMs = stopwatch.Elapsed.TotalMilliseconds,
            Url = url,
            Detail = length is > 0 ? $"из {length / 1024} КБ" : ""
        };
    }
}

public sealed class StunResult
{
    public bool Ok { get; init; }
    public int Successes { get; init; }
    public int Attempts { get; init; }
    public double RttMs { get; init; }
    public string Detail { get; init; } = "";

    public string Text => Ok
        ? $"UDP: OK, {RttMs:0} мс ({Successes}/{Attempts})"
        : $"UDP: нет ответа ({Successes}/{Attempts})";
}

/// <summary>
/// Minimal STUN binding request. Checks that UDP actually passes and comes back,
/// which is what Discord voice needs (the strategy profiles fake STUN packets).
/// </summary>
public static class StunTester
{
    private static readonly byte[] MagicCookie = { 0x21, 0x12, 0xA4, 0x42 };

    public static async Task<StunResult> TestAsync(
        string host = "stun.l.google.com",
        int port = 19302,
        int attempts = 3,
        int timeoutMs = 1500,
        CancellationToken ct = default)
    {
        var successes = 0;
        var rtts = new List<double>();

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            if (address is null)
                return new StunResult { Detail = "DNS не вернул адрес" };

            var endpoint = new IPEndPoint(address, port);
            using var udp = new UdpClient();

            for (var i = 0; i < attempts; i++)
            {
                var request = BuildRequest();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);

                var stopwatch = Stopwatch.StartNew();
                try
                {
                    await udp.SendAsync(request, endpoint, cts.Token).ConfigureAwait(false);
                    var response = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    stopwatch.Stop();

                    if (response.Buffer.Length >= 20)
                    {
                        var data = response.Buffer;
                        if (data[0] == 0x01 && data[1] == 0x01 &&
                            data[4] == MagicCookie[0] && data[5] == MagicCookie[1] &&
                            data[6] == MagicCookie[2] && data[7] == MagicCookie[3])
                        {
                            successes++;
                            rtts.Add(stopwatch.Elapsed.TotalMilliseconds);
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // this attempt timed out, try the next one
                }
                catch (SocketException)
                {
                    // blocked UDP, count as a failure
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StunResult { Detail = ex.Message };
        }

        return new StunResult
        {
            Ok = successes > 0,
            Successes = successes,
            Attempts = attempts,
            RttMs = rtts.Count > 0 ? rtts.Average() : 0,
            Detail = successes > 0 ? "" : "STUN не ответил"
        };
    }

    private static byte[] BuildRequest()
    {
        var request = new byte[20];
        request[0] = 0x00;
        request[1] = 0x01; // Binding Request
        request[2] = 0x00;
        request[3] = 0x00; // length
        Buffer.BlockCopy(MagicCookie, 0, request, 4, 4);
        Random.Shared.NextBytes(request.AsSpan(8, 12));
        return request;
    }
}
