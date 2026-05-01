using ByeWhiteList.Windows.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ByeWhiteList.Windows.Services
{
    public enum SpeedTestStage
    {
        Starting,
        Ping,
        Download,
        Upload,
        Finished
    }

    public sealed record SpeedTestProgress(SpeedTestStage Stage, double Percent, double? PingMs, double? DownloadMbps, double? UploadMbps);

    public sealed record SpeedTestSummary(double? PingMs, double? DownloadMbps, double? UploadMbps);

    public static class SpeedTestRunner
    {
        private enum SpeedTestEndpointKind
        {
            Cloudflare,
            LibreSpeed
        }

        private sealed record SpeedTestEndpoint(
            string Name,
            SpeedTestEndpointKind Kind,
            Uri PingUri,
            Func<long, Uri> DownloadUri,
            Uri UploadUri);

        private sealed record ThroughputResult(double? Mbps, long Bytes, string? Error);

        public static Task<SpeedTestSummary> RunDirectAsync(IProgress<SpeedTestProgress> progress, CancellationToken token)
        {
            return RunInternalAsync(
                createHandler: () => CreateDirectHandler(),
                progress: progress,
                token: token);
        }

        public static async Task<SpeedTestSummary> RunAsync(ProxyEntity server, IProgress<SpeedTestProgress> progress, CancellationToken token)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));

            progress?.Report(new SpeedTestProgress(SpeedTestStage.Starting, 0, null, null, null));

            var xrayPath = XrayBootstrapper.ResolveExistingXrayPath();
            if (!File.Exists(xrayPath))
                throw new FileNotFoundException("xray.exe не найден", xrayPath);

            if (string.IsNullOrWhiteSpace(server.BeanJson))
                throw new InvalidOperationException("Сервер не содержит конфиг (BeanJson пуст)");

            // Use a wide high-port range to reduce chance of conflicts and avoid "well-known proxy ports".
            var httpPort = FindFreePort(30000, 60999);
            var inboundTag = "http-in-0";
            var outboundTag = "proxy-0";

            // IMPORTANT:
            // Some .NET HTTP proxy stacks don't reliably send Proxy-Authorization on CONNECT in all cases.
            // For now VGSpeed uses an ephemeral, loopback-only proxy without auth (port is randomized and lives briefly).
            var proxyUser = "";
            var proxyPass = "";

            var configJson = XrayService.GenerateHttpProxyBatchTestConfig(new List<(string BeanJson, int HttpPort, string InboundTag, string OutboundTag, string ProxyUser, string ProxyPass)>
            {
                (server.BeanJson ?? "", httpPort, inboundTag, outboundTag, proxyUser, proxyPass)
            });

            var cfgPath = Path.Combine(Path.GetTempPath(), $"xray_speed_ui_{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(cfgPath, configJson, token).ConfigureAwait(false);

            Process? proc = null;
            var lastXrayLines = new Queue<string>(capacity: 80);
            try
            {
                GeoDataUpdater.EnsureGeoDataFilesExistFromBundledAssets();

                proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = xrayPath,
                        Arguments = $"run -c \"{cfgPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        WorkingDirectory = GeoDataUpdater.GetGeoDataDir()
                    }
                };

                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (lastXrayLines)
                    {
                        if (lastXrayLines.Count >= 80) lastXrayLines.Dequeue();
                        lastXrayLines.Enqueue(e.Data);
                    }
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (lastXrayLines)
                    {
                        if (lastXrayLines.Count >= 80) lastXrayLines.Dequeue();
                        lastXrayLines.Enqueue(e.Data);
                    }
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                overallCts.CancelAfter(TimeSpan.FromSeconds(90));

                if (!WaitForLocalPort(httpPort, timeoutMs: 6500, overallCts.Token))
                    throw new InvalidOperationException("xray не открыл локальный HTTP-прокси для теста");

                if (proc.HasExited)
                    throw new InvalidOperationException("xray завершился до старта теста");

                return await RunInternalAsync(
                    createHandler: () => CreateProxyHandler(httpPort, proxyUser, proxyPass),
                    progress: progress,
                    token: overallCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    string[] lines;
                    lock (lastXrayLines) { lines = lastXrayLines.ToArray(); }
                    if (lines.Length > 0)
                    {
                        var tail = string.Join("\n", lines.TakeLast(20));
                        throw new InvalidOperationException(ex.Message + "\n\nXray log:\n" + tail, ex);
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch { }

                throw;
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
                            await Task.WhenAny(wait, Task.Delay(800, CancellationToken.None)).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
                catch { }

                try { if (File.Exists(cfgPath)) File.Delete(cfgPath); } catch { }
            }
        }

        private static async Task<SpeedTestSummary> RunInternalAsync(Func<SocketsHttpHandler> createHandler, IProgress<SpeedTestProgress> progress, CancellationToken token)
        {
            var candidates = await SelectEndpointCandidatesAsync(createHandler, token).ConfigureAwait(false);
            if (candidates.Count == 0)
                candidates.AddRange(BuildEndpointsFromBaseUri(new Uri("https://speed.cloudflare.com"), "Cloudflare"));

            var errors = new List<string>();

            foreach (var endpoint in candidates.Take(6))
            {
                try
                {
                    // Inform UI which endpoint is being used (helps debugging VPN-specific blocks).
                    try { progress?.Report(new SpeedTestProgress(SpeedTestStage.Starting, 0, null, null, null)); } catch { }

                    progress?.Report(new SpeedTestProgress(SpeedTestStage.Ping, 0, null, null, null));
                    var pingSamples = new List<double>(capacity: 3);
                    for (var i = 0; i < 3; i++)
                    {
                        try
                        {
                            var sample = await MeasurePingMs(endpoint, createHandler, token).ConfigureAwait(false);
                            if (sample.HasValue)
                                pingSamples.Add(sample.Value);
                        }
                        catch { }

                        var currentPing = pingSamples.Count == 0 ? (double?)null : pingSamples.OrderBy(x => x).ElementAt(pingSamples.Count / 2);
                        progress?.Report(new SpeedTestProgress(SpeedTestStage.Ping, Math.Clamp(((i + 1) * 100.0) / 3.0, 0, 100), currentPing, null, null));
                    }

                    var pingMs = pingSamples.Count == 0 ? (double?)null : pingSamples.OrderBy(x => x).ElementAt(pingSamples.Count / 2);

                    // Download
                    progress?.Report(new SpeedTestProgress(SpeedTestStage.Download, 0, pingMs, null, null));
                    var down = await MeasureDownloadMbps(
                        endpoint,
                        createHandler,
                        maxDurationMs: 12000,
                        (p, mbps) => progress?.Report(new SpeedTestProgress(SpeedTestStage.Download, p, pingMs, mbps, null)),
                        token).ConfigureAwait(false);

                    progress?.Report(new SpeedTestProgress(SpeedTestStage.Download, 100, pingMs, down.Mbps, null));
                    if (down.Mbps == null)
                        throw new InvalidOperationException($"Download тест не удался (bytes={down.Bytes}, error={down.Error ?? "нет данных"})");

                    // Upload
                    progress?.Report(new SpeedTestProgress(SpeedTestStage.Upload, 0, pingMs, down.Mbps, null));
                    var up = await TryMeasureUploadMbps(
                        endpoint,
                        createHandler,
                        maxDurationMs: 12000,
                        (p, mbps) => progress?.Report(new SpeedTestProgress(SpeedTestStage.Upload, p, pingMs, down.Mbps, mbps)),
                        token).ConfigureAwait(false);

                    progress?.Report(new SpeedTestProgress(SpeedTestStage.Finished, 100, pingMs, down.Mbps, up.Mbps));
                    if (up.Mbps == null)
                        throw new InvalidOperationException($"Upload тест не удался (bytes={up.Bytes}, error={up.Error ?? "нет данных"})");

                    return new SpeedTestSummary(pingMs, down.Mbps, up.Mbps);
                }
                catch (Exception ex)
                {
                    errors.Add($"{endpoint.Name}: {ex.Message}");
                    // Try next endpoint candidate.
                }
            }

            throw new InvalidOperationException("SpeedTest не удался. " + string.Join(" | ", errors.Take(6)));
        }

        private static int FindFreePort(int start, int end)
        {
            for (var p = start; p <= end; p++)
            {
                try
                {
                    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, p);
                    listener.Start();
                    listener.Stop();
                    return p;
                }
                catch { }
            }
            throw new InvalidOperationException("Не удалось подобрать свободный порт для speedtest");
        }

        private static bool WaitForLocalPort(int port, int timeoutMs, CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            var ipProps = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();

            while (sw.ElapsedMilliseconds < timeoutMs && !token.IsCancellationRequested)
            {
                try
                {
                    var listeners = ipProps.GetActiveTcpListeners();
                    if (listeners.Any(l => IPAddress.IsLoopback(l.Address) && l.Port == port))
                        return true;
                }
                catch { }

                Thread.Sleep(50);
            }

            return false;
        }

        private static SocketsHttpHandler CreateDirectHandler()
        {
            return new SocketsHttpHandler
            {
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromMilliseconds(4000),
                MaxConnectionsPerServer = 64
            };
        }

        private static SocketsHttpHandler CreateProxyHandler(int httpPort, string proxyUser, string proxyPass)
        {
            var proxy = new WebProxy($"http://127.0.0.1:{httpPort}");
            if (!string.IsNullOrWhiteSpace(proxyUser) && !string.IsNullOrWhiteSpace(proxyPass))
                proxy.Credentials = new NetworkCredential(proxyUser, proxyPass);

            return new SocketsHttpHandler
            {
                Proxy = proxy,
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromMilliseconds(6000),
                MaxConnectionsPerServer = 64
            };
        }

        private static async Task<double?> MeasurePingMs(SpeedTestEndpoint endpoint, Func<SocketsHttpHandler> createHandler, CancellationToken token)
        {
            using var handler = createHandler();
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            var sw = Stopwatch.StartNew();

            using var req = BuildRequest(AppendNoCache(endpoint.PingUri).ToString());
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                var buf = new byte[1];
                await stream.ReadAsync(buf.AsMemory(0, 1), cts.Token).ConfigureAwait(false);
            }
            catch { }

            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        private static async Task<ThroughputResult> MeasureDownloadMbps(
            SpeedTestEndpoint endpoint,
            Func<SocketsHttpHandler> createHandler,
            int maxDurationMs,
            Action<double, double?>? progress,
            CancellationToken token)
        {
            var streams = 4;
            var minChunkBytes = 1 * 1024 * 1024;
            var maxChunkBytes = 32 * 1024 * 1024;
            var currentChunkBytes = minChunkBytes;

            using var handler = createHandler();
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(maxDurationMs + 8000));

            var sw = Stopwatch.StartNew();
            long totalBytes = 0;
            string? lastError = null;

            async Task WorkerAsync()
            {
                var buffer = new byte[64 * 1024];
                while (!cts.IsCancellationRequested && sw.ElapsedMilliseconds < maxDurationMs)
                {
                    var chunk = Volatile.Read(ref currentChunkBytes);
                    var url = endpoint.DownloadUri(chunk);

                    var reqSw = Stopwatch.StartNew();
                    try
                    {
                        using var req = BuildRequest(AppendNoCache(url).ToString());
                        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            lastError = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                            continue;
                        }

                        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                        long local = 0;
                        while (!cts.IsCancellationRequested && sw.ElapsedMilliseconds < maxDurationMs)
                        {
                            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token).ConfigureAwait(false);
                            if (read <= 0) break;
                            local += read;
                            Interlocked.Add(ref totalBytes, read);
                        }

                        reqSw.Stop();
                        if (local > 256 * 1024)
                        {
                            var ms = Math.Max(1, reqSw.ElapsedMilliseconds);
                            if (ms < 650 && chunk < maxChunkBytes)
                                Interlocked.Exchange(ref currentChunkBytes, Math.Min(maxChunkBytes, chunk * 2));
                            else if (ms > 1700 && chunk > minChunkBytes)
                                Interlocked.Exchange(ref currentChunkBytes, Math.Max(minChunkBytes, chunk / 2));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = TruncateError(ex);
                    }
                }
            }

            var tasks = Enumerable.Range(0, streams).Select(_ => Task.Run(WorkerAsync, cts.Token)).ToList();

            long lastBytes = 0;
            var lastMs = 0L;
            double smooth = 0;

            while (!cts.IsCancellationRequested && sw.ElapsedMilliseconds < maxDurationMs)
            {
                try
                {
                    await Task.Delay(120, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                var nowMs = sw.ElapsedMilliseconds;
                var bytesNow = Interlocked.Read(ref totalBytes);
                var deltaBytes = bytesNow - lastBytes;
                var deltaMs = Math.Max(1, nowMs - lastMs);

                lastBytes = bytesNow;
                lastMs = nowMs;

                var instMbps = (deltaBytes * 8.0) / (deltaMs / 1000.0) / 1_000_000.0;
                if (instMbps < 0) instMbps = 0;
                smooth = smooth <= 0 ? instMbps : (smooth * 0.82 + instMbps * 0.18);

                var p = Math.Clamp((nowMs * 100.0) / Math.Max(1, maxDurationMs), 0, 99.9);
                progress?.Invoke(p, bytesNow > 64 * 1024 ? smooth : null);
            }

            try { cts.Cancel(); } catch { }
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }

            sw.Stop();
            var seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
            var mbps = (Interlocked.Read(ref totalBytes) * 8.0) / seconds / 1_000_000.0;
            if (totalBytes > 256 * 1024 && mbps > 0)
                return new ThroughputResult(mbps, totalBytes, null);

            return new ThroughputResult(null, totalBytes, lastError ?? "нет данных");
        }

        private static async Task<ThroughputResult> TryMeasureUploadMbps(
            SpeedTestEndpoint endpoint,
            Func<SocketsHttpHandler> createHandler,
            int maxDurationMs,
            Action<double, double?>? progress,
            CancellationToken token)
        {
            using var handler = createHandler();
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(maxDurationMs + 8000));

            var sw = Stopwatch.StartNew();
            var workers = 3;
            var minChunkBytes = 256 * 1024;
            var maxChunkBytes = 4 * 1024 * 1024;
            var currentChunkBytes = minChunkBytes;
            long uploadedBytes = 0;
            string? lastError = null;

            async Task WorkerAsync()
            {
                while (!cts.IsCancellationRequested && sw.ElapsedMilliseconds < maxDurationMs)
                {
                    var size = Volatile.Read(ref currentChunkBytes);
                    var payload = new byte[size];
                    Random.Shared.NextBytes(payload);

                    try
                    {
                        using var req = BuildRequest(AppendNoCache(endpoint.UploadUri).ToString(), method: HttpMethod.Post);
                        req.Content = new ByteArrayContent(payload);
                        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            lastError = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                            continue;
                        }

                        Interlocked.Add(ref uploadedBytes, size);
                        if (size < maxChunkBytes)
                            Interlocked.Exchange(ref currentChunkBytes, Math.Min(maxChunkBytes, size * 2));
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = TruncateError(ex);
                        if (size > minChunkBytes)
                            Interlocked.Exchange(ref currentChunkBytes, Math.Max(minChunkBytes, size / 2));
                    }
                }
            }

            var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(WorkerAsync, cts.Token)).ToList();

            long lastBytes = 0;
            var lastMs = 0L;
            double smooth = 0;

            while (!cts.IsCancellationRequested && sw.ElapsedMilliseconds < maxDurationMs)
            {
                try
                {
                    await Task.Delay(140, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                var nowMs = sw.ElapsedMilliseconds;
                var bytesNow = Interlocked.Read(ref uploadedBytes);
                var deltaBytes = bytesNow - lastBytes;
                var deltaMs = Math.Max(1, nowMs - lastMs);

                lastBytes = bytesNow;
                lastMs = nowMs;

                var instMbps = (deltaBytes * 8.0) / (deltaMs / 1000.0) / 1_000_000.0;
                if (instMbps < 0) instMbps = 0;
                smooth = smooth <= 0 ? instMbps : (smooth * 0.82 + instMbps * 0.18);

                var p = Math.Clamp((nowMs * 100.0) / Math.Max(1, maxDurationMs), 0, 99.9);
                progress?.Invoke(p, bytesNow > 64 * 1024 ? smooth : null);
            }

            try { cts.Cancel(); } catch { }
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }

            sw.Stop();
            progress?.Invoke(100, null);

            var seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
            var mbps = (Interlocked.Read(ref uploadedBytes) * 8.0) / seconds / 1_000_000.0;
            if (uploadedBytes > 256 * 1024 && mbps > 0)
                return new ThroughputResult(mbps, uploadedBytes, null);

            return new ThroughputResult(null, uploadedBytes, lastError ?? "нет данных");
        }

        private static string TruncateError(Exception ex)
        {
            try
            {
                var s = ex.GetBaseException().ToString();
                if (s.Length <= 1200) return s;
                return s.Substring(0, 1200) + "...";
            }
            catch
            {
                return ex.Message;
            }
        }

        private static Uri AppendNoCache(Uri uri)
        {
            try
            {
                var b = new UriBuilder(uri);
                var add = $"_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                if (string.IsNullOrWhiteSpace(b.Query) || b.Query == "?")
                    b.Query = add;
                else
                    b.Query = b.Query.TrimStart('?') + "&" + add;
                return b.Uri;
            }
            catch
            {
                return uri;
            }
        }

        private static async Task<List<SpeedTestEndpoint>> SelectEndpointCandidatesAsync(Func<SocketsHttpHandler> createHandler, CancellationToken token)
        {
            var endpoints = new List<SpeedTestEndpoint>();

            // 1) From settings
            var raw = global::ByeWhiteList.Windows.AppSettings.Instance.SpeedTestEndpoints ?? "";
            foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var s = line.Trim();
                if (s.Length == 0) continue;
                if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) continue;
                endpoints.AddRange(BuildEndpointsFromBaseUri(uri, nameHint: uri.Host));
            }

            // 2) From LibreSpeed public backend servers list (best-effort)
            try
            {
                endpoints.AddRange(await TryFetchLibreSpeedPublicServersAsync(createHandler, token).ConfigureAwait(false));
            }
            catch { }

            endpoints = endpoints
                .GroupBy(e => e.PingUri.ToString(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(24)
                .ToList();

            if (endpoints.Count == 0)
                endpoints.AddRange(BuildEndpointsFromBaseUri(new Uri("https://speed.cloudflare.com"), "Cloudflare"));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(1800));

            var probeTasks = endpoints.Select(async ep =>
            {
                try
                {
                    var ms = await MeasurePingMs(ep, createHandler, cts.Token).ConfigureAwait(false);
                    return (ep, pingMs: ms);
                }
                catch
                {
                    return (ep, pingMs: (double?)null);
                }
            }).ToList();

            // User requirement: probes in parallel, pick the first responder fast.
            // But for reliability (VPN paths often block some hosts), we also collect a few more responders
            // for fallback without delaying the start noticeably.
            var sw = Stopwatch.StartNew();
            var responders = new List<(SpeedTestEndpoint ep, double pingMs)>();
            SpeedTestEndpoint? first = null;
            var firstAtMs = -1L;

            while (probeTasks.Count > 0 && sw.ElapsedMilliseconds < 1800 && !cts.IsCancellationRequested)
            {
                var done = await Task.WhenAny(probeTasks).ConfigureAwait(false);
                probeTasks.Remove(done);
                try
                {
                    var (ep, pingMs) = await done.ConfigureAwait(false);
                    if (!pingMs.HasValue)
                        continue;

                    responders.Add((ep, pingMs.Value));
                    if (first == null)
                    {
                        first = ep;
                        firstAtMs = sw.ElapsedMilliseconds;
                    }

                    // After first success, keep collecting for a short grace window for fallback.
                    if (first != null && sw.ElapsedMilliseconds - firstAtMs >= 700)
                        break;

                    if (responders.Count >= 8)
                        break;
                }
                catch { }
            }

            if (first != null)
            {
                // Order by ping for fallbacks, but keep the first responder at the top as requested.
                var ordered = responders
                    .OrderBy(r => r.pingMs)
                    .Select(r => r.ep)
                    .Distinct()
                    .ToList();

                ordered.Remove(first);
                ordered.Insert(0, first);
                return ordered;
            }

            // No ping responders: return a small deterministic fallback list.
            return endpoints.Take(6).ToList();
        }

        private static IEnumerable<SpeedTestEndpoint> BuildEndpointsFromBaseUri(Uri baseUri, string nameHint)
        {
            if (!baseUri.AbsoluteUri.EndsWith("/"))
                baseUri = new Uri(baseUri.AbsoluteUri + "/");

            if (baseUri.Host.Contains("speed.cloudflare.com", StringComparison.OrdinalIgnoreCase))
            {
                var ping = new Uri(baseUri, "__down?bytes=1");
                var up = new Uri(baseUri, "__up");
                yield return new SpeedTestEndpoint(
                    Name: $"Cloudflare ({nameHint})",
                    Kind: SpeedTestEndpointKind.Cloudflare,
                    PingUri: ping,
                    DownloadUri: bytes => new Uri(baseUri, $"__down?bytes={Math.Max(1, bytes)}"),
                    UploadUri: up);
                yield break;
            }

            // For arbitrary bases we try BOTH styles:
            // - Cloudflare-style (__down/__up)
            // - LibreSpeed-style (backend/empty.php + backend/garbage.php)
            // Selection logic will pick the first responding endpoint.
            yield return new SpeedTestEndpoint(
                Name: $"Cloudflare-style ({nameHint})",
                Kind: SpeedTestEndpointKind.Cloudflare,
                PingUri: new Uri(baseUri, "__down?bytes=1"),
                DownloadUri: bytes => new Uri(baseUri, $"__down?bytes={Math.Max(1, bytes)}"),
                UploadUri: new Uri(baseUri, "__up"));

            yield return new SpeedTestEndpoint(
                Name: $"LibreSpeed ({nameHint})",
                Kind: SpeedTestEndpointKind.LibreSpeed,
                PingUri: new Uri(baseUri, "backend/empty.php"),
                DownloadUri: bytes =>
                {
                    var chunks = (int)Math.Clamp(Math.Round(bytes / (1024.0 * 1024.0)), 1, 128);
                    return new Uri(baseUri, $"backend/garbage.php?ckSize={chunks}");
                },
                UploadUri: new Uri(baseUri, "backend/empty.php"));
        }

        private static async Task<List<SpeedTestEndpoint>> TryFetchLibreSpeedPublicServersAsync(Func<SocketsHttpHandler> createHandler, CancellationToken token)
        {
            var listUrl = new Uri("https://librespeed.org/backend-servers/servers.php");
            using var handler = createHandler();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };

            using var resp = await client.GetAsync(listUrl, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new List<SpeedTestEndpoint>();

            var json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return new List<SpeedTestEndpoint>();

            var list = new List<SpeedTestEndpoint>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                try
                {
                    var name = el.TryGetProperty("name", out var n) ? (n.GetString() ?? "server") : "server";
                    var server = el.TryGetProperty("server", out var s) ? s.GetString() : null;
                    var pingURL = el.TryGetProperty("pingURL", out var p) ? p.GetString() : null;
                    var dlURL = el.TryGetProperty("dlURL", out var d) ? d.GetString() : null;
                    var ulURL = el.TryGetProperty("ulURL", out var u) ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(pingURL) || string.IsNullOrWhiteSpace(dlURL) || string.IsNullOrWhiteSpace(ulURL))
                        continue;

                    if (!Uri.TryCreate(server, UriKind.Absolute, out var serverUri))
                        continue;
                    if (!serverUri.AbsoluteUri.EndsWith("/"))
                        serverUri = new Uri(serverUri.AbsoluteUri + "/");

                    var pingUri = new Uri(serverUri, pingURL);
                    var uploadUri = new Uri(serverUri, ulURL);

                    list.Add(new SpeedTestEndpoint(
                        Name: $"LibreSpeed: {name}",
                        Kind: SpeedTestEndpointKind.LibreSpeed,
                        PingUri: pingUri,
                        DownloadUri: bytes =>
                        {
                            var chunks = (int)Math.Clamp(Math.Round(bytes / (1024.0 * 1024.0)), 1, 128);
                            var dl = new Uri(serverUri, dlURL);
                            var b = new UriBuilder(dl);
                            var add = $"ckSize={chunks}";
                            if (string.IsNullOrWhiteSpace(b.Query) || b.Query == "?")
                                b.Query = add;
                            else
                                b.Query = b.Query.TrimStart('?') + "&" + add;
                            return b.Uri;
                        },
                        UploadUri: uploadUri));
                }
                catch
                {
                    // ignore broken entry
                }

                if (list.Count >= 18) break;
            }

            return list;
        }

        private static HttpRequestMessage BuildRequest(string url, HttpMethod? method = null)
        {
            var req = new HttpRequestMessage(method ?? HttpMethod.Get, url);

            // Proxy + HTTP/2 can be flaky on some networks/servers; LibreSpeed works fine over HTTP/1.1.
            req.Version = HttpVersion.Version11;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("ByeWhiteList-VPN", "1.0"));
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true, MaxAge = TimeSpan.Zero };
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

            return req;
        }
    }
}
