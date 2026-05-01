using ByeWhiteList.Windows.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ByeWhiteList.Windows.Services
{
    public static class ServerSpeedTester
    {
        private const int DefaultTimeoutSeconds = 3;
        private const int DefaultMaxBytes = 64 * 1024 * 1024; // 64 MB cap (streamed; duration-limited)
        private const int DefaultMaxDurationMs = 1800; // ~1.8s per server (approx)
        private const int DefaultConnectTimeoutMs = 700;

        public static async Task<List<SpeedTestResult>> TestServersAsync(
            List<ProxyEntity> servers,
            int maxConcurrency = 2,
            int timeoutSeconds = DefaultTimeoutSeconds,
            int maxBytes = DefaultMaxBytes,
            int maxDurationMs = DefaultMaxDurationMs)
        {
            if (servers == null || servers.Count == 0)
                return new List<SpeedTestResult>();

            var xrayPath = XrayBootstrapper.ResolveExistingXrayPath();
            if (!File.Exists(xrayPath))
                return servers.Select(s => new SpeedTestResult { Server = s, SpeedKbps = 0, BytesDownloaded = 0 }).ToList();

            var entries = AllocateProxyPorts(servers);
            var configJson = XrayService.GenerateHttpProxyBatchTestConfig(entries
                .Select(e => (e.Server.BeanJson ?? "", e.HttpPort, e.InboundTag, e.OutboundTag, e.ProxyUser, e.ProxyPass))
                .ToList());

            var cfgPath = Path.Combine(Path.GetTempPath(), $"xray_speed_batch_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(cfgPath, configJson).ConfigureAwait(false);

            Process? proc = null;
            try
            {
                proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = xrayPath,
                        Arguments = $"run -c \"{cfgPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false
                    }
                };

                proc.Start();

                using var overallCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, timeoutSeconds * 4)));

                // Wait for xray to bind its local ports (no socket exceptions in debugger output).
                if (!WaitForLocalPorts(entries.Select(e => e.HttpPort).ToList(), timeoutMs: 1200, overallCts.Token))
                {
                    return servers.Select(s => new SpeedTestResult { Server = s, SpeedKbps = 0, BytesDownloaded = 0 }).ToList();
                }

                if (proc.HasExited)
                    return servers.Select(s => new SpeedTestResult { Server = s, SpeedKbps = 0, BytesDownloaded = 0 }).ToList();

                var results = new List<SpeedTestResult>(servers.Count);
                var sem = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));

                var tasks = entries.Select(async e =>
                {
                    await sem.WaitAsync(overallCts.Token).ConfigureAwait(false);
                    try
                    {
                        var (status, speedKbps, bytes, error) = await DownloadThroughHttpProxy(
                            e.HttpPort,
                            e.ProxyUser,
                            e.ProxyPass,
                            timeoutSeconds,
                            maxBytes,
                            maxDurationMs,
                            overallCts.Token).ConfigureAwait(false);
                        lock (results) results.Add(new SpeedTestResult { Server = e.Server, SpeedKbps = speedKbps, BytesDownloaded = bytes, Status = status, Error = error });
                    }
                    catch
                    {
                        lock (results) results.Add(new SpeedTestResult { Server = e.Server, SpeedKbps = 0, BytesDownloaded = 0, Status = SpeedTestStatus.Error, Error = "error" });
                    }
                    finally
                    {
                        sem.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks).ConfigureAwait(false);
                return results.OrderByDescending(r => r.SpeedKbps).ToList();
            }
            finally
            {
                try
                {
                    if (proc != null && !proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        try
                        {
                            var wait = proc.WaitForExitAsync();
                            await Task.WhenAny(wait, Task.Delay(800)).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
                catch { }

                try { if (File.Exists(cfgPath)) File.Delete(cfgPath); } catch { }
            }
        }

        private static async Task<(SpeedTestStatus Status, long SpeedKbps, long BytesDownloaded, string? Error)> DownloadThroughHttpProxy(
            int httpPort,
            string proxyUser,
            string proxyPass,
            int timeoutSeconds,
            int maxBytes,
            int maxDurationMs,
            CancellationToken overallToken)
        {
            // Cloudflare endpoint returns a stream of N bytes. Good for speed measurements.
            var url = $"https://speed.cloudflare.com/__down?bytes={maxBytes}";

            var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{httpPort}")
                {
                    Credentials = new NetworkCredential(proxyUser, proxyPass)
                },
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromMilliseconds(DefaultConnectTimeoutMs)
            };

            using var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(overallToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, timeoutSeconds)));

            var sw = Stopwatch.StartNew();
            long total = 0;

            try
            {
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                var buffer = new byte[64 * 1024];

                while (total < maxBytes && !cts.IsCancellationRequested && sw.ElapsedMilliseconds < maxDurationMs)
                {
                    var toRead = Math.Min(buffer.Length, maxBytes - (int)total);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cts.Token).ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    total += read;
                }
            }
            catch (OperationCanceledException)
            {
                // timeout
                sw.Stop();
                return (SpeedTestStatus.Timeout, 0, 0, "timeout");
            }
            catch
            {
                // ignore
            }

            sw.Stop();
            var seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
            var kbps = (long)Math.Round((total / 1024.0) / seconds);
            if (kbps > 0 && total > 64 * 1024)
                return (SpeedTestStatus.Ok, Math.Max(0, kbps), Math.Max(0, total), null);

            if (cts.IsCancellationRequested)
                return (SpeedTestStatus.Timeout, 0, 0, "timeout");

            return (SpeedTestStatus.Error, 0, 0, "no-data");
        }

        private static List<PortEntry> AllocateProxyPorts(List<ProxyEntity> servers)
        {
            var list = new List<PortEntry>(servers.Count);
            var used = new HashSet<int>();
            var startPort = 18080;

            for (var i = 0; i < servers.Count; i++)
            {
                var port = startPort + i;
                for (var tries = 0; tries < 200; tries++)
                {
                    if (!used.Contains(port) && PortHelper.IsPortFree(port))
                        break;
                    port++;
                }

                used.Add(port);
                list.Add(new PortEntry(
                    Server: servers[i],
                    HttpPort: port,
                    InboundTag: $"http-in-{i}",
                    OutboundTag: $"proxy-{i}",
                    ProxyUser: $"u{i}",
                    ProxyPass: Guid.NewGuid().ToString("N")));
            }

            return list;
        }

        private static class PortHelper
        {
            public static bool IsPortFree(int port)
            {
                try
                {
                    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static bool WaitForLocalPorts(List<int> ports, int timeoutMs, CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            var ipProps = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();

            while (sw.ElapsedMilliseconds < timeoutMs && !token.IsCancellationRequested)
            {
                try
                {
                    var listeners = ipProps.GetActiveTcpListeners();
                    var open = 0;
                    foreach (var p in ports)
                    {
                        if (listeners.Any(l => IPAddress.IsLoopback(l.Address) && l.Port == p))
                            open++;
                    }

                    if (open == ports.Count)
                        return true;
                }
                catch
                {
                    // ignore
                }

                try { Task.Delay(40, token).Wait(token); } catch { }
            }

            return false;
        }

        private sealed record PortEntry(ProxyEntity Server, int HttpPort, string InboundTag, string OutboundTag, string ProxyUser, string ProxyPass);
    }

    public class SpeedTestResult
    {
        public ProxyEntity? Server { get; set; }
        public long SpeedKbps { get; set; }
        public long BytesDownloaded { get; set; }
        public SpeedTestStatus Status { get; set; }
        public string? Error { get; set; }
    }

    public enum SpeedTestStatus
    {
        Unknown = 0,
        Ok = 1,
        Timeout = 2,
        Error = 3
    }
}
