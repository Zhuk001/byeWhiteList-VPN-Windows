using ByeWhiteList.Windows.Models;
using ByeWhiteList.Windows.Data;
using ByeWhiteList.Windows.Services;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace ByeWhiteList.Windows
{
    public partial class SpeedTestWindow : Window
    {
        private readonly ProxyEntity? _server;
        private readonly bool _useExistingVpnTunnel;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _testStarted;
        private string? _lastErrorFilePath;
        private string? _lastErrorDetails;

        public SpeedTestWindow(ProxyEntity? server, bool useExistingVpnTunnel = false)
        {
            _server = server;
            _useExistingVpnTunnel = useExistingVpnTunnel;
            InitializeComponent();
            Loaded += SpeedTestWindow_Loaded;
            Closed += (_, _) => { try { _cts.Cancel(); } catch { } };
        }

        private async void SpeedTestWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_server == null)
                {
                    TitleText.Text = "VGSpeed — Прямая сеть";
                }
                else if (_useExistingVpnTunnel)
                {
                    TitleText.Text = $"VGSpeed — VPN: {_server.DisplayName()}";
                }
                else
                {
                    TitleText.Text = $"VGSpeed — {_server.DisplayName()}";
                }

                await WebView.EnsureCoreWebView2Async();
                WebView.CoreWebView2.Settings.IsScriptEnabled = true;
                WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                WebView.CoreWebView2.WebMessageReceived += WebView_WebMessageReceived;

                var htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "speedtest.html");
                if (File.Exists(htmlPath))
                {
                    var html = File.ReadAllText(htmlPath);
                    WebView.NavigateToString(html);
                    if (_server == null)
                    {
                        StatusText.Text = "Тест прямой сети. Нажмите «Начать тест».";
                    }
                    else if (_useExistingVpnTunnel)
                    {
                        StatusText.Text = $"Тест через VPN-туннель: {_server.DisplayName()}. Нажмите «Начать тест».";
                    }
                    else
                    {
                        StatusText.Text = $"Тест сервера: {_server.DisplayName()}. Нажмите «Начать тест».";
                    }
                }
                else
                {
                    StatusText.Text = "Не найден wwwroot/speedtest.html";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка: {ex.Message}";
            }
        }

        private void WebView_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.WebMessageAsJson;
                if (string.IsNullOrWhiteSpace(json))
                    return;

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("cmd", out var cmdEl))
                    return;

                var cmd = cmdEl.GetString();
                if (string.Equals(cmd, "start", StringComparison.OrdinalIgnoreCase))
                {
                    if (_testStarted)
                        return;
                    _testStarted = true;
                    _ = RunAsync();
                }
                else if (string.Equals(cmd, "cancel", StringComparison.OrdinalIgnoreCase))
                {
                    try { _cts.Cancel(); } catch { }
                }
            }
            catch { }
        }

        private async Task RunAsync()
        {
            try
            {
                StatusText.Text = "Запуск теста...";
                _lastErrorFilePath = null;
                _lastErrorDetails = null;
                await PostToUiAsync(new { stage = "starting" });

                var progress = new Progress<SpeedTestProgress>(p =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = p.Stage switch
                        {
                            SpeedTestStage.Ping => "Ping...",
                            SpeedTestStage.Download => $"Загрузка... {p.Percent:0}%",
                            SpeedTestStage.Upload => $"Отдача... {p.Percent:0}%",
                            SpeedTestStage.Finished => "Готово",
                            _ => "..."
                        };
                    });

                    _ = PostToUiAsync(new
                    {
                        stage = p.Stage.ToString().ToLowerInvariant(),
                        percent = p.Percent,
                        pingMs = p.PingMs,
                        downloadMbps = p.DownloadMbps,
                        uploadMbps = p.UploadMbps
                    });
                });

                var result = (_server == null || _useExistingVpnTunnel)
                    ? await SpeedTestRunner.RunDirectAsync(progress, _cts.Token)
                    : await SpeedTestRunner.RunAsync(_server, progress, _cts.Token);

                StatusText.Text = "SpeedTest завершён";
                await PostToUiAsync(new
                {
                    stage = "done",
                    percent = 100,
                    pingMs = result.PingMs,
                    downloadMbps = result.DownloadMbps,
                    uploadMbps = result.UploadMbps
                });

                try
                {
                    if (_server != null)
                    {
                        if (result.PingMs.HasValue)
                            Database.Instance.UpdateServerPing(_server.Id, (int)Math.Round(result.PingMs.Value));

                        if (result.DownloadMbps.HasValue)
                        {
                            var kbps = (long)Math.Round(result.DownloadMbps.Value * 128.0); // ~KB/s
                            Database.Instance.UpdateServerSpeed(_server.Id, kbps, 0, (int)SpeedTestStatus.Ok, null);
                        }
                        else
                        {
                            Database.Instance.UpdateServerSpeed(_server.Id, 0, 0, (int)SpeedTestStatus.Error, "no-data");
                        }
                    }
                }
                catch { }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Отменено";
                await PostToUiAsync(new { stage = "error", message = "canceled" });
            }
            catch (Exception ex)
            {
                try
                {
                    var errPath = Path.Combine(Path.GetTempPath(), "ByeWhiteList-VPN_speedtest_last_error.txt");
                    var details = ex.ToString();
                    File.WriteAllText(errPath, details);
                    _lastErrorFilePath = errPath;
                    _lastErrorDetails = details;
                    StatusText.Text = $"Ошибка speedtest: {ex.Message}\nДетали: {errPath}\n(Нажмите ⧉ чтобы скопировать)";
                }
                catch
                {
                    StatusText.Text = $"Ошибка speedtest: {ex.Message}";
                }

                await PostToUiAsync(new { stage = "error", message = ex.Message });
            }
            finally
            {
                // Allow a re-run from UI without reopening the window.
                _testStarted = false;
                await PostToUiAsync(new { stage = "ready" });
            }
        }

        private Task PostToUiAsync(object payload)
        {
            try
            {
                if (WebView.CoreWebView2 == null)
                    return Task.CompletedTask;
                var json = JsonSerializer.Serialize(payload);
                WebView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch { }
            return Task.CompletedTask;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void CopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string text;

                if (!string.IsNullOrWhiteSpace(_lastErrorFilePath) && File.Exists(_lastErrorFilePath))
                {
                    text = File.ReadAllText(_lastErrorFilePath);
                }
                else if (!string.IsNullOrWhiteSpace(_lastErrorDetails))
                {
                    text = _lastErrorDetails;
                }
                else
                {
                    // Fallback: at least copy what user sees.
                    text = _server == null
                        ? $"Target: Direct network\nStatus: {StatusText.Text}"
                        : $"Target: {_server.DisplayName()}\nStatus: {StatusText.Text}";
                }

                System.Windows.Clipboard.SetText(text);
                StatusText.Text = "Лог скопирован в буфер обмена";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Не удалось скопировать лог: {ex.Message}";
            }
        }

        private void Overlay_MouseDown(object sender, MouseButtonEventArgs e) => Close();

        private void Card_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    }
}
