using ByeWhiteList.Windows.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ByeWhiteList.Windows.Services
{
    public class FastUrlTester
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private const int HTTP_TIMEOUT_MS = 3000;

        public static async Task<int> TestHttpAsync(ProxyEntity server)
        {
            if (server == null)
                return -1;

            try
            {
                string testUrl = "https://www.gstatic.com/generate_204";

                using var cts = new CancellationTokenSource(HTTP_TIMEOUT_MS);
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMilliseconds(HTTP_TIMEOUT_MS);

                var request = new HttpRequestMessage(HttpMethod.Head, testUrl);
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    var response = await client.SendAsync(request, cts.Token);
                    stopwatch.Stop();

                    if (response.IsSuccessStatusCode)
                    {
                        var elapsed = stopwatch.ElapsedMilliseconds;
                        if (elapsed > 0 && elapsed < 10000)
                            return (int)elapsed;
                    }
                    return -1;
                }
                catch (TaskCanceledException)
                {
                    return -1;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"HTTP test error: {ex.Message}");
                    return -1;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HTTP test error for {server.DisplayName()}: {ex.Message}");
                return -1;
            }
        }

        public static async Task<List<ServerPingResult>> TestProfilesParallel(
            List<ProxyEntity>? servers,
            int concurrentTests = 10,
            int timeoutMs = 3000)
        {
            if (servers == null || servers.Count == 0)
                return new List<ServerPingResult>();

            var results = new List<ServerPingResult>();
            var semaphore = new SemaphoreSlim(concurrentTests);

            var tasks = servers.Select(async server =>
            {
                await semaphore.WaitAsync();
                try
                {
                    if (server != null)
                    {
                        int ping = await TestHttpWithTimeout(server, timeoutMs);
                        lock (results)
                        {
                            results.Add(new ServerPingResult { Server = server, Ping = ping });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Test error: {ex.Message}");
                    lock (results)
                    {
                        results.Add(new ServerPingResult { Server = server, Ping = -1 });
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            return results.OrderBy(r => r.Ping > 0 ? r.Ping : int.MaxValue).ToList();
        }

        private static async Task<int> TestHttpWithTimeout(ProxyEntity server, int timeoutMs)
        {
            if (server == null) return -1;

            try
            {
                var task = TestHttpAsync(server);
                var timeoutTask = Task.Delay(timeoutMs);
                var completedTask = await Task.WhenAny(task, timeoutTask);

                if (completedTask == task)
                {
                    var result = await task;
                    if (result > 0 && result < 10000)
                        return result;
                }
                return -1;
            }
            catch
            {
                return -1;
            }
        }

        public static async Task TestServersPingsAsync(List<ProxyEntity>? servers, Action<ProxyEntity, int>? onPingComplete)
        {
            if (servers == null || onPingComplete == null) return;

            var results = await TestProfilesParallel(servers, 10, 3000);
            foreach (var result in results)
            {
                if (result.Server != null)
                {
                    onPingComplete.Invoke(result.Server, result.Ping);
                }
            }
        }
    }

    public class ServerPingResult
    {
        public ProxyEntity? Server { get; set; }
        public int Ping { get; set; }
    }
}