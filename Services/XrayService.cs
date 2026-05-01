using ByeWhiteList.Windows.Models;
using Newtonsoft.Json.Linq;
using ByeWhiteList.Windows.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ByeWhiteList.Windows.Services
{
    public class XrayService
    {
        private static readonly ConcurrentDictionary<int, byte> _startedXrayPids = new();
        private readonly SemaphoreSlim _lifecycleLock = new SemaphoreSlim(1, 1);
        private bool _elevationVerifiedForSession;
        private Process? _xrayProcess;
        private string? _configPath;
        private bool _isRunning;
        private ProxyEntity? _currentServer;
        private CancellationTokenSource? _speedCts;
        private CancellationTokenSource? _connectivityCheckCts;
        private int _socksPort = 10808;
        private bool _routesApplied;
        private readonly List<IPAddress> _serverBypassIps = new();
        private bool _serverBypassApplied;
        // Legacy/optional: OS-level bypass routes. Not used in the karing-like geosite/geoip routing,
        // but kept for backward compatibility (currently not invoked anywhere).
        private readonly List<(string Destination, string Mask)> _whitelistBypassRoutes = new();
        // We may apply a reduced/aggregated set of whitelist routes (to keep Windows stable).
        // Track what we actually add so we can remove it quickly on disconnect.
        private readonly List<(string Destination, string Mask)> _whitelistBypassAppliedRoutes = new();
        private bool _whitelistBypassApplied;
        private bool _dnsOverrideApplied;
        private string? _dnsOverrideInterfaceName;
        private bool _dnsOriginalWasDhcp;
        private readonly List<IPAddress> _dnsOriginalServersV4 = new();

        public event Action<bool>? OnConnectionStateChanged;
        public event Action<string>? OnLogMessage;
        public event Action<string, string>? OnSpeedUpdate;

        public bool IsRunning => _isRunning;
        public ProxyEntity? CurrentServer => _currentServer;

        public async Task<bool> EnsureElevationForVpnAsync()
        {
            if (_elevationVerifiedForSession || IsAdministrator())
                return true;

            try
            {
                LogManager.Add("🔐 Для настройки VPN нужны права администратора...");
                await RunElevatedRouteCommand("/c echo ByeWhiteList elevation check >nul");
                _elevationVerifiedForSession = true;
                LogManager.Add("✅ Права администратора подтверждены.");
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                LogManager.Add("❌ Запрос прав администратора отклонён.");
                return false;
            }
            catch (Exception ex)
            {
                LogManager.Add($"❌ Не удалось получить права администратора: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> Start(ProxyEntity server)
        {
            await _lifecycleLock.WaitAsync();
            try
            {
                return await StartInternal(server);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private async Task<bool> StartInternal(ProxyEntity server)
        {
            LogManager.Add($"========== START XRAY ==========");
            LogManager.Add($"Сервер: {server.DisplayName()}");

            if (_isRunning)
                await StopInternal();

            try
            {
                OnLogMessage?.Invoke($"🚀 Запуск Xray для сервера: {server.DisplayName()}");

                // Ensure geodata exists locally (fast local copy). Network update is done elsewhere (settings / startup).
                GeoDataUpdater.EnsureGeoDataFilesExistFromBundledAssets();

                var config = GenerateTunConfig(server.BeanJson ?? "");
                _configPath = Path.Combine(Path.GetTempPath(), $"xray_{Guid.NewGuid()}.json");
                await File.WriteAllTextAsync(_configPath, config);

                LogManager.Add($"📝 Конфиг сохранён: {_configPath}");

                var xrayPath = XrayBootstrapper.ResolveExistingXrayPath();
                LogManager.Add($"🔍 Путь к xray: {xrayPath}");
                LogManager.Add($"🔍 Файл существует: {File.Exists(xrayPath)}");

                if (!File.Exists(xrayPath))
                {
                    try
                    {
                        OnLogMessage?.Invoke("⏳ xray.exe не найден, пробую скачать автоматически…");
                        LogManager.Add("⏳ xray.exe не найден, пробую скачать автоматически…");

                        var installedPath = await XrayBootstrapper.DownloadLatestXrayAsync(
                            msg =>
                            {
                                try { LogManager.Add(msg); } catch { }
                                try { OnLogMessage?.Invoke(msg); } catch { }
                            },
                            CancellationToken.None);

                        xrayPath = XrayBootstrapper.ResolveExistingXrayPath();
                        LogManager.Add($"🔍 Xray после скачивания: {xrayPath}");
                        LogManager.Add($"🔍 Файл существует: {File.Exists(xrayPath)}");

                        if (!File.Exists(xrayPath))
                        {
                            OnLogMessage?.Invoke("❌ Xray не удалось скачать.");
                            LogManager.Add("❌ Xray не удалось скачать.");
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLogMessage?.Invoke($"❌ Не удалось скачать Xray: {ex.Message}");
                        LogManager.Add($"❌ Не удалось скачать Xray: {ex.Message}");
                        return false;
                    }
                }

                if (_xrayProcess != null)
                {
                    try { _xrayProcess.Dispose(); } catch { }
                    _xrayProcess = null;
                }

                _xrayProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = xrayPath,
                        Arguments = $"run -c \"{_configPath}\"",
                        WorkingDirectory = GeoDataUpdater.GetGeoDataDir(),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    },
                    EnableRaisingEvents = true
                };

                // Xray/V2Ray asset locations (geoip.dat / geosite.dat).
                // WorkingDirectory is set too, but env vars make it more robust on different builds.
                try
                {
                    _xrayProcess.StartInfo.Environment["XRAY_LOCATION_ASSET"] = GeoDataUpdater.GetGeoDataDir();
                    _xrayProcess.StartInfo.Environment["V2RAY_LOCATION_ASSET"] = GeoDataUpdater.GetGeoDataDir();
                }
                catch { }

                LogManager.Add($"🚀 Запуск процесса: {xrayPath} run -c \"{_configPath}\"");

                _xrayProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        LogManager.Add($"📢 stdout: {e.Data}");
                    }
                };

                _xrayProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        LogManager.Add($"⚠️ stderr: {e.Data}");
                    }
                };

                _xrayProcess.Exited += (s, e) =>
                {
                    LogManager.Add($"🔴 Процесс завершён, ExitCode: {_xrayProcess?.ExitCode}");
                    StopSpeedMonitor();
                    _isRunning = false;
                    _currentServer = null;
                    OnConnectionStateChanged?.Invoke(false);
                };

                bool started = _xrayProcess.Start();
                LogManager.Add($"Процесс запущен: {started}");

                if (!started)
                {
                    OnLogMessage?.Invoke($"❌ Не удалось запустить xray процесс");
                    return false;
                }

                _xrayProcess.BeginOutputReadLine();
                _xrayProcess.BeginErrorReadLine();
                try { _startedXrayPids[_xrayProcess.Id] = 1; } catch { }

                // Don't hard-wait here; SetupTunRouting will retry-discover the adapter.
                LogManager.Add($"⏳ Ожидание инициализации TUN...");
                await Task.Delay(300);

                if (_xrayProcess.HasExited)
                {
                    LogManager.Add($"❌ Процесс завершился сразу. ExitCode: {_xrayProcess.ExitCode}");
                    OnLogMessage?.Invoke($"❌ Xray завершился сразу после запуска (ExitCode: {_xrayProcess.ExitCode})");
                    return false;
                }

                _isRunning = true;
                _currentServer = server;

                // With sendThrough binding, we don't need to pre-resolve server IPs and add OS bypass routes.

                // Если адаптер ранее был отключен вручную/старой логикой — включаем обратно.
                await TryEnableTunAdapterIfNeededAsync();

                // Настраиваем TUN маршруты
                await SetupTunRouting();

                StartSpeedMonitor();

                OnConnectionStateChanged?.Invoke(true);
                LogManager.Add($"✅ Xray TUN успешно запущен, PID: {_xrayProcess.Id}");

                // Тест трафика — в фоне, чтобы не задерживать "подключено".
                try { _connectivityCheckCts?.Cancel(); _connectivityCheckCts?.Dispose(); } catch { }
                _connectivityCheckCts = new CancellationTokenSource();
                _ = Task.Run(() => RunConnectivityCheckInBackground(_connectivityCheckCts.Token));

                return true;
            }
            catch (Exception ex)
            {
                LogManager.Add($"❌ Исключение: {ex.Message}");
                OnLogMessage?.Invoke($"❌ Ошибка: {ex.Message}");

                // If Xray is already running but routing failed, make sure we don't leave it behind.
                // IMPORTANT: StartInternal runs under _lifecycleLock, so calling Stop() here would deadlock.
                try { await StopInternal(); } catch { }
                return false;
            }
        }

        private sealed record TrafficTestResult(bool Success, bool DnsOk, string? ErrorMessage);

        private async Task RunConnectivityCheckInBackground(CancellationToken token)
        {
            try
            {
                // Give Windows routing/TUN adapter a moment to settle.
                await Task.Delay(900, token);

                // Best-effort: wait until the TUN adapter actually has an IPv4 (often appears slightly later).
                await WaitForTunIpv4Async(TimeSpan.FromSeconds(5), token);

                var result = await TestTrafficDetailed(attempts: 3, retryDelay: TimeSpan.FromMilliseconds(900));
                if (result.Success)
                    return;

                // DNS override is expensive (netsh can take seconds). Apply it only when DNS looks actually broken.
                if (!result.DnsOk)
                {
                    try { await ApplyDnsOverrideIfNeeded(); } catch { }
                    await TestTrafficDetailed(attempts: 2, retryDelay: TimeSpan.FromMilliseconds(900));
                }
            }
            catch (OperationCanceledException)
            {
                // Normal on disconnect/exit.
            }
            catch
            {
                // never fail the connection because of background diagnostics
            }
        }

        private static async Task<bool> WaitForTunIpv4Async(TimeSpan timeout, CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (TryGetTunInterfaceIpv4(out _, out _))
                        return true;
                }
                catch
                {
                    // ignore
                }

                await Task.Delay(200, token);
            }

            return false;
        }

        private static bool TryGetTunInterfaceIpv4(out int ifIndex, out string ipv4)
        {
            NetworkInterface? best = null;
            int bestScore = -1;
            string bestIp = "";
            int bestIndex = -1;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus == OperationalStatus.Down ||
                    nic.OperationalStatus == OperationalStatus.NotPresent)
                    continue;

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = nic.GetIPProperties();
                var ipv4Props = ipProps.GetIPv4Properties();
                if (ipv4Props == null)
                    continue;

                string? candIpv4 = null;
                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = ua.Address.ToString();
                        if (!ip.StartsWith("127.", StringComparison.Ordinal))
                        {
                            candIpv4 = ip;
                            break;
                        }
                    }
                }

                // We only care about "ready" adapter with IPv4.
                if (string.IsNullOrWhiteSpace(candIpv4))
                    continue;

                int score = 0;

                if (candIpv4.StartsWith("10.0.236.", StringComparison.Ordinal))
                    score += 1000;

                if (nic.Name.Contains("byewhitelist", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("byewhitelist", StringComparison.OrdinalIgnoreCase))
                    score += 300;

                if (nic.Name.Contains("xray", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("xray", StringComparison.OrdinalIgnoreCase))
                    score += 100;
                if (nic.Name.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase))
                    score += 80;
                if (nic.Name.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("tun", StringComparison.OrdinalIgnoreCase))
                    score += 50;

                // Prefer link-local/10.x over LAN 192.168.x (we want the tunnel adapter).
                if (candIpv4.StartsWith("169.254.", StringComparison.Ordinal))
                    score += 60;
                if (candIpv4.StartsWith("10.", StringComparison.Ordinal))
                    score += 15;
                if (candIpv4.StartsWith("192.168.", StringComparison.Ordinal))
                    score -= 10;

                if (score > bestScore)
                {
                    best = nic;
                    bestScore = score;
                    bestIp = candIpv4;
                    bestIndex = ipv4Props.Index;
                }
            }

            if (best != null && bestScore >= 50)
            {
                ifIndex = bestIndex;
                ipv4 = bestIp;
                return true;
            }

            ifIndex = -1;
            ipv4 = "";
            return false;
        }

        private async Task SetupTunRouting()
        {
            try
            {
                LogManager.Add($"🔧 Настройка TUN маршрутов...");
                _routesApplied = false;
                _serverBypassApplied = false;
                _whitelistBypassApplied = false;
                _dnsOverrideApplied = false;
                _dnsOverrideInterfaceName = null;
                _dnsOriginalServersV4.Clear();

                // Split default routes (/1 + /1) so we don't delete/replace 0.0.0.0/0.
                // To make routing reliable, bind the routes to the actual TUN interface (IF index).
                // Xray's TUN adapter IPv4 may be 10.x, 172.x, or even link-local 169.254.x depending on driver/settings,
                // so we detect it by interface name/description + having an IPv4 address.
                if (!TryGetTunInterfaceWithRetry(out int ifIndex, out string tunIfIpv4))
                    throw new InvalidOperationException("TUN интерфейс не найден (нет подходящего сетевого интерфейса).");

                LogManager.Add($"🔍 TUN IF: {ifIndex}, ip: {tunIfIpv4}");

                // Prefer the gateway we assign in config (tun_address). If Windows rejects it,
                // fall back to using the interface IPv4 as next-hop.
                string preferredGateway = "10.0.236.1";

                var fallbackGateway = string.Equals(tunIfIpv4, "0.0.0.0", StringComparison.Ordinal) ? null : tunIfIpv4;
                await ApplySplitDefaultRoutes(ifIndex, preferredGateway, fallbackGateway);

                _routesApplied = true;
                LogManager.Add("✅ Маршруты через TUN добавлены: 0.0.0.0/1 и 128.0.0.0/1");

                // Optional: validate in background (never blocks connection).
                _ = Task.Run(() =>
                {
                    try
                    {
                        var ok = IsRoutePresent("0.0.0.0", "128.0.0.0") && IsRoutePresent("128.0.0.0", "128.0.0.0");
                        if (!ok)
                            LogManager.Add("⚠️ route print не подтвердил split-default маршруты (может быть медленным/нестабильным).");
                    }
                    catch { }
                });

                // DNS override is expensive (netsh can take seconds). We apply it only if we detect traffic/DNS issues
                // in the background connectivity check after "connected".
            }
            catch (Exception ex)
            {
                LogManager.Add($"❌ Ошибка настройки маршрутов: {ex.Message}");
                throw;
            }
        }

        private async Task RemoveTunRouting()
        {
            try
            {
                if (!_routesApplied)
                    return;

                LogManager.Add($"🔧 Удаление TUN маршрутов...");

                await RunElevatedRouteCommand("/c route delete 0.0.0.0 mask 128.0.0.0 & route delete 128.0.0.0 mask 128.0.0.0");

                await RestoreDnsOverrideIfNeeded();
                LogManager.Add($"✅ Маршруты через TUN удалены");
            }
            catch (Exception ex)
            {
                LogManager.Add($"⚠️ Ошибка удаления маршрутов: {ex.Message}");
            }
            finally
            {
                _routesApplied = false;
            }
        }

        private async Task ApplyDnsOverrideIfNeeded()
        {
            if (_dnsOverrideApplied)
                return;

            if (!TryGetDefaultGatewayInterface(out int ifIndex, out _))
            {
                LogManager.Add("⚠️ Не удалось определить интерфейс с default gateway, DNS override пропущен.");
                return;
            }

            NetworkInterface? nic = null;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up)
                    continue;

                var ipv4 = n.GetIPProperties().GetIPv4Properties();
                if (ipv4 == null)
                    continue;

                if (ipv4.Index == ifIndex)
                {
                    nic = n;
                    break;
                }
            }

            if (nic == null)
            {
                LogManager.Add("⚠️ Интерфейс default gateway не найден, DNS override пропущен.");
                return;
            }

            _dnsOverrideInterfaceName = nic.Name;
            _dnsOriginalServersV4.Clear();
            foreach (var a in nic.GetIPProperties().DnsAddresses)
            {
                if (a.AddressFamily == AddressFamily.InterNetwork)
                    _dnsOriginalServersV4.Add(a);
            }

            var ipv4Props = nic.GetIPProperties().GetIPv4Properties();
            _dnsOriginalWasDhcp = ipv4Props?.IsDhcpEnabled ?? true;

            // Pick "boring but reliable" public DNS servers. Using multiple improves resiliency.
            const string dns1 = "1.1.1.1";
            const string dns2 = "8.8.8.8";
            const string dns3 = "9.9.9.9";

            // Apply DNS override via netsh (admin). One elevation prompt for all operations.
            // Note: interface names can contain spaces; netsh requires quotes.
            string ifName = _dnsOverrideInterfaceName.Replace("\"", "");
            await RunElevatedRouteCommand(
                "/c " +
                $"netsh interface ip set dns name=\"{ifName}\" static {dns1} primary & " +
                $"netsh interface ip add dns name=\"{ifName}\" {dns2} index=2 & " +
                $"netsh interface ip add dns name=\"{ifName}\" {dns3} index=3");

            // Flush local resolver cache so apps immediately re-resolve via the new DNS servers.
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "ipconfig",
                    Arguments = "/flushdns",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p != null)
                    await p.WaitForExitAsync();
            }
            catch
            {
                // best-effort
            }

            _dnsOverrideApplied = true;
            LogManager.Add($"✅ DNS override применён на интерфейсе '{_dnsOverrideInterfaceName}': {dns1}, {dns2}, {dns3}");
        }

        private async Task RestoreDnsOverrideIfNeeded()
        {
            if (!_dnsOverrideApplied || string.IsNullOrWhiteSpace(_dnsOverrideInterfaceName))
                return;

            try
            {
                string ifName = _dnsOverrideInterfaceName.Replace("\"", "");

                if (_dnsOriginalWasDhcp || _dnsOriginalServersV4.Count == 0)
                {
                    await RunElevatedRouteCommand($"/c netsh interface ip set dns name=\"{ifName}\" dhcp");
                    LogManager.Add($"🧹 DNS восстановлен (DHCP) на интерфейсе '{_dnsOverrideInterfaceName}'");
                }
                else
                {
                    // Re-apply original static servers in order.
                    var cmd = new StringBuilder();
                    cmd.Append("/c ");
                    cmd.Append($"netsh interface ip set dns name=\"{ifName}\" static {_dnsOriginalServersV4[0]} primary");
                    for (int i = 1; i < _dnsOriginalServersV4.Count; i++)
                    {
                        cmd.Append(" & ");
                        cmd.Append($"netsh interface ip add dns name=\"{ifName}\" {_dnsOriginalServersV4[i]} index={i + 1}");
                    }
                    await RunElevatedRouteCommand(cmd.ToString());
                    LogManager.Add($"🧹 DNS восстановлен (static) на интерфейсе '{_dnsOverrideInterfaceName}'");
                }
            }
            catch
            {
                // best-effort
            }
            finally
            {
                _dnsOverrideApplied = false;
                _dnsOverrideInterfaceName = null;
                _dnsOriginalServersV4.Clear();
            }
        }

        private async Task ResolveServerBypassIps(ProxyEntity server)
        {
            _serverBypassIps.Clear();

            try
            {
                string? host = null;
                if (!string.IsNullOrWhiteSpace(server.BeanJson))
                {
                    var input = JObject.Parse(server.BeanJson);
                    host = input["server"]?.Value<string>();
                }

                if (string.IsNullOrWhiteSpace(host))
                    return;

                // If it's already an IP literal, no need for DNS.
                if (IPAddress.TryParse(host, out var ipLiteral))
                {
                    if (IsPublicRoutableIp(ipLiteral))
                        _serverBypassIps.Add(ipLiteral);
                    return;
                }

                IPAddress[] addrs;
                try
                {
                    addrs = (await DnsAResolver.ResolveAAsync(host, TimeSpan.FromSeconds(2))).ToArray();
                }
                catch
                {
                    // If DNS is flaky, don't hard-fail; we just won't add bypass routes.
                    return;
                }

                foreach (var a in addrs)
                {
                    if (a.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (!IsPublicRoutableIp(a))
                        continue;
                    if (!_serverBypassIps.Contains(a))
                        _serverBypassIps.Add(a);
                }

                if (_serverBypassIps.Count > 0)
                    LogManager.Add($"🧩 Bypass до сервера: {string.Join(", ", _serverBypassIps.Select(x => x.ToString()))}");
            }
            catch
            {
                // no-op
            }
        }

        private async Task ApplyWhitelistBypassRoutesIfNeeded()
        {
            if (_whitelistBypassApplied)
                return;
            if (_whitelistBypassRoutes.Count == 0)
                return;

            if (!TryGetDefaultGatewayInterface(out int ifIndex, out string gatewayIpv4))
            {
                LogManager.Add("⚠️ Не удалось определить физический default gateway, whitelist bypass маршруты не добавлены.");
                return;
            }

            // Keep it bounded; too many routes can make Windows networking unstable and UAC scripts slow.
            // Instead of taking the "first N", we try to aggregate the whitelist into fewer CIDRs first.
            const int maxRoutes = 10000;
            var routes = BuildWhitelistBypassRoutesForApply(_whitelistBypassRoutes, maxRoutes, out var reduceLog);
            if (!string.IsNullOrWhiteSpace(reduceLog))
                LogManager.Add(reduceLog);

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal enableextensions");
            sb.AppendLine("REM whitelist bypass routes (direct, outside VPN)");

            foreach (var r in routes)
            {
                // Idempotency: delete first, ignore errors.
                sb.AppendLine($"route delete {r.Destination} mask {r.Mask} >nul 2>nul");
                sb.AppendLine($"route add {r.Destination} mask {r.Mask} {gatewayIpv4} metric 1 if {ifIndex} >nul");
            }

            sb.AppendLine("exit /b 0");
            await RunElevatedCmdScript(sb.ToString(), "whitelist_bypass_apply");

            _whitelistBypassAppliedRoutes.Clear();
            _whitelistBypassAppliedRoutes.AddRange(routes);
            _whitelistBypassApplied = true;
            LogManager.Add($"✅ Добавлены whitelist bypass-маршруты: {routes.Count} (GW {gatewayIpv4}, if={ifIndex})");
        }

        private async Task RemoveWhitelistBypassRoutesIfNeeded()
        {
            if (!_whitelistBypassApplied)
                return;
            if (_whitelistBypassAppliedRoutes.Count == 0 && _whitelistBypassRoutes.Count == 0)
                return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("setlocal enableextensions");
                sb.AppendLine("REM whitelist bypass routes cleanup");

                // Only remove what we actually applied. Fallback to the raw list (best-effort)
                // for backward compatibility / edge cases.
                var routesToRemove = _whitelistBypassAppliedRoutes.Count > 0
                    ? _whitelistBypassAppliedRoutes
                    : _whitelistBypassRoutes;

                foreach (var r in routesToRemove)
                {
                    sb.AppendLine($"route delete {r.Destination} mask {r.Mask} >nul 2>nul");
                }

                sb.AppendLine("exit /b 0");
                await RunElevatedCmdScript(sb.ToString(), "whitelist_bypass_remove");

                LogManager.Add("🧹 Удалены whitelist bypass-маршруты");
            }
            catch
            {
                // best-effort
            }
            finally
            {
                _whitelistBypassApplied = false;
                _whitelistBypassAppliedRoutes.Clear();
            }
        }

        private static bool TryCidrOrIpToRoute(string cidrOrIp, out string destination, out string mask)
        {
            destination = "";
            mask = "";

            var s = (cidrOrIp ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s))
                return false;

            if (!s.Contains("/"))
            {
                if (IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    destination = s;
                    mask = "255.255.255.255";
                    return true;
                }
                return false;
            }

            var parts = s.Split('/');
            if (parts.Length != 2)
                return false;

            if (!IPAddress.TryParse(parts[0], out var baseIp) || baseIp.AddressFamily != AddressFamily.InterNetwork)
                return false;

            if (!int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > 32)
                return false;

            destination = parts[0];
            mask = PrefixToMask(prefix);
            return true;
        }

        private static string PrefixToMask(int prefix)
        {
            uint mask = prefix == 0 ? 0u : 0xffffffffu << (32 - prefix);
            var b1 = (mask >> 24) & 0xFF;
            var b2 = (mask >> 16) & 0xFF;
            var b3 = (mask >> 8) & 0xFF;
            var b4 = mask & 0xFF;
            return $"{b1}.{b2}.{b3}.{b4}";
        }

        private static List<(string Destination, string Mask)> BuildWhitelistBypassRoutesForApply(
            List<(string Destination, string Mask)> routes,
            int maxRoutes,
            out string logLine)
        {
            logLine = "";
            if (routes.Count == 0)
                return new List<(string Destination, string Mask)>();

            // 1) Exact aggregation (merge overlaps + convert to minimal CIDRs).
            var exact = AggregateRoutesToCidrs(routes);
            if (exact.Count <= maxRoutes)
            {
                if (exact.Count != routes.Count)
                    logLine = $"🧮 Суммаризация whitelist bypass routes: {routes.Count} -> {exact.Count} (точно, без расширения подсетей).";
                return exact;
            }

            // 2) If still too many, progressively coarsen / widen small prefixes.
            // This may send *some* non-whitelisted IPs direct (trade-off for stability).
            int[] coarsenTargets = new[] { 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8 };
            foreach (var targetPrefix in coarsenTargets)
            {
                var widened = CoarsenRoutesToPrefix(exact, targetPrefix);
                var widenedAgg = AggregateRoutesToCidrs(widened);
                if (widenedAgg.Count <= maxRoutes)
                {
                    logLine = $"🧮 Суммаризация whitelist bypass routes: {routes.Count} -> {widenedAgg.Count} (расширено до /{targetPrefix} для части записей).";
                    return widenedAgg;
                }
            }

            // 3) Last resort: deterministic cap after exact aggregation.
            logLine = $"⚠️ Белый список IP очень большой ({exact.Count} после суммаризации), ограничиваю до первых {maxRoutes} маршрутов direct-bypass.";
            return exact.Take(maxRoutes).ToList();
        }

        private static List<(string Destination, string Mask)> AggregateRoutesToCidrs(List<(string Destination, string Mask)> routes)
        {
            // Convert all routes to IPv4 ranges, merge overlaps/adjacent, then range->CIDR.
            var ranges = new List<(uint Start, uint End)>(routes.Count);
            foreach (var r in routes)
            {
                if (!TryParseIpv4ToUInt(r.Destination, out var ip) || !TryParseIpv4ToUInt(r.Mask, out var mask))
                    continue;

                // Normalize: destination should be network base for its mask.
                uint start = ip & mask;
                uint end = start | ~mask;
                ranges.Add((start, end));
            }

            if (ranges.Count == 0)
                return new List<(string Destination, string Mask)>();

            ranges.Sort((a, b) =>
            {
                int c = a.Start.CompareTo(b.Start);
                return c != 0 ? c : a.End.CompareTo(b.End);
            });

            var merged = new List<(uint Start, uint End)>(ranges.Count);
            var cur = ranges[0];
            for (int i = 1; i < ranges.Count; i++)
            {
                var r = ranges[i];
                if (r.Start <= cur.End + 1) // overlap or adjacent
                {
                    cur = (cur.Start, Math.Max(cur.End, r.End));
                }
                else
                {
                    merged.Add(cur);
                    cur = r;
                }
            }
            merged.Add(cur);

            var result = new List<(string Destination, string Mask)>();
            foreach (var m in merged)
            {
                foreach (var cidr in RangeToCidrs(m.Start, m.End))
                    result.Add(cidr);
            }

            // Dedup (RangeToCidrs is deterministic but coarsening may introduce dups).
            result.Sort((x, y) => string.CompareOrdinal(x.Destination + x.Mask, y.Destination + y.Mask));
            for (int i = result.Count - 1; i > 0; i--)
            {
                if (string.Equals(result[i].Destination, result[i - 1].Destination, StringComparison.Ordinal) &&
                    string.Equals(result[i].Mask, result[i - 1].Mask, StringComparison.Ordinal))
                {
                    result.RemoveAt(i);
                }
            }

            return result;
        }

        private static List<(string Destination, string Mask)> CoarsenRoutesToPrefix(
            List<(string Destination, string Mask)> routes,
            int targetPrefix)
        {
            if (routes.Count == 0)
                return new List<(string Destination, string Mask)>();
            if (targetPrefix < 0) targetPrefix = 0;
            if (targetPrefix > 32) targetPrefix = 32;

            uint targetMask = PrefixToMaskUInt(targetPrefix);
            string targetMaskStr = PrefixToMask(targetPrefix);

            var result = new List<(string Destination, string Mask)>(routes.Count);
            foreach (var r in routes)
            {
                if (!TryParseIpv4ToUInt(r.Destination, out var ip) || !TryParseIpv4ToUInt(r.Mask, out var mask))
                    continue;

                int prefix = MaskUIntToPrefix(mask);
                if (prefix < 0)
                    continue;

                if (prefix > targetPrefix)
                {
                    uint net = ip & targetMask;
                    result.Add((UIntToIpv4String(net), targetMaskStr));
                }
                else
                {
                    // Keep as-is, but normalize to network base for its mask.
                    uint net = ip & mask;
                    result.Add((UIntToIpv4String(net), UIntToIpv4String(mask)));
                }
            }

            return result;
        }

        private static IEnumerable<(string Destination, string Mask)> RangeToCidrs(uint start, uint end)
        {
            // Standard "range to CIDR" conversion. Produces a minimal set of CIDR blocks covering [start..end].
            while (start <= end)
            {
                // Largest power-of-two block size at start.
                uint maxSize = start == 0 ? 0x80000000u : (start & (0u - start)); // lowest set bit
                int maxPrefix = 32 - Log2(maxSize);

                // Shrink block until it fits within end.
                ulong remaining = (ulong)end - start + 1;
                while ((ulong)maxSize > remaining)
                {
                    maxSize >>= 1;
                    maxPrefix++;
                }

                uint mask = PrefixToMaskUInt(maxPrefix);
                yield return (UIntToIpv4String(start), UIntToIpv4String(mask));

                start += maxSize;
                if (start == 0) // overflow
                    break;
            }
        }

        private static int Log2(uint v)
        {
            // v must be power of two.
            int r = 0;
            while ((v >>= 1) != 0) r++;
            return r;
        }

        private static bool TryParseIpv4ToUInt(string s, out uint value)
        {
            value = 0;
            if (!IPAddress.TryParse((s ?? "").Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                return false;

            var b = ip.GetAddressBytes();
            value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            return true;
        }

        private static string UIntToIpv4String(uint v)
        {
            var b1 = (v >> 24) & 0xFF;
            var b2 = (v >> 16) & 0xFF;
            var b3 = (v >> 8) & 0xFF;
            var b4 = v & 0xFF;
            return $"{b1}.{b2}.{b3}.{b4}";
        }

        private static uint PrefixToMaskUInt(int prefix)
        {
            return prefix == 0 ? 0u : 0xffffffffu << (32 - prefix);
        }

        private static int MaskUIntToPrefix(uint mask)
        {
            // Must be contiguous ones then zeros.
            bool zeroSeen = false;
            int prefix = 0;
            for (int i = 31; i >= 0; i--)
            {
                bool bit = ((mask >> i) & 1u) == 1u;
                if (bit)
                {
                    if (zeroSeen) return -1; // non-contiguous
                    prefix++;
                }
                else
                {
                    zeroSeen = true;
                }
            }
            return prefix;
        }

        private static List<string> ExtractResolvableDomains(List<string> domainsRaw)
        {
            var result = new List<string>();
            if (domainsRaw == null || domainsRaw.Count == 0)
                return result;

            foreach (var lineRaw in domainsRaw)
            {
                var line = (lineRaw ?? "").Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (line.StartsWith("#"))
                    continue;

                // Accept plain domains and xray-style prefixes.
                if (line.StartsWith("domain:", StringComparison.OrdinalIgnoreCase))
                    line = line.Substring("domain:".Length);
                else if (line.StartsWith("full:", StringComparison.OrdinalIgnoreCase))
                    line = line.Substring("full:".Length);
                else if (line.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("keyword:", StringComparison.OrdinalIgnoreCase))
                {
                    // Can't resolve these deterministically.
                    continue;
                }

                line = line.Trim();
                if (line.StartsWith("*."))
                    line = line.Substring(2);
                if (line.StartsWith("."))
                    line = line.Substring(1);

                if (line.IndexOfAny(new[] { ' ', '\t', '/', '\\' }) >= 0)
                    continue;

                result.Add(line);
            }

            return result;
        }

        private async Task ApplyServerBypassRoutesIfNeeded()
        {
            if (_serverBypassApplied)
                return;
            if (_serverBypassIps.Count == 0)
                return;

            if (!TryGetDefaultGatewayInterface(out int ifIndex, out string gatewayIpv4))
            {
                LogManager.Add("⚠️ Не удалось определить физический default gateway, bypass маршруты не добавлены.");
                return;
            }

            // Single elevation prompt for all host routes.
            // We delete first for idempotency.
            var cmd = new StringBuilder();
            cmd.Append("/c ");
            for (int i = 0; i < _serverBypassIps.Count; i++)
            {
                string ip = _serverBypassIps[i].ToString();
                if (i > 0) cmd.Append(" & ");
                cmd.Append($"route delete {ip} mask 255.255.255.255");
                cmd.Append(" & ");
                cmd.Append($"route add {ip} mask 255.255.255.255 {gatewayIpv4} metric 1 if {ifIndex}");
            }

            await RunElevatedRouteCommand(cmd.ToString());
            _serverBypassApplied = true;
            LogManager.Add($"✅ Добавлены bypass-маршруты до сервера через GW {gatewayIpv4} (if={ifIndex})");
        }

        private async Task RemoveServerBypassRoutesIfNeeded()
        {
            if (!_serverBypassApplied || _serverBypassIps.Count == 0)
                return;

            try
            {
                var cmd = new StringBuilder();
                cmd.Append("/c ");
                for (int i = 0; i < _serverBypassIps.Count; i++)
                {
                    string ip = _serverBypassIps[i].ToString();
                    if (i > 0) cmd.Append(" & ");
                    cmd.Append($"route delete {ip} mask 255.255.255.255");
                }

                await RunElevatedRouteCommand(cmd.ToString());
                LogManager.Add("🧹 Удалены bypass-маршруты до сервера");
            }
            catch
            {
                // best-effort
            }
            finally
            {
                _serverBypassApplied = false;
            }
        }

        private static bool TryGetDefaultGatewayInterface(out int ifIndex, out string gatewayIpv4)
        {
            gatewayIpv4 = "";
            ifIndex = -1;

            NetworkInterface? best = null;
            int bestScore = -1;
            string bestGateway = "";
            int bestIfIndex = -1;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var props = nic.GetIPProperties();
                var ipv4Props = props.GetIPv4Properties();
                if (ipv4Props == null)
                    continue;

                string? gw = null;
                foreach (var ga in props.GatewayAddresses)
                {
                    if (ga.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var s = ga.Address.ToString();
                        if (!string.Equals(s, "0.0.0.0", StringComparison.Ordinal))
                        {
                            gw = s;
                            break;
                        }
                    }
                }

                if (gw == null)
                    continue;

                // Avoid selecting our own tunnel adapters as "default" for bypass.
                if (nic.Name.Contains("xray", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("xray", StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("byewhitelist", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("byewhitelist", StringComparison.OrdinalIgnoreCase))
                    continue;

                int score = 0;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 50;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 40;

                // Prefer interfaces with a private IPv4 (LAN) address (192.168/10/172.16-31)
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IsPrivateIpv4(ua.Address))
                    {
                        score += 30;
                        break;
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = nic;
                    bestGateway = gw;
                    bestIfIndex = ipv4Props.Index;
                }
            }

            if (best == null)
                return false;

            ifIndex = bestIfIndex;
            gatewayIpv4 = bestGateway;
            return ifIndex > 0 && !string.IsNullOrWhiteSpace(gatewayIpv4);
        }

        private static bool IsPrivateIpv4(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
                return false;

            var b = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            return false;
        }

        private static bool IsPublicRoutableIp(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
                return false;

            if (IPAddress.IsLoopback(ip))
                return false;

            var b = ip.GetAddressBytes();
            // 0.0.0.0/8, 127.0.0.0/8
            if (b[0] == 0 || b[0] == 127) return false;
            // 169.254.0.0/16 (link-local)
            if (b[0] == 169 && b[1] == 254) return false;
            // Private ranges
            if (IsPrivateIpv4(ip)) return false;
            // 100.64.0.0/10 (CGNAT)
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;

            return true;
        }

        private static bool TryGetTunInterfaceWithRetry(out int ifIndex, out string nextHopIp)
        {
            // Give the adapter a moment to appear after Xray starts.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (TryGetTunInterface(out ifIndex, out nextHopIp))
                    return true;

                Thread.Sleep(150);
            }

            ifIndex = -1;
            nextHopIp = "";
            return false;
        }

        private static bool TryGetTunInterface(out int ifIndex, out string nextHopIp)
        {
            NetworkInterface? best = null;
            int bestScore = -1;
            string bestIp = "";
            int bestIndex = -1;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                // Some tunnel adapters may report Unknown/Dormant for a short time after creation.
                if (nic.OperationalStatus == OperationalStatus.Down ||
                    nic.OperationalStatus == OperationalStatus.NotPresent)
                    continue;

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = nic.GetIPProperties();
                var ipv4Props = ipProps.GetIPv4Properties();
                if (ipv4Props == null)
                    continue;

                string? ipv4 = null;
                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = ua.Address.ToString();
                        if (!ip.StartsWith("127.", StringComparison.Ordinal))
                        {
                            ipv4 = ip;
                            break;
                        }
                    }
                }

                int score = 0;

                // Strong signals first.
                if (ipv4 != null && ipv4.StartsWith("10.0.236.", StringComparison.Ordinal))
                    score += 1000;

                if (nic.Name.Contains("byewhitelist", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("byewhitelist", StringComparison.OrdinalIgnoreCase))
                    score += 300;

                if (nic.Name.Contains("xray", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("xray", StringComparison.OrdinalIgnoreCase))
                    score += 100;
                if (nic.Name.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase))
                    score += 80;
                if (nic.Name.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("tun", StringComparison.OrdinalIgnoreCase))
                    score += 50;

                // Prefer link-local/10.x over LAN 192.168.x (we want the tunnel adapter).
                if (ipv4 != null && ipv4.StartsWith("169.254.", StringComparison.Ordinal))
                {
                    score += 60;
                    if (ipProps.GatewayAddresses.Count == 0)
                        score += 40;
                }
                if (ipv4 != null && ipv4.StartsWith("10.", StringComparison.Ordinal))
                    score += 15;
                if (ipv4 != null && ipv4.StartsWith("192.168.", StringComparison.Ordinal))
                    score -= 10;
                if (ipProps.GatewayAddresses.Count > 0)
                    score -= 5;

                // If the interface looks like a TUN but doesn't have an IPv4 yet, keep it as a candidate.
                if (ipv4 == null && score > 0)
                    score -= 5;
                if (ipv4 == null && score <= 0)
                    continue;

                if (score > bestScore)
                {
                    best = nic;
                    bestScore = score;
                    bestIp = ipv4 ?? "";
                    bestIndex = ipv4Props.Index;
                }
            }

            if (best != null && bestScore >= 50)
            {
                LogManager.Add($"🧭 TUN найден: {best.Name} ({best.Description}), ip={(string.IsNullOrEmpty(bestIp) ? "<none>" : bestIp)}, if={bestIndex}");
                ifIndex = bestIndex;
                // If the interface has no IPv4 address, route.exe sometimes accepts 0.0.0.0 as "on-link" gateway
                // when IF is specified.
                nextHopIp = string.IsNullOrEmpty(bestIp) ? "0.0.0.0" : bestIp;
                return true;
            }

            // Debug: list a few candidates to understand why detection failed.
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    var ipProps = nic.GetIPProperties();
                    var ipv4Props = ipProps.GetIPv4Properties();
                    if (ipv4Props == null)
                        continue;

                    string? ipv4 = null;
                    foreach (var ua in ipProps.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            var ip = ua.Address.ToString();
                            if (!ip.StartsWith("127.", StringComparison.Ordinal))
                            {
                                ipv4 = ip;
                                break;
                            }
                        }
                    }

                    if (ipv4 == null &&
                        !nic.Name.Contains("xray", StringComparison.OrdinalIgnoreCase) &&
                        !nic.Description.Contains("xray", StringComparison.OrdinalIgnoreCase) &&
                        !nic.Name.Contains("wintun", StringComparison.OrdinalIgnoreCase) &&
                        !nic.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase) &&
                        !nic.Name.Contains("tun", StringComparison.OrdinalIgnoreCase) &&
                        !nic.Description.Contains("tun", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    LogManager.Add($"🧩 IF cand: {nic.OperationalStatus} {nic.Name} ({nic.Description}) ip={(ipv4 ?? "<none>")} if={ipv4Props.Index}");
                }
            }
            catch
            {
                // ignore
            }

            ifIndex = -1;
            nextHopIp = "";
            return false;
        }

        private static bool IsRoutePresent(string destination, string mask)
        {
            try
            {
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd",
                        Arguments = "/c route print -4",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    }
                };

                p.Start();
                var wait = p.WaitForExitAsync();
                var done = Task.WhenAny(wait, Task.Delay(1200)).GetAwaiter().GetResult();
                if (done != wait)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return false;
                }

                string output = "";
                try
                {
                    var read = p.StandardOutput.ReadToEndAsync();
                    var readDone = Task.WhenAny(read, Task.Delay(400)).GetAwaiter().GetResult();
                    if (readDone == read)
                        output = read.GetAwaiter().GetResult();
                }
                catch { }

                // Match the "Active Routes" row: Destination + Netmask
                return output.Contains(destination, StringComparison.Ordinal) &&
                       output.Contains(mask, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static async Task RunElevatedRouteCommand(string arguments)
        {
            // If the app is already running as admin, do NOT spawn visible elevated cmd windows.
            // Run the command hidden in the background.
            if (IsAdministrator())
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                proc.Start();
                await proc.WaitForExitAsync();

                if (proc.ExitCode != 0)
                {
                    var err = "";
                    try { err = await proc.StandardError.ReadToEndAsync(); } catch { }
                    throw new InvalidOperationException($"Команда маршрутизации завершилась с кодом {proc.ExitCode}. {err}".Trim());
                }

                return;
            }

            // Fallback: ask for elevation (will show UAC prompt).
            var elevated = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (elevated == null)
                throw new InvalidOperationException("Не удалось запустить cmd (runas).");

            await elevated.WaitForExitAsync();

            if (elevated.ExitCode != 0)
                throw new InvalidOperationException($"Команда маршрутизации завершилась с кодом {elevated.ExitCode}.");
        }

        private static async Task RunElevatedCmdScript(string scriptContent, string debugName)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"byewhitelist_{debugName}_{Guid.NewGuid():N}.cmd");
            await File.WriteAllTextAsync(tempPath, scriptContent, Encoding.ASCII);

            try
            {
                // Run the script as admin (single UAC prompt), avoid cmd.exe line-length limits.
                await RunElevatedRouteCommand($"/c \"\"{tempPath}\"\"");
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static async Task ApplySplitDefaultRoutes(int ifIndex, string gateway, string? fallbackGateway)
        {
            try
            {
                // Single elevation prompt for delete+add.
                await RunElevatedRouteCommand(
                    $"/c route delete 0.0.0.0 mask 128.0.0.0 & " +
                    $"route delete 128.0.0.0 mask 128.0.0.0 & " +
                    $"route add 0.0.0.0 mask 128.0.0.0 {gateway} metric 1 if {ifIndex} & " +
                    $"route add 128.0.0.0 mask 128.0.0.0 {gateway} metric 1 if {ifIndex}");
            }
            catch when (!string.IsNullOrEmpty(fallbackGateway) && !string.Equals(fallbackGateway, gateway, StringComparison.Ordinal))
            {
                LogManager.Add($"⚠️ Не удалось поставить маршруты через {gateway}, пробую через {fallbackGateway}...");
                await RunElevatedRouteCommand(
                    $"/c route delete 0.0.0.0 mask 128.0.0.0 & " +
                    $"route delete 128.0.0.0 mask 128.0.0.0 & " +
                    $"route add 0.0.0.0 mask 128.0.0.0 {fallbackGateway} metric 1 if {ifIndex} & " +
                    $"route add 128.0.0.0 mask 128.0.0.0 {fallbackGateway} metric 1 if {ifIndex}");
            }
        }

        private async Task<bool> TestTraffic()
        {
            var r = await TestTrafficDetailed(attempts: 1, retryDelay: TimeSpan.Zero);
            return r.Success;
        }

        private async Task<TrafficTestResult> TestTrafficDetailed(int attempts, TimeSpan retryDelay)
        {
            TrafficTestResult last = new(false, DnsOk: false, ErrorMessage: null);

            for (int attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
            {
                try
                {
                    if (attempt == 1)
                        LogManager.Add("🔍 Тест трафика через TUN...");
                    else
                        LogManager.Add($"🔁 Повтор теста трафика ({attempt}/{attempts})...");

                    bool dnsOk = false;
                    try
                    {
                        var ipify = await DnsAResolver.ResolveAAsync("api.ipify.org", TimeSpan.FromSeconds(4));
                        var cf = await DnsAResolver.ResolveAAsync("www.cloudflare.com", TimeSpan.FromSeconds(4));
                        var all = ipify.Concat(cf).Distinct().ToArray();
                        dnsOk = all.Any(a => a.AddressFamily == AddressFamily.InterNetwork);

                        LogManager.Add($"🔎 DNS api.ipify.org -> {string.Join(", ", ipify.Select(a => a.ToString()))}");
                        if (cf.Count > 0)
                            LogManager.Add($"🔎 DNS www.cloudflare.com -> {string.Join(", ", cf.Select(a => a.ToString()))}");
                        if (!dnsOk)
                            LogManager.Add("⚠️ DNS: нет IPv4-адресов (возможно, ещё не успело примениться после смены маршрутов)");
                    }
                    catch (Exception dnsEx)
                    {
                        LogManager.Add($"⚠️ DNS: ошибка резолва: {dnsEx.Message}");
                    }

                    using var handler = new SocketsHttpHandler
                    {
                        AllowAutoRedirect = true,
                        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                    };

                    using var httpClient = new HttpClient(handler)
                    {
                        Timeout = TimeSpan.FromSeconds(10)
                    };

                    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ByeWhiteList.Windows/1.0");

                    // Prefer HTTP/1.1 to avoid QUIC/HTTP3 flakiness on some networks.
                    var endpoints = new[]
                    {
                        "https://api.ipify.org?format=text",
                        "https://www.cloudflare.com/cdn-cgi/trace"
                    };

                    foreach (var url in endpoints)
                    {
                        try
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get, url)
                            {
                                Version = HttpVersion.Version11,
                                VersionPolicy = HttpVersionPolicy.RequestVersionExact
                            };

                            using var response = await httpClient.SendAsync(req);
                            if (!response.IsSuccessStatusCode)
                                continue;

                            var body = (await response.Content.ReadAsStringAsync()).Trim();
                            if (string.IsNullOrWhiteSpace(body))
                                continue;

                            var ip = ExtractIpFromResponse(url, body) ?? body;
                            LogManager.Add($"✅ ТРАФИК РАБОТАЕТ! Ваш IP: {ip}");
                            return new TrafficTestResult(true, dnsOk, null);
                        }
                        catch (Exception reqEx)
                        {
                            last = new TrafficTestResult(false, dnsOk, reqEx.Message);
                        }
                    }

                    last = new TrafficTestResult(false, dnsOk, last.ErrorMessage ?? "HTTP не вернул успешный ответ");
                }
                catch (Exception ex)
                {
                    last = new TrafficTestResult(false, DnsOk: last.DnsOk, ErrorMessage: ex.Message);
                }

                LogManager.Add($"❌ Тест трафика не удался: {last.ErrorMessage}");

                if (attempt < attempts && retryDelay > TimeSpan.Zero)
                    await Task.Delay(retryDelay);
            }

            return last;
        }

        private static string? ExtractIpFromResponse(string url, string body)
        {
            // Cloudflare trace format: key=value per line; ip=...
            if (url.Contains("cdn-cgi/trace", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in body.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("ip=", StringComparison.OrdinalIgnoreCase))
                        return t.Substring(3).Trim();
                }
            }

            return null;
        }

        public async Task Stop()
        {
            await _lifecycleLock.WaitAsync();
            try
            {
                await StopInternal();
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private async Task StopInternal()
        {
            LogManager.Add($"========== STOP XRAY ==========");
            // UI should show only high-level statuses; detailed logs go to LogManager.

            StopSpeedMonitor();
            try
            {
                _connectivityCheckCts?.Cancel();
                _connectivityCheckCts?.Dispose();
            }
            catch { }
            finally
            {
                _connectivityCheckCts = null;
            }

            // Удаляем TUN маршруты, но не даём этому блочить выход приложения.
            try
            {
                var removeRoutesTask = RemoveTunRouting();
                var done = await Task.WhenAny(removeRoutesTask, Task.Delay(1500));
                if (done == removeRoutesTask)
                {
                    await removeRoutesTask;
                }
                else
                {
                    LogManager.Add("⚠️ Таймаут удаления TUN маршрутов — продолжаю остановку.");
                }
            }
            catch (Exception ex)
            {
                LogManager.Add($"⚠️ Ошибка удаления TUN маршрутов: {ex.Message}");
            }

            if (_xrayProcess != null && !_xrayProcess.HasExited)
            {
                try
                {
                    LogManager.Add($"🛑 Убиваем процесс PID: {_xrayProcess.Id}");
                    try
                    {
                        // Ensure helper/child processes are terminated too.
                        _xrayProcess.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        _xrayProcess.Kill();
                    }

                    await Task.Delay(500);

                    try
                    {
                        await _xrayProcess.WaitForExitAsync();
                    }
                    catch
                    {
                        // Best-effort; process might already be gone/disposed.
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Add($"⚠️ Ошибка при остановке: {ex.Message}");
                }
                finally
                {
                    try { _startedXrayPids.TryRemove(_xrayProcess.Id, out _); } catch { }
                    _xrayProcess.Dispose();
                    _xrayProcess = null;
                }
            }

            if (_configPath != null && File.Exists(_configPath))
            {
                try { File.Delete(_configPath); LogManager.Add($"🗑 Удалён конфиг: {_configPath}"); } catch { }
            }

            _isRunning = false;
            _currentServer = null;
            OnConnectionStateChanged?.Invoke(false);
            LogManager.Add($"✅ Xray остановлен");
        }

        private static async Task TryEnableTunAdapterIfNeededAsync()
        {
            try
            {
                // Best-effort: make sure known tunnel interfaces are enabled before routing setup.
                var knownNames = new[] { "ByeWhiteList-VPN", "xray0" };
                foreach (var name in knownNames)
                {
                    var safe = name.Replace("\"", "");
                    try
                    {
                        await RunElevatedRouteCommand($"/c netsh interface set interface name=\"{safe}\" admin=enabled");
                        LogManager.Add($"✅ Интерфейс включён: {safe}");
                    }
                    catch
                    {
                        // ignore; interface can be absent on first run
                    }
                }
            }
            catch
            {
                // best-effort
            }
        }

        public static void ForceKillKnownXrayProcesses()
        {
            foreach (var pid in _startedXrayPids.Keys.ToList())
            {
                try
                {
                    var process = Process.GetProcessById(pid);
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch { process.Kill(); }
                    }
                }
                catch { }
                finally
                {
                    try { _startedXrayPids.TryRemove(pid, out _); } catch { }
                }
            }
        }

        public static void ForceKillAllXrayProcesses()
        {
            try
            {
                foreach (var process in Process.GetProcessesByName("xray"))
                {
                    try
                    {
                        if (process.HasExited)
                            continue;
                        try { process.Kill(entireProcessTree: true); }
                        catch { process.Kill(); }
                    }
                    catch { }
                    finally
                    {
                        try { _startedXrayPids.TryRemove(process.Id, out _); } catch { }
                        try { process.Dispose(); } catch { }
                    }
                }
            }
            catch { }
        }

        private void StartSpeedMonitor()
        {
            StopSpeedMonitor();

            _speedCts = new CancellationTokenSource();
            var token = _speedCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    var nic = FindTunInterface();
                    if (nic == null)
                        return;

                    var prev = nic.GetIPv4Statistics();
                    long prevRx = prev.BytesReceived;
                    long prevTx = prev.BytesSent;

                    while (!token.IsCancellationRequested && _isRunning)
                    {
                        await Task.Delay(1000, token).ConfigureAwait(false);

                        // Интерфейс может появиться чуть позже или поменяться после перезапуска.
                        nic = FindTunInterface() ?? nic;
                        var cur = nic.GetIPv4Statistics();

                        long rx = cur.BytesReceived;
                        long tx = cur.BytesSent;

                        long down = Math.Max(0, rx - prevRx);
                        long up = Math.Max(0, tx - prevTx);

                        prevRx = rx;
                        prevTx = tx;

                        OnSpeedUpdate?.Invoke(FormatBytesPerSecond(down), FormatBytesPerSecond(up));
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal shutdown.
                }
                catch (Exception ex)
                {
                    LogManager.Add($"⚠️ Speed monitor error: {ex.Message}");
                }
            }, token);
        }

        private void StopSpeedMonitor()
        {
            try
            {
                _speedCts?.Cancel();
                _speedCts?.Dispose();
            }
            catch
            {
                // Best-effort.
            }
            finally
            {
                _speedCts = null;
            }
        }

        public void RestartSpeedMonitor()
        {
            try
            {
                if (!_isRunning)
                    return;
                StartSpeedMonitor();
            }
            catch
            {
                // best-effort
            }
        }

        private NetworkInterface? FindTunInterface()
        {
            const string hint = "byewhitelist0";

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                if (nic.Name.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    return nic;
                }
            }

            // Fallback: match by "xray" even if hint differs.
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                if (nic.Name.Contains("xray", StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("xray", StringComparison.OrdinalIgnoreCase))
                {
                    return nic;
                }
            }

            return null;
        }

        private static string FormatBytesPerSecond(long bytesPerSecond)
        {
            const double Kb = 1024.0;
            const double Mb = 1024.0 * 1024.0;

            double bps = Math.Max(0, bytesPerSecond);

            if (bps >= Mb)
                return $"{bps / Mb:0.0} MB/s";
            if (bps >= Kb)
                return $"{bps / Kb:0.0} KB/s";

            return $"{bps:0} B/s";
        }

        public async Task<bool> SwitchServerAsync(ProxyEntity newServer)
        {
            if (IsRunning)
            {
                await Stop();
                await Task.Delay(500);
                return await Start(newServer);
            }
            return false;
        }

        private string GenerateTunConfig(string beanJson)
        {
            try
            {
                var input = JObject.Parse(beanJson);

                var enableLocalSocks = ByeWhiteList.Windows.AppSettings.Instance.EnableLocalSocksProxy;
                if (enableLocalSocks)
                {
                    _socksPort = GetAvailableTcpPort(_socksPort);
                    LogManager.Add($"🧦 SOCKS порт: {_socksPort}");
                }

                // Bind both proxy and direct outbounds to the physical interface IP so that:
                // - direct rules truly bypass the VPN even when split-default routes point to the TUN
                // - proxy outbound keeps using the real network (prevents routing loops)
                string? sendThrough = TryGetPhysicalInterfaceIpv4(out var physicalIp) ? physicalIp : null;
                if (!string.IsNullOrWhiteSpace(sendThrough))
                    LogManager.Add($"🧭 sendThrough: {sendThrough}");

                // Whitelist: domains/IPs that must bypass VPN (direct).
                var userDirect = NormalizeForceDirectDomains(ByeWhiteList.Windows.AppSettings.Instance.ForceDirectDomains);

                // User overrides: domains that must go through VPN even if they match whitelist direct rules.
                var forceProxyDomains = NormalizeForceProxyDomains(ByeWhiteList.Windows.AppSettings.Instance.ForceProxyDomains);

                var profile = GeoRoutingProfiles.Get(ByeWhiteList.Windows.AppSettings.Instance.GeoRoutingProfileId);

                var routingRules = new JArray();

                // 0) Always keep local/private traffic direct (LAN, DNS to router, etc.).
                routingRules.Add(new JObject
                {
                    ["type"] = "field",
                    ["ip"] = new JArray { "geoip:private" },
                    ["outboundTag"] = "direct"
                });

                routingRules.Add(new JObject
                {
                    ["type"] = "field",
                    ["domain"] = new JArray { "geosite:private" },
                    ["outboundTag"] = "direct"
                });

                // 1) Force-proxy domains (user overrides) — must be before direct whitelist domains.
                if (forceProxyDomains.Count > 0)
                {
                    routingRules.Add(new JObject
                    {
                        ["type"] = "field",
                        ["domain"] = ToJArrayStrings(forceProxyDomains),
                        ["outboundTag"] = "proxy"
                    });
                }

                // 2) Force-direct domains (user overrides)
                if (userDirect.Count > 0)
                {
                    routingRules.Add(new JObject
                    {
                        ["type"] = "field",
                        ["domain"] = ToJArrayStrings(userDirect),
                        ["outboundTag"] = "direct"
                    });
                }

                // 3) Country ruleset (karing-like): direct for local geoip/geosite, everything else via proxy.
                var geoSiteTags = SplitGeoSiteTags(profile.GeoSiteTag);
                if (geoSiteTags.Count > 0)
                {
                    LogManager.Add($"🌍 geosite direct: {string.Join(", ", geoSiteTags)}");
                    routingRules.Add(new JObject
                    {
                        ["type"] = "field",
                        ["domain"] = ToJArrayStrings(geoSiteTags),
                        ["outboundTag"] = "direct"
                    });
                }

                LogManager.Add($"🌍 geoip direct: {profile.GeoIpTag}");
                routingRules.Add(new JObject
                {
                    ["type"] = "field",
                    ["ip"] = new JArray { profile.GeoIpTag },
                    ["outboundTag"] = "direct"
                });

                // Everything that enters via TUN goes through the VPN/proxy by default.
                routingRules.Add(new JObject
                {
                    ["type"] = "field",
                    ["inboundTag"] = new JArray { "tun-in" },
                    ["outboundTag"] = "proxy"
                });

                var config = new JObject
                {
                    ["log"] = new JObject { ["loglevel"] = "warning" },
                    ["inbounds"] = BuildInbounds(enableLocalSocks),
                    ["outbounds"] = new JArray
                    {
                        BuildOutboundWithSendThrough(input, sendThrough),
                        CreateDirectOutbound(sendThrough),
                        new JObject { ["protocol"] = "blackhole", ["tag"] = "block" }
                    },
                    ["routing"] = new JObject
                    {
                        // Helpful so geoip rules can kick in even when domain doesn't match.
                        ["domainStrategy"] = "IPIfNonMatch",
                        ["rules"] = routingRules
                    }
                };

                return config.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception ex)
            {
                LogManager.Add($"❌ Ошибка генерации TUN конфига: {ex.Message}");
                return GenerateFallbackTunConfig();
            }
        }

        private JArray BuildInbounds(bool enableLocalSocks)
        {
            var inbounds = new JArray
            {
                new JObject
                {
                    ["tag"] = "tun-in",
                    ["protocol"] = "tun",
                    ["listen"] = "0.0.0.0",
                    ["port"] = 0,
                    // Needed so domain-based whitelist rules can work for TUN traffic.
                    // Without sniffing, TUN inbound often only has destination IP and will never match "domain:" rules.
                    ["sniffing"] = new JObject
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new JArray { "http", "tls", "quic" }
                    },
                    ["settings"] = new JObject
                    {
                        // Ask Xray to use a stable adapter name (visible in Network Connections).
                        ["name"] = "ByeWhiteList-VPN",
                        ["tun_address"] = "10.0.236.1/24",
                        ["mtu"] = 1500,
                        ["route_address"] = new JArray { "0.0.0.0/1", "128.0.0.0/1" }
                    }
                }
            };

            if (enableLocalSocks)
            {
                // SECURITY NOTE:
                // Local SOCKS proxies are a known detection/leak vector for "spyware" apps on the same device/host.
                // Keep it disabled by default. If enabled, bind to localhost and disable UDP to reduce risk.
                inbounds.Add(new JObject
                {
                    ["tag"] = "socks",
                    ["protocol"] = "socks",
                    ["port"] = _socksPort,
                    ["listen"] = "127.0.0.1",
                    ["settings"] = new JObject
                    {
                        ["auth"] = "noauth",
                        ["udp"] = false
                    }
                });
            }

            return inbounds;
        }

        private static JObject CreateDirectOutbound(string? sendThrough)
        {
            var o = new JObject
            {
                ["protocol"] = "freedom",
                ["tag"] = "direct"
            };

            if (!string.IsNullOrWhiteSpace(sendThrough))
                o["sendThrough"] = sendThrough;

            return o;
        }

        private static bool TryGetPhysicalInterfaceIpv4(out string ipv4)
        {
            ipv4 = "";
            try
            {
                if (!TryGetDefaultGatewayInterface(out int ifIndex, out _))
                    return false;

                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var p = nic.GetIPProperties().GetIPv4Properties();
                    if (p == null)
                        continue;
                    if (p.Index != ifIndex)
                        continue;

                    var ipProps = nic.GetIPProperties();
                    foreach (var ua in ipProps.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        var ip = ua.Address.ToString();
                        if (ip.StartsWith("127.", StringComparison.Ordinal))
                            continue;
                        if (ip.StartsWith("169.254.", StringComparison.Ordinal))
                            continue;
                        ipv4 = ip;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static JObject BuildOutboundWithSendThrough(JObject input, string? sendThrough)
        {
            var outbound = BuildOutbound(input);
            if (!string.IsNullOrWhiteSpace(sendThrough))
                outbound["sendThrough"] = sendThrough;
            return outbound;
        }

        private static List<string> NormalizeWhitelistIps(List<string> ipsRaw)
        {
            var result = new List<string>();
            if (ipsRaw == null || ipsRaw.Count == 0)
                return result;

            foreach (var lineRaw in ipsRaw)
            {
                var line = (lineRaw ?? "").Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (line.StartsWith("#"))
                    continue;

                // Allow CIDR (x.x.x.x/yy) and single IPv4.
                if (line.Contains("/"))
                {
                    // Validate: IPv4 + prefix length.
                    var parts = line.Split('/');
                    if (parts.Length == 2 &&
                        IPAddress.TryParse(parts[0], out var ip) &&
                        ip.AddressFamily == AddressFamily.InterNetwork &&
                        int.TryParse(parts[1], out var prefix) &&
                        prefix >= 0 && prefix <= 32)
                    {
                        // A bypass rule for 0.0.0.0/0 would effectively send *everything* direct,
                        // which defeats the "whitelist direct, everything else proxy" model.
                        if (prefix == 0)
                        {
                            LogManager.Add("⚠️ В whitelist-ips найдено 0.0.0.0/0 — игнорирую (иначе весь трафик пойдёт в direct).");
                        }
                        else
                        {
                            result.Add($"{ip}/{prefix}");
                        }
                    }
                    continue;
                }

                if (IPAddress.TryParse(line, out var single) && single.AddressFamily == AddressFamily.InterNetwork)
                    result.Add(line);
            }

            return result;
        }

        private static List<string> NormalizeWhitelistDomains(List<string> domainsRaw)
        {
            var result = new List<string>();
            if (domainsRaw == null || domainsRaw.Count == 0)
                return result;

            // We often get many "однотипных" subdomains (full:foo.example.com, full:bar.example.com, ...).
            // Xray can match them cheaper as a single suffix rule: domain:example.com.
            // This keeps routing config smaller and reduces DNS churn for "bypass routes" preparation.
            const int fullToDomainAggregationThreshold = 10;

            var domainSuffixRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fullRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keywordAndRegexRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var lineRaw in domainsRaw)
            {
                var line = (lineRaw ?? "").Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (line.StartsWith("#"))
                    continue;

                if (line.StartsWith("keyword:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase))
                {
                    keywordAndRegexRules.Add(line);
                    continue;
                }

                if (line.StartsWith("domain:", StringComparison.OrdinalIgnoreCase))
                {
                    var host = NormalizeDomainToken(line.Substring("domain:".Length));
                    if (!string.IsNullOrWhiteSpace(host))
                        domainSuffixRules.Add("domain:" + host);
                    continue;
                }

                if (line.StartsWith("full:", StringComparison.OrdinalIgnoreCase))
                {
                    var host = NormalizeDomainToken(line.Substring("full:".Length));
                    if (!string.IsNullOrWhiteSpace(host))
                        fullRules.Add("full:" + host);
                    continue;
                }

                // Common formats: "example.com", ".example.com", "*.example.com"
                if (line.StartsWith("*."))
                    line = line.Substring(2);
                if (line.StartsWith("."))
                    line = line.Substring(1);

                var token = NormalizeDomainToken(line);
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                // Suffix match by default: applies to token and all subdomains.
                domainSuffixRules.Add("domain:" + token);
            }

            // 1) If we already have domain:suffix, full:subdomain is redundant.
            if (domainSuffixRules.Count > 0 && fullRules.Count > 0)
            {
                foreach (var full in fullRules.ToList())
                {
                    var host = full.Substring("full:".Length);
                    var suffix = "domain:" + GetRegistrableDomain(host);
                    // If we have a broader rule that surely includes the full host, we can drop it.
                    // We only do this for registrable domain (not public suffix) to avoid "domain:co.uk" style accidents.
                    if (domainSuffixRules.Contains(suffix))
                        fullRules.Remove(full);
                }
            }

            // 1b) If we have *many* domain:sub.example.com rules under the same registrable domain,
            // collapse them into a single domain:example.com. This broadens the whitelist on purpose
            // (trade-off: fewer rules & less config size vs. more direct traffic).
            if (domainSuffixRules.Count > 0)
            {
                var byRegistrable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var rule in domainSuffixRules)
                {
                    var host = rule.Substring("domain:".Length);
                    var reg = GetRegistrableDomain(host);
                    if (string.IsNullOrWhiteSpace(reg))
                        continue;

                    // Only consider deeper subdomains (api.example.com -> example.com). If the host is already registrable, keep it.
                    if (host.Equals(reg, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!byRegistrable.TryGetValue(reg, out var list))
                    {
                        list = new List<string>();
                        byRegistrable[reg] = list;
                    }
                    list.Add(rule);
                }

                foreach (var kv in byRegistrable)
                {
                    var reg = kv.Key;
                    var items = kv.Value;
                    if (items.Count < fullToDomainAggregationThreshold)
                        continue;

                    var aggregatedRule = "domain:" + reg;
                    if (!domainSuffixRules.Contains(aggregatedRule))
                        domainSuffixRules.Add(aggregatedRule);

                    foreach (var item in items)
                        domainSuffixRules.Remove(item);
                }
            }

            // 2) Aggregate many full:*.X under a single domain:X.
            if (fullRules.Count > 0)
            {
                var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var full in fullRules)
                {
                    var host = full.Substring("full:".Length);
                    var reg = GetRegistrableDomain(host);
                    if (string.IsNullOrWhiteSpace(reg))
                        continue;

                    if (!groups.TryGetValue(reg, out var list))
                    {
                        list = new List<string>();
                        groups[reg] = list;
                    }
                    list.Add(full);
                }

                foreach (var kv in groups)
                {
                    var registrable = kv.Key;
                    var items = kv.Value;
                    if (items.Count < fullToDomainAggregationThreshold)
                        continue;

                    var aggregatedRule = "domain:" + registrable;
                    if (!domainSuffixRules.Contains(aggregatedRule))
                        domainSuffixRules.Add(aggregatedRule);

                    foreach (var full in items)
                        fullRules.Remove(full);
                }
            }

            // Stable ordering helps diffs/logs.
            result.AddRange(keywordAndRegexRules.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            result.AddRange(domainSuffixRules.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            result.AddRange(fullRules.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return result;
        }

        private static List<string> NormalizeForceProxyDomains(string raw)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            var lines = raw.Replace("\r\n", "\n").Split('\n');
            foreach (var lineRaw in lines)
            {
                var line = (lineRaw ?? "").Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (line.StartsWith("#"))
                    continue;

                if (line.StartsWith("full:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("keyword:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(line);
                    continue;
                }

                // Common formats: "example.com", ".example.com", "*.example.com"
                if (line.StartsWith("*."))
                    line = line.Substring(2);
                if (line.StartsWith("."))
                    line = line.Substring(1);

                if (line.IndexOfAny(new[] { ' ', '\t', '/', '\\' }) >= 0)
                    continue;

                // Suffix match by default (more convenient for users): include subdomains too.
                result.Add("domain:" + line);
            }

            return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> NormalizeForceDirectDomains(string raw)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            var lines = raw.Replace("\r\n", "\n").Split('\n');
            foreach (var lineRaw in lines)
            {
                var line = (lineRaw ?? "").Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (line.StartsWith("#"))
                    continue;

                // TLD shortcuts: ".ru" -> regexp match.
                if (TryTldShortcutToRegexp(line, out var rx))
                {
                    result.Add(rx);
                    continue;
                }

                if (line.StartsWith("full:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("keyword:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(line);
                    continue;
                }

                if (line.StartsWith("*."))
                    line = line.Substring(2);
                if (line.StartsWith("."))
                    line = line.Substring(1);

                if (!IsLikelyDomainToken(line))
                    continue;

                result.Add("domain:" + line);
            }

            return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> SplitGeoSiteTags(string raw)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return result;

            var parts = raw.Split(
                new[] { "\r\n", "\n", "\r", ",", ";" },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var p in parts)
            {
                var s = (p ?? "").Trim();
                if (string.IsNullOrWhiteSpace(s))
                    continue;
                result.Add(s);
            }

            return result;
        }

        private static bool IsLikelyDomainToken(string token)
        {
            token = (token ?? "").Trim().Trim('.');
            if (string.IsNullOrWhiteSpace(token))
                return false;
            if (token.IndexOfAny(new[] { ' ', '\t', '/', '\\' }) >= 0)
                return false;
            // Must have at least one dot to be a domain.
            if (!token.Contains('.'))
                return false;
            return true;
        }

        private static bool TryTldShortcutToRegexp(string token, out string regexpRule)
        {
            regexpRule = "";
            token = (token ?? "").Trim();
            if (token.StartsWith("."))
                token = token.Substring(1);

            // ".ru" / ".su" / ".рф"
            if (string.Equals(token, "ru", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "su", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "рф", StringComparison.OrdinalIgnoreCase))
            {
                // Go regexp in Xray: escape dot.
                regexpRule = $"regexp:.*\\.{token}$";
                return true;
            }

            return false;
        }

        private static (HashSet<string> FullHosts, List<string> Suffixes) GetForceProxyHostMatchers(string raw)
        {
            var fullHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var suffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(raw))
                return (fullHosts, new List<string>());

            var lines = raw.Replace("\r\n", "\n").Split('\n');
            foreach (var lineRaw in lines)
            {
                var line = (lineRaw ?? "").Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (line.StartsWith("#"))
                    continue;

                if (line.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("keyword:", StringComparison.OrdinalIgnoreCase))
                {
                    // Can't reliably apply to a raw domain list here.
                    continue;
                }

                if (line.StartsWith("full:", StringComparison.OrdinalIgnoreCase))
                {
                    var host = NormalizeDomainToken(line.Substring("full:".Length));
                    if (!string.IsNullOrWhiteSpace(host))
                        fullHosts.Add(host);
                    continue;
                }

                if (line.StartsWith("domain:", StringComparison.OrdinalIgnoreCase))
                {
                    var host = NormalizeDomainToken(line.Substring("domain:".Length));
                    if (!string.IsNullOrWhiteSpace(host))
                        suffixes.Add(host);
                    continue;
                }

                // Plain domain token.
                if (line.StartsWith("*."))
                    line = line.Substring(2);
                if (line.StartsWith("."))
                    line = line.Substring(1);

                var token = NormalizeDomainToken(line);
                if (!string.IsNullOrWhiteSpace(token))
                    suffixes.Add(token);
            }

            // Sort suffixes by length (longest first) so checks short-circuit on specific entries.
            var suffixList = suffixes.OrderByDescending(s => s.Length).ToList();
            return (fullHosts, suffixList);
        }

        private static bool IsForceProxyDomain(string domain, HashSet<string> fullHosts, List<string> suffixes)
        {
            domain = NormalizeDomainToken(domain);
            if (string.IsNullOrWhiteSpace(domain))
                return false;

            if (fullHosts.Contains(domain))
                return true;

            foreach (var s in suffixes)
            {
                if (domain.Equals(s, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (domain.EndsWith("." + s, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string NormalizeDomainToken(string token)
        {
            token = (token ?? "").Trim();
            if (string.IsNullOrWhiteSpace(token))
                return "";

            // Remove surrounding dots and trailing dot.
            token = token.Trim().Trim('.');
            if (string.IsNullOrWhiteSpace(token))
                return "";

            // Basic sanity: avoid obvious non-domain tokens.
            if (token.IndexOfAny(new[] { ' ', '\t', '/', '\\' }) >= 0)
                return "";

            return token.ToLowerInvariant();
        }

        private static string GetRegistrableDomain(string host)
        {
            host = NormalizeDomainToken(host);
            if (string.IsNullOrWhiteSpace(host))
                return "";

            var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return host;

            string last2 = parts[^2] + "." + parts[^1];
            if (parts.Length >= 3 && IsTwoLevelPublicSuffix(last2))
                return parts[^3] + "." + last2;

            return last2;
        }

        private static bool IsTwoLevelPublicSuffix(string last2)
        {
            // Minimal list to avoid aggregating to an overly broad suffix like "co.uk".
            // We only need the common ones we may encounter in practice.
            return last2.Equals("co.uk", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("org.uk", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("gov.uk", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("ac.uk", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("com.au", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("net.au", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("org.au", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("com.br", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("com.tr", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("com.cn", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("com.hk", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("com.tw", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("com.sg", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("com.my", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("com.ph", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("com.ua", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("com.ru", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("net.ru", StringComparison.OrdinalIgnoreCase) ||
                   last2.Equals("org.ru", StringComparison.OrdinalIgnoreCase);
        }

        private static JArray ToJArrayStrings(IEnumerable<string> values)
        {
            var arr = new JArray();
            foreach (var v in values)
            {
                if (string.IsNullOrWhiteSpace(v))
                    continue;
                arr.Add(v!);
            }
            return arr;
        }

        private static int GetAvailableTcpPort(int preferredPort)
        {
            if (preferredPort <= 0)
                preferredPort = 10808;

            if (CanBindTcp(IPAddress.Loopback, preferredPort))
                return preferredPort;

            // Fall back to a free ephemeral port chosen by the OS.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static bool CanBindTcp(IPAddress address, int port)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(address, port);
                listener.Start();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { listener?.Stop(); } catch { }
            }
        }

        private string GenerateFallbackTunConfig()
        {
            return @"{
  ""log"": { ""loglevel"": ""warning"" },
  ""inbounds"": [
    {
      ""tag"": ""tun-in"",
      ""protocol"": ""tun"",
      ""listen"": ""0.0.0.0"",
      ""port"": 0,
      ""settings"": {
        ""tun_address"": ""10.0.236.1/24"",
        ""mtu"": 1500,
        ""route_address"": [ ""0.0.0.0/1"", ""128.0.0.0/1"" ]
      }
    }
  ],
  ""outbounds"": [
    { ""protocol"": ""freedom"", ""tag"": ""direct"" }
  ]
}";
        }

        private static JObject BuildOutbound(JObject input)
        {
            string protocol = input["type"]?.Value<string>()?.ToLower() ?? "vless";
            string server = input["server"]?.Value<string>() ?? "";
            int port = input["port"]?.Value<int>() ?? 0;
            string uuid = input["uuid"]?.Value<string>() ?? "";

            JObject settings;

            // Xray outbounds differ by protocol. If we always emit vnext/users/id,
            // trojan/shadowsocks configs become invalid and Xray exits with code 1.
            if (protocol == "trojan")
            {
                string password = input["password"]?.Value<string>()
                    ?? input["pass"]?.Value<string>()
                    ?? "";

                settings = new JObject
                {
                    ["servers"] = new JArray
                    {
                        new JObject
                        {
                            ["address"] = server,
                            ["port"] = port,
                            ["password"] = password
                        }
                    }
                };
            }
            else if (protocol == "shadowsocks" || protocol == "ss")
            {
                // Normalize "ss" to the actual Xray protocol name.
                protocol = "shadowsocks";

                string method = input["method"]?.Value<string>() ?? "chacha20-ietf-poly1305";
                string password = input["password"]?.Value<string>()
                    ?? input["pass"]?.Value<string>()
                    ?? "";

                settings = new JObject
                {
                    ["servers"] = new JArray
                    {
                        new JObject
                        {
                            ["address"] = server,
                            ["port"] = port,
                            ["method"] = method,
                            ["password"] = password
                        }
                    }
                };
            }
            else
            {
                // vless/vmess share vnext/users, but user fields differ a bit.
                var user = new JObject
                {
                    ["id"] = uuid
                };

                if (protocol == "vless")
                {
                    user["encryption"] = "none";
                    string flow = input["flow"]?.Value<string>() ?? "";
                    if (!string.IsNullOrEmpty(flow))
                        user["flow"] = flow;
                }
                else if (protocol == "vmess")
                {
                    int alterId = input["alterId"]?.Value<int>()
                        ?? input["alter_id"]?.Value<int>()
                        ?? 0;
                    user["alterId"] = alterId;

                    // LinkParser may overload "security" with TLS, so only apply it as VMess cipher when it looks like one.
                    string cipher = input["cipher"]?.Value<string>()
                        ?? input["scy"]?.Value<string>()
                        ?? input["vmess_security"]?.Value<string>()
                        ?? input["security"]?.Value<string>()
                        ?? "auto";
                    cipher = cipher.ToLowerInvariant();
                    if (cipher != "tls" && cipher != "reality")
                        user["security"] = cipher;
                }
                else
                {
                    // Default to vless-style if unknown.
                    user["encryption"] = "none";
                }

                settings = new JObject
                {
                    ["vnext"] = new JArray
                    {
                        new JObject
                        {
                            ["address"] = server,
                            ["port"] = port,
                            ["users"] = new JArray { user }
                        }
                    }
                };
            }

            var outbound = new JObject
            {
                ["tag"] = "proxy",
                ["protocol"] = protocol,
                ["settings"] = settings
            };

            var streamSettings = new JObject();

            string network = input["network"]?.Value<string>()?.ToLower() ?? "tcp";
            streamSettings["network"] = network;

            if (network == "grpc")
            {
                streamSettings["grpcSettings"] = new JObject
                {
                    ["serviceName"] = input["serviceName"]?.Value<string>() ?? ""
                };
            }
            else if (network == "ws")
            {
                string host = input["host"]?.Value<string>() ?? "";

                var wsSettings = new JObject
                {
                    ["path"] = input["path"]?.Value<string>() ?? "/"
                };

                if (!string.IsNullOrEmpty(host))
                {
                    wsSettings["headers"] = new JObject
                    {
                        ["Host"] = host
                    };
                }

                streamSettings["wsSettings"] = wsSettings;
            }

            string security = input["security"]?.Value<string>()?.ToLower() ?? "";
            string realityPubKey = input["reality_pub_key"]?.Value<string>() ?? "";

            if (security == "reality" || !string.IsNullOrEmpty(realityPubKey))
            {
                streamSettings["security"] = "reality";
                streamSettings["realitySettings"] = new JObject
                {
                    ["serverName"] = input["sni"]?.Value<string>() ?? input["serverName"]?.Value<string>() ?? "",
                    ["fingerprint"] = input["fingerprint"]?.Value<string>() ?? "chrome",
                    ["publicKey"] = realityPubKey,
                    ["shortId"] = input["reality_short_id"]?.Value<string>() ?? input["sid"]?.Value<string>() ?? ""
                };
            }
            else if (security == "tls")
            {
                streamSettings["security"] = "tls";
                streamSettings["tlsSettings"] = new JObject
                {
                    ["serverName"] = input["sni"]?.Value<string>() ?? "",
                    ["allowInsecure"] = false
                };
            }

            outbound["streamSettings"] = streamSettings;

            return outbound;
        }

        public static string GenerateHttpProxyTestConfig(string beanJson, int httpPort)
        {
            try
            {
                var input = JObject.Parse(beanJson);

                var config = new JObject
                {
                    ["log"] = new JObject { ["loglevel"] = "warning" },
                    ["inbounds"] = new JArray
                    {
                        new JObject
                        {
                            ["tag"] = "http-in",
                            ["protocol"] = "http",
                            ["listen"] = "127.0.0.1",
                            ["port"] = httpPort,
                            ["settings"] = new JObject
                            {
                                ["timeout"] = 30
                            }
                        }
                    },
                    ["outbounds"] = new JArray
                    {
                        BuildOutbound(input),
                        new JObject { ["protocol"] = "freedom", ["tag"] = "direct" },
                        new JObject { ["protocol"] = "blackhole", ["tag"] = "block" }
                    },
                    ["routing"] = new JObject
                    {
                        ["domainStrategy"] = "AsIs",
                        ["rules"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "field",
                                ["inboundTag"] = new JArray { "http-in" },
                                ["outboundTag"] = "proxy"
                            }
                        }
                    }
                };

                return config.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch
            {
                // Return a minimal config that will fail fast.
                return @"{ ""log"": { ""loglevel"": ""warning"" }, ""inbounds"": [], ""outbounds"": [ { ""protocol"": ""freedom"", ""tag"": ""direct"" } ] }";
            }
        }

        public static string GenerateHttpProxyBatchTestConfig(IReadOnlyList<(string BeanJson, int HttpPort, string InboundTag, string OutboundTag, string ProxyUser, string ProxyPass)> entries)
        {
            try
            {
                var inbounds = new JArray();
                var outbounds = new JArray();
                var rules = new JArray();

                foreach (var e in entries)
                {
                    var input = JObject.Parse(e.BeanJson);

                    inbounds.Add(new JObject
                    {
                        ["tag"] = e.InboundTag,
                        ["protocol"] = "http",
                        ["listen"] = "127.0.0.1",
                        ["port"] = e.HttpPort,
                        ["settings"] = new JObject
                        {
                            // Protect localhost proxy from trivial port-scans / bypass attacks.
                            // Speedtest client knows these credentials; other local apps won't.
                            ["timeout"] = 15
                        }
                    });

                    // If accounts are null, remove it from settings (xray expects the property omitted, not null).
                    var settings = (JObject)inbounds.Last["settings"]!;
                    if (string.IsNullOrWhiteSpace(e.ProxyUser) || string.IsNullOrWhiteSpace(e.ProxyPass))
                    {
                        settings.Remove("accounts");
                    }
                    else
                    {
                        settings["accounts"] = new JArray
                        {
                            new JObject
                            {
                                ["user"] = e.ProxyUser,
                                ["pass"] = e.ProxyPass
                            }
                        };
                    }

                    var outbound = BuildOutbound(input);
                    outbound["tag"] = e.OutboundTag;
                    outbounds.Add(outbound);

                    rules.Add(new JObject
                    {
                        ["type"] = "field",
                        ["inboundTag"] = new JArray { e.InboundTag },
                        ["outboundTag"] = e.OutboundTag
                    });
                }

                outbounds.Add(new JObject { ["protocol"] = "freedom", ["tag"] = "direct" });
                outbounds.Add(new JObject { ["protocol"] = "blackhole", ["tag"] = "block" });

                var config = new JObject
                {
                    ["log"] = new JObject { ["loglevel"] = "warning" },
                    ["inbounds"] = inbounds,
                    ["outbounds"] = outbounds,
                    ["routing"] = new JObject
                    {
                        ["domainStrategy"] = "AsIs",
                        ["rules"] = rules
                    }
                };

                return config.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch
            {
                return @"{ ""log"": { ""loglevel"": ""warning"" }, ""inbounds"": [], ""outbounds"": [ { ""protocol"": ""freedom"", ""tag"": ""direct"" } ] }";
            }
        }

        public async Task<int> TestServerAsync(ProxyEntity server, int timeoutMs = 5000)
        {
            var testConfigPath = Path.Combine(Path.GetTempPath(), $"xray_test_{Guid.NewGuid()}.json");
            var testProcess = new Process();

            try
            {
                var xrayPath = XrayBootstrapper.ResolveExistingXrayPath();
                if (!File.Exists(xrayPath))
                    return -1;

                var config = GenerateTunConfig(server.BeanJson ?? "");
                await File.WriteAllTextAsync(testConfigPath, config);

                testProcess.StartInfo = new ProcessStartInfo
                {
                    FileName = xrayPath,
                    Arguments = $"run -c \"{testConfigPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                testProcess.Start();
                await Task.Delay(2000);

                if (testProcess.HasExited)
                    return -1;

                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromMilliseconds(timeoutMs);

                var stopwatch = Stopwatch.StartNew();
                var response = await httpClient.GetAsync("https://api.ipify.org");
                stopwatch.Stop();

                if (response.IsSuccessStatusCode)
                    return (int)stopwatch.ElapsedMilliseconds;

                return -1;
            }
            catch
            {
                return -1;
            }
            finally
            {
                try
                {
                    if (!testProcess.HasExited)
                    {
                        try { testProcess.Kill(entireProcessTree: true); }
                        catch { testProcess.Kill(); }
                    }
                    testProcess.Dispose();
                }
                catch { }
                try { File.Delete(testConfigPath); } catch { }
            }
        }
    }
}
