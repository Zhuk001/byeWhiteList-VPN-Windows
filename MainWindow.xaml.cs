using ByeWhiteList.Windows.Data;
using ByeWhiteList.Windows.Helpers;
using ByeWhiteList.Windows.Models;
using ByeWhiteList.Windows.Services;
using MaterialDesignThemes.Wpf;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

// Убираем конфликт с WinForms
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;

namespace ByeWhiteList.Windows
{
    public partial class MainWindow : Window
    {
        private XrayService? _xrayService;
        private ProxyEntity? _selectedServer;
        private bool _isVpnTransition;
        private bool _tabHeaderWheelHooked;
        private readonly Dictionary<long, double> _savedListOffsets = new Dictionary<long, double>();
        private bool _suppressListScrollSave;
        private readonly Dictionary<long, List<ProxyEntity>> _sortedServersCache = new Dictionary<long, List<ProxyEntity>>();
        private long _lastActiveGroupId = 3L;
        private void ShowLanguageDialog()
        {
            var dialog = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = MediaBrushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true
            };

            var overlay = new Grid { Background = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0)) };
            overlay.MouseDown += (_, __) => dialog.Close();

            var card = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x24, 0x24, 0x24)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16),
                Width = 360,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            card.MouseDown += (_, e) => e.Handled = true;

            var root = new StackPanel();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.Children.Add(new TextBlock
            {
                Text = "Язык",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = MediaBrushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });

            var closeBtn = new Button
            {
                Width = 28,
                Height = 28,
                Background = MediaBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0),
                Content = new PackIcon
                {
                    Kind = PackIconKind.Close,
                    Width = 18,
                    Height = 18,
                    Foreground = MediaBrushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                }
            };

            closeBtn.Click += (_, _) => dialog.Close();
            closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B));
            closeBtn.MouseLeave += (_, _) => closeBtn.Background = MediaBrushes.Transparent;

            Grid.SetColumn(closeBtn, 1);
            header.Children.Add(closeBtn);
            root.Children.Add(header);

            root.Children.Add(new TextBlock
            {
                Text = "Выберите язык интерфейса:",
                Foreground = MediaBrushes.Gray,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });

            Button MakeLanguageButton(string title, string code)
            {
                var btn = new Button
                {
                    Content = title,
                    Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B)),
                    Foreground = MediaBrushes.White,
                    BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 0, 0, 10),
                    Cursor = Cursors.Hand,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                    Template = CreateRoundedButtonTemplate(12)
                };

                btn.Click += (_, _) =>
                {
                    StatusText.Text = code == "ru"
                        ? "🌐 Выбран язык: Русский"
                        : "🌐 Selected language: English";

                    dialog.Close();
                };

                return btn;
            }

            root.Children.Add(MakeLanguageButton("🇷🇺 Русский", "ru"));
            root.Children.Add(MakeLanguageButton("🇬🇧 English", "en"));

            card.Child = root;
            overlay.Children.Add(card);
            dialog.Content = overlay;
            dialog.ShowDialog();
        }
        private enum VpnUiState
        {
            Off,
            Connecting,
            On,
            Disconnecting
        }
        private async Task<bool> EnsureXrayReadyAsync(string reason)
        {
            try
            {
                var downloaded = Dispatcher.CheckAccess()
                    ? await EnsureXrayDownloadedWithDialogAsync()
                    : await Dispatcher.InvokeAsync(EnsureXrayDownloadedWithDialogAsync).Task.Unwrap();

                if (downloaded)
                    return true;

                if (Dispatcher.CheckAccess())
                {
                    StatusText.Text = "❌ Xray не скачан.";
                }
                else
                {
                    await Dispatcher.InvokeAsync(() => StatusText.Text = "❌ Xray не скачан.");
                }
                LogManager.Add($"❌ Xray не скачан ({reason})");

                if (Dispatcher.CheckAccess())
                {
                    SetVpnUiState(VpnUiState.Off);
                }
                else
                {
                    await Dispatcher.InvokeAsync(() => SetVpnUiState(VpnUiState.Off));
                }
                return false;
            }
            catch (Exception ex)
            {
                if (Dispatcher.CheckAccess())
                {
                    StatusText.Text = $"❌ Ошибка Xray: {ex.Message}";
                }
                else
                {
                    await Dispatcher.InvokeAsync(() => StatusText.Text = $"❌ Ошибка Xray: {ex.Message}");
                }
                LogManager.Add($"❌ Ошибка Xray ({reason}): {ex.Message}");

                if (Dispatcher.CheckAccess())
                {
                    SetVpnUiState(VpnUiState.Off);
                }
                else
                {
                    await Dispatcher.InvokeAsync(() => SetVpnUiState(VpnUiState.Off));
                }
                return false;
            }
        }
        private async Task<bool> EnsureXrayDownloadedWithDialogAsync()
        {
            try
            {
                var existing = XrayBootstrapper.ResolveExistingXrayPath();
                if (File.Exists(existing))
                    return true;

                var preferred = XrayBootstrapper.GetPreferredInstallPath();

                var dialog = new Window
                {
                    Owner = this,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = MediaBrushes.Transparent,
                    ResizeMode = ResizeMode.NoResize,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    ShowInTaskbar = false,
                    Topmost = true
                };

                var overlay = new Grid { Background = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0)) };
                overlay.MouseDown += (_, __) => dialog.Close();

                var card = new Border
                {
                    Background = new SolidColorBrush(MediaColor.FromRgb(0x1F, 0x1F, 0x1F)),
                    BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(16),
                    Padding = new Thickness(18),
                    Width = 520,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };
                card.MouseDown += (_, e) => e.Handled = true;

                var root = new StackPanel();

                {
                    var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    header.Children.Add(new TextBlock
                    {
                        Text = "Скачать Xray",
                        FontSize = 15,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = MediaBrushes.White,
                        VerticalAlignment = VerticalAlignment.Center
                    });

                    var closeBtn = new Button
                    {
                        Width = 28,
                        Height = 28,
                        Background = MediaBrushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Padding = new Thickness(0),
                        Content = new PackIcon
                        {
                            Kind = PackIconKind.Close,
                            Width = 18,
                            Height = 18,
                            Foreground = MediaBrushes.White,
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                        }
                    };
                    closeBtn.Click += (_, _) => dialog.Close();
                    closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B));
                    closeBtn.MouseLeave += (_, _) => closeBtn.Background = MediaBrushes.Transparent;
                    Grid.SetColumn(closeBtn, 1);
                    header.Children.Add(closeBtn);

                    root.Children.Add(header);
                }

                var info = new TextBlock
                {
                    Text =
                        "Для работы VPN нужен компонент Xray (xray.exe).\n\n" +
                        "Скачать Xray сейчас?\n\n" +
                        "Файл будет сохранён туда, где у приложения есть права:\n" +
                        $"• {preferred}",
                    Foreground = MediaBrushes.White,
                    Opacity = 0.9,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                root.Children.Add(info);

                var status = new TextBlock
                {
                    Text = "",
                    Foreground = MediaBrushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                root.Children.Add(status);

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right
                };

                bool result = false;

                var downloadBtn = new Button
                {
                    Content = "✅ Скачать",
                    Width = 160,
                    Margin = new Thickness(0, 0, 10, 0),
                    Background = new SolidColorBrush(MediaColor.FromRgb(76, 175, 80)),
                    Foreground = MediaBrushes.White,
                    Cursor = Cursors.Hand,
                    Template = CreateRoundedButtonTemplate(14)
                };

                var cancelBtn = new Button
                {
                    Content = "❌ Отмена",
                    Width = 160,
                    Background = new SolidColorBrush(MediaColor.FromRgb(244, 67, 54)),
                    Foreground = MediaBrushes.White,
                    Cursor = Cursors.Hand,
                    Template = CreateRoundedButtonTemplate(14)
                };

                cancelBtn.Click += (_, _) =>
                {
                    result = false;
                    dialog.Close();
                };

                downloadBtn.Click += async (_, _) =>
                {
                    downloadBtn.IsEnabled = false;
                    cancelBtn.IsEnabled = false;
                    status.Text = "⏳ Скачиваю Xray…";

                    try
                    {
                        var installedPath = await XrayBootstrapper.DownloadLatestXrayAsync(
                            msg => Dispatcher.Invoke(() => status.Text = msg),
                            CancellationToken.None);

                        if (File.Exists(installedPath))
                        {
                            result = true;
                            dialog.Close();
                        }
                        else
                        {
                            status.Text = "❌ Не удалось сохранить xray.exe.";
                        }
                    }
                    catch (Exception ex)
                    {
                        status.Text = $"❌ Ошибка: {ex.Message}";
                    }
                    finally
                    {
                        if (dialog.IsVisible)
                        {
                            downloadBtn.IsEnabled = true;
                            cancelBtn.IsEnabled = true;
                        }
                    }
                };

                btnPanel.Children.Add(downloadBtn);
                btnPanel.Children.Add(cancelBtn);
                root.Children.Add(btnPanel);

                card.Child = root;
                overlay.Children.Add(card);
                dialog.Content = overlay;
                dialog.ShowDialog();

                // Re-check both locations after dialog closes.
                var after = XrayBootstrapper.ResolveExistingXrayPath();
                return result && File.Exists(after);
            }
            catch
            {
                return false;
            }
        }

        private readonly List<ProxyGroup> _groups = new List<ProxyGroup>();
        private readonly Dictionary<long, List<ProxyEntity>> _serversCache = new Dictionary<long, List<ProxyEntity>>();
        private void PurgeGroupFromMemory(long groupId)
        {
            _serversCache.Remove(groupId);
            _sortedServersCache.Remove(groupId);
            _savedListOffsets.Remove(groupId);
            if (_lastActiveGroupId == groupId)
                _lastActiveGroupId = 3;
        }

        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _exitOnClose;
        private bool _shutdownInProgress;
        private enum OnboardingStep
        {
            None,
            NeedUpdateServers,
            NeedUpdateSpeed,
            NeedSelectServer,
            NeedConnectVpn,
            Done
        }
        private bool _onboardingActive;
        private OnboardingStep _onboardingStep = OnboardingStep.None;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SizeChanged += (_, _) => ShowTutorialOverlayForCurrentStep();
            LocationChanged += (_, _) => ShowTutorialOverlayForCurrentStep();

            LogManager.OnLogAdded += _ => Dispatcher.Invoke(() => { });
            LogManager.Add("🚀 Приложение запущено");

            _xrayService = new XrayService();
            _xrayService.OnLogMessage += msg =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (IsUserStatusMessage(msg))
                        StatusText.Text = msg;
                });
            };
            _xrayService.OnConnectionStateChanged += isConnected =>
            {
                Dispatcher.Invoke(() => UpdateVpnButtonState(isConnected));
            };

            _xrayService.OnSpeedUpdate += (download, upload) =>
            {
                Dispatcher.Invoke(() =>
                {
                    SpeedDownloadText.Text = $"⬇ {download}";
                    SpeedUploadText.Text = $"⬆ {upload}";
                });
            };

            // AutoMode/BlockedMode removed.

            await LoadGroups();
            await LoadAllServers();
            CreateTabs();
            HookTabHeaderWheel();

            // Ensure VPN button is in a known visual state on launch.
            SetVpnUiState(_xrayService.IsRunning ? VpnUiState.On : VpnUiState.Off);

            // AutoMode removed.

            try
            {
                if (AppSettings.Instance.IsFirstRun ||
                    string.IsNullOrWhiteSpace(AppSettings.Instance.GeoRoutingProfileId))
                {
                    var w = new RoutingSettingsWindow { Owner = this };
                    w.ShowDialog();

                    ShowSmallInfoDialog("Подготовка", "Скачиваю свежие geosite/geoip в фоне. Это нужно один раз перед первым подключением.");
                    try { _ = Task.Run(async () => await GeoDataUpdater.UpdateGeoDataAsync(force: true)); } catch { }

                    AppSettings.Instance.IsFirstRun = false;
                    AppSettings.Instance.Save();

                    _onboardingActive = true;
                    AdvanceOnboarding(OnboardingStep.NeedUpdateServers);
                }
            }
            catch { }

            // Background geodata auto-update (never blocks startup).
            try
            {
                if (AppSettings.Instance.GeoDataAutoUpdateOnStartup)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await GeoDataUpdater.UpdateGeoDataAsync(force: false); } catch { }
                    });
                }
            }
            catch { }
        }

        private async void RoutingSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var menu = new SettingsMenuWindow
                {
                    Owner = this
                };

                bool? chosen = menu.ShowDialog();
                if (chosen != true || menu.SelectedAction == null)
                    return;

                switch (menu.SelectedAction.Value)
                {
                    case SettingsMenuAction.RoutingRules:
                    {
                        var w = new RoutingSettingsWindow { Owner = this };
                        bool? saved = w.ShowDialog();
                        if (saved == true)
                        {
                            StatusText.Text = "⚙ Обновлены исключения маршрутизации. Переподключите VPN.";
                            LogManager.Add("⚙ Обновлены исключения маршрутизации. Требуется переподключение VPN.");
                        }
                        break;
                    }

                    case SettingsMenuAction.Speedtest:
                        {
                            ProxyEntity? target = null;

                            if (_xrayService != null && _xrayService.IsRunning)
                                target = _xrayService.CurrentServer ?? _selectedServer;

                            var useExistingTunnel = _xrayService?.IsRunning == true;

                            // ❗ Проверяем Xray только если НЕ используем уже активный VPN
                            if (!useExistingTunnel)
                            {
                                if (!await EnsureXrayReadyAsync("speedtest"))
                                    break;
                            }

                            new SpeedTestWindow(target, useExistingVpnTunnel: useExistingTunnel)
                            {
                                Owner = this
                            }.ShowDialog();

                            break;
                        }

                    case SettingsMenuAction.News:
                        await ShowNewsDialogAsync();
                        break;

                    case SettingsMenuAction.AppUpdate:
                        await ShowAppUpdateDialogAsync();
                        break;

                    case SettingsMenuAction.Feedback:
                        OpenFeedbackEmail();
                        break;

                    case SettingsMenuAction.StartTutorial:
                        await StartOnboardingFromSettingsAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                LogManager.Add($"⚠️ Не удалось открыть настройки маршрутизации: {ex.Message}");
            }
        }

        private async Task StartOnboardingFromSettingsAsync()
        {
            var w = new RoutingSettingsWindow { Owner = this };
            w.ShowDialog();

            ShowSmallInfoDialog("Подготовка", "Обновляю geosite/geoip в фоне и запускаю обучение.");
            _ = Task.Run(async () =>
            {
                try { await GeoDataUpdater.UpdateGeoDataAsync(force: true); } catch { }
            });

            _onboardingActive = true;
            AdvanceOnboarding(OnboardingStep.NeedUpdateServers);
            await Task.CompletedTask;
        }

        private void ShowSmallInfoDialog(string title, string message)
        {
            var dialog = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = MediaBrushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true
            };

            var card = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x24, 0x24, 0x24)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16)
            };

            var root = new StackPanel { Width = 340 };

            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = MediaBrushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            var closeBtn = new Button
            {
                Width = 28,
                Height = 28,
                Background = MediaBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0),
                Content = new PackIcon
                {
                    Kind = PackIconKind.Close,
                    Width = 18,
                    Height = 18,
                    Foreground = MediaBrushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                }
            };
            closeBtn.Click += (_, _) => dialog.Close();
            closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B));
            closeBtn.MouseLeave += (_, _) => closeBtn.Background = MediaBrushes.Transparent;

            Grid.SetColumn(closeBtn, 1);
            header.Children.Add(closeBtn);

            root.Children.Add(header);

            root.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 13,
                Foreground = MediaBrushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });

            var okBtn = new Button
            {
                Content = "OK",
                Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B)),
                Foreground = MediaBrushes.White,
                Padding = new Thickness(12, 8, 12, 8),
                Cursor = Cursors.Hand,
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                Template = CreateRoundedButtonTemplate(12)
            };
            okBtn.Click += (_, _) => dialog.Close();
            root.Children.Add(okBtn);

            card.Child = root;
            dialog.Content = card;
            dialog.ShowDialog();
        }

        private void ApplyOnboardingUiState()
        {
            if (!_onboardingActive)
            {
                AddServerButton.IsEnabled = true;
                UpdateServersButton.IsEnabled = true;
                RoutingSettingsButton.IsEnabled = true;
                MinimizeToTrayButton.IsEnabled = true;
                VpnButton.IsEnabled = !_isVpnTransition;
                TutorialOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            AddServerButton.IsEnabled = false;
            RoutingSettingsButton.IsEnabled = false;
            MinimizeToTrayButton.IsEnabled = false;
            VpnButton.IsEnabled = _onboardingStep == OnboardingStep.NeedConnectVpn && !_isVpnTransition;
            UpdateServersButton.IsEnabled = _onboardingStep == OnboardingStep.NeedUpdateServers || _onboardingStep == OnboardingStep.NeedUpdateSpeed;
            ShowTutorialOverlayForCurrentStep();
        }

        private FrameworkElement? GetOnboardingTarget()
        {
            return _onboardingStep switch
            {
                OnboardingStep.NeedUpdateServers => UpdateServersButton,
                OnboardingStep.NeedUpdateSpeed => UpdateServersButton,
                OnboardingStep.NeedSelectServer => ServersTabControl,
                OnboardingStep.NeedConnectVpn => VpnButton,
                _ => null
            };
        }

        private string GetOnboardingText()
        {
            return _onboardingStep switch
            {
                OnboardingStep.NeedUpdateServers => "Шаг 1/4: Нажмите «Обновить сервера», чтобы получить актуальный список.",
                OnboardingStep.NeedUpdateSpeed => "Шаг 2/4: Нажмите «Обновить скорость». Самые быстрые серверы будут сверху.",
                OnboardingStep.NeedSelectServer => "Шаг 3/4: Выберите сервер из списка (лучше первый сверху).",
                OnboardingStep.NeedConnectVpn => "Шаг 4/4: Нажмите «Включить VPN».",
                _ => ""
            };
        }

        private void ShowTutorialOverlayForCurrentStep()
        {
            if (!_onboardingActive || _onboardingStep == OnboardingStep.None || _onboardingStep == OnboardingStep.Done)
            {
                TutorialOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            var target = GetOnboardingTarget();
            if (target == null || !target.IsLoaded)
            {
                TutorialOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            TutorialText.Text = GetOnboardingText();
            TutorialOverlay.Visibility = Visibility.Visible;

            Dispatcher.InvokeAsync(() =>
            {
                target.UpdateLayout();
                TutorialBubble.UpdateLayout();

                var topLeft = target.TranslatePoint(new System.Windows.Point(0, 0), this);
                var width = Math.Max(40, target.ActualWidth);
                var height = Math.Max(40, target.ActualHeight);

                Canvas.SetLeft(TutorialHighlight, Math.Max(0, topLeft.X - 6));
                Canvas.SetTop(TutorialHighlight, Math.Max(0, topLeft.Y - 6));
                TutorialHighlight.Width = width + 12;
                TutorialHighlight.Height = height + 12;

                var bubbleLeft = Math.Max(10, Math.Min(ActualWidth - TutorialBubble.Width - 10, topLeft.X));
                var bubbleTop = topLeft.Y + height + 14;
                if (bubbleTop + TutorialBubble.ActualHeight > ActualHeight - 10)
                    bubbleTop = Math.Max(10, topLeft.Y - TutorialBubble.ActualHeight - 14);

                Canvas.SetLeft(TutorialBubble, bubbleLeft);
                Canvas.SetTop(TutorialBubble, Math.Max(10, bubbleTop));
            });
        }

        private void AdvanceOnboarding(OnboardingStep nextStep)
        {
            _onboardingStep = nextStep;
            ApplyOnboardingUiState();

            switch (_onboardingStep)
            {
                case OnboardingStep.NeedUpdateServers:
                    ShowTutorialOverlayForCurrentStep();
                    break;
                case OnboardingStep.NeedUpdateSpeed:
                    ShowTutorialOverlayForCurrentStep();
                    break;
                case OnboardingStep.NeedSelectServer:
                    ShowTutorialOverlayForCurrentStep();
                    break;
                case OnboardingStep.NeedConnectVpn:
                    ShowTutorialOverlayForCurrentStep();
                    break;
                case OnboardingStep.Done:
                    _onboardingActive = false;
                    _onboardingStep = OnboardingStep.None;
                    ApplyOnboardingUiState();
                    ShowSmallInfoDialog("Готово", "Обучение завершено. Теперь вы можете пользоваться приложением.");
                    break;
            }
        }

        private async Task ShowAppUpdateDialogAsync()
        {
            var dialog = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = MediaBrushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true
            };

            var overlay = new Grid { Background = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0)) };
            overlay.MouseDown += (_, __) => dialog.Close();

            var card = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x24, 0x24, 0x24)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16),
                Width = 520,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            card.MouseDown += (_, e) => e.Handled = true;

            var root = new StackPanel();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.Children.Add(new TextBlock
            {
                Text = "Обновление приложения",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = MediaBrushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });

            var closeBtn = new Button
            {
                Width = 28,
                Height = 28,
                Background = MediaBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0),
                Content = new PackIcon
                {
                    Kind = PackIconKind.Close,
                    Width = 18,
                    Height = 18,
                    Foreground = MediaBrushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                }
            };
            closeBtn.Click += (_, _) => dialog.Close();
            closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B));
            closeBtn.MouseLeave += (_, _) => closeBtn.Background = MediaBrushes.Transparent;

            Grid.SetColumn(closeBtn, 1);
            header.Children.Add(closeBtn);
            root.Children.Add(header);

            var currentVersion = GitHubReleaseService.GetCurrentVersionString();
            var versionText = new TextBlock
            {
                Text = $"Текущая версия: {currentVersion}",
                Foreground = MediaBrushes.Gray,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(versionText);

            var statusText = new TextBlock
            {
                Text = "⏳ Проверяю обновления...",
                Foreground = MediaBrushes.Gray,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(statusText);

            var notesBox = new TextBox
            {
                IsReadOnly = true,
                Text = "",
                Background = new SolidColorBrush(MediaColor.FromRgb(0x1A, 0x1A, 0x1A)),
                Foreground = MediaBrushes.White,
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(10),
                Height = 210,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(notesBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            var updateBtn = new Button
            {
                Content = "✅ Обновить",
                Width = 140,
                Margin = new Thickness(0, 0, 10, 0),
                Background = MediaBrushes.Green,
                Foreground = MediaBrushes.White,
                Cursor = Cursors.Hand,
                IsEnabled = false,
                Template = CreateRoundedButtonTemplate(14)
            };

            var cancelBtn = new Button
            {
                Content = "❌ Отмена",
                Width = 140,
                Background = MediaBrushes.IndianRed,
                Foreground = MediaBrushes.White,
                Cursor = Cursors.Hand,
                Template = CreateRoundedButtonTemplate(14)
            };
            cancelBtn.Click += (_, _) => dialog.Close();

            btnPanel.Children.Add(updateBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            card.Child = root;
            overlay.Children.Add(card);
            dialog.Content = overlay;

            GitHubReleaseInfo? latest = null;
            GitHubAsset? bestAsset = null;

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            _ = Task.Run(async () =>
            {
                try
                {
                    latest = await GitHubReleaseService.GetLatestReleaseAsync(cts.Token);
                    if (latest == null)
                    {
                        Dispatcher.Invoke(() => statusText.Text = "❌ Не удалось получить список релизов.");
                        return;
                    }

                    bestAsset = GitHubReleaseService.PickBestAsset(latest.Assets);
                    var isNew = GitHubReleaseService.IsNewerThanCurrent(latest.Version);

                    Dispatcher.Invoke(() =>
                    {
                        statusText.Text = isNew
                            ? $"✅ Доступно обновление: {latest.Tag}"
                            : $"✅ У вас последняя версия: {latest.Tag}";

                        notesBox.Text = string.IsNullOrWhiteSpace(latest.Body)
                            ? "(описание релиза пустое)"
                            : latest.Body.Trim();

                        updateBtn.IsEnabled = isNew && bestAsset != null && bestAsset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                        if (bestAsset == null)
                            statusText.Text += " (нет файлов релиза)";
                        else if (!bestAsset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            statusText.Text += $" (авто‑обновление поддерживает только .zip: найден {bestAsset.Name})";
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => statusText.Text = $"❌ Ошибка проверки: {ex.Message}");
                }
            });

            updateBtn.Click += async (_, _) =>
            {
                if (latest == null || bestAsset == null)
                    return;

                try
                {
                    updateBtn.IsEnabled = false;
                    cancelBtn.IsEnabled = false;

                    statusText.Text = $"⏳ Скачиваю {bestAsset.Name}...";

                    var currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    var currentDir = string.IsNullOrWhiteSpace(currentExePath)
                        ? AppDomain.CurrentDomain.BaseDirectory
                        : Path.GetDirectoryName(currentExePath)!;

                    if (!CanWriteToDirectory(currentDir, out var permError))
                    {
                        statusText.Text = $"❌ Нет прав на запись в папку программы.\n{permError}\n\nРекомендуется использовать портативную (.zip) версию в папке пользователя.";
                        cancelBtn.IsEnabled = true;
                        return;
                    }
 
                    var tmpRoot = Path.Combine(Path.GetTempPath(), $"byewhitelist_update_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(tmpRoot);
 
                    var zipPath = Path.Combine(tmpRoot, bestAsset.Name);

                    await GitHubReleaseService.DownloadFileAsync(bestAsset.DownloadUrl, zipPath,
                        (done, total) =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (total.HasValue && total.Value > 0)
                                {
                                    var pct = (int)Math.Round(done * 100.0 / total.Value);
                                    statusText.Text = $"⏳ Скачиваю {bestAsset.Name}... {pct}%";
                                }
                                else
                                {
                                    statusText.Text = $"⏳ Скачиваю {bestAsset.Name}... {done / (1024 * 1024)} MB";
                                }
                            });
                        },
                        CancellationToken.None);
 
                    statusText.Text = "⏳ Распаковываю...";
                    var extractDir = Path.Combine(tmpRoot, "extracted");
                    await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true));
 
                    // Find the first directory that contains an .exe with our name; otherwise use root.
                    var appExe = Path.GetFileName(Process.GetCurrentProcess().MainModule?.FileName ?? "byeWhiteList.exe");
                    var extractedRoot = await Task.Run(() => FindExtractedRoot(extractDir, appExe) ?? extractDir);
 
                    statusText.Text = "⏳ Применяю обновление...";
 
                    var cmdPath = Path.Combine(tmpRoot, "apply_update.cmd");
                    var cmd = BuildUpdateCmd(
                        currentPid: Process.GetCurrentProcess().Id,
                        sourceDir: extractedRoot,
                        targetDir: currentDir,
                        exeName: appExe);

                    await File.WriteAllTextAsync(cmdPath, cmd, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"\"{cmdPath}\"\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
 
                    // Close current app; updater script will restart it.
                    System.Windows.Application.Current.Shutdown();
                }
                catch (Exception ex)
                {
                    statusText.Text = $"❌ Не удалось обновить: {ex.Message}";
                    cancelBtn.IsEnabled = true;
                }
            };

            dialog.ShowDialog();
        }

        private static string? FindExtractedRoot(string extractDir, string expectedExeName)
        {
            try
            {
                var direct = Path.Combine(extractDir, expectedExeName);
                if (File.Exists(direct))
                    return extractDir;

                foreach (var dir in Directory.GetDirectories(extractDir, "*", SearchOption.AllDirectories))
                {
                    var p = Path.Combine(dir, expectedExeName);
                    if (File.Exists(p))
                        return dir;
                }
            }
            catch { }

            return null;
        }

        private static string BuildUpdateCmd(int currentPid, string sourceDir, string targetDir, string exeName)
        {
            // NOTE: no admin escalation here; if app is installed into a protected folder, copy may fail.
            // We keep it simple and safe: best-effort copy, then start the app.
            var src = SanitizeForCmdSet(sourceDir);
            var dst = SanitizeForCmdSet(targetDir);
            var exe = SanitizeForCmdSet(exeName);

            return
$@"@echo off
setlocal
chcp 65001 >nul

set ""PID={currentPid}""
set ""SRC={src}""
set ""DST={dst}""
set ""EXE={exe}""

REM wait for app to exit
:wait
tasklist /fi ""pid eq %PID%"" | findstr /i ""%PID%"" >nul
if %errorlevel%==0 (
  ping 127.0.0.1 -n 2 >nul
  goto wait
)

REM copy files (portable layout)
robocopy ""%SRC%"" ""%DST%"" /E /R:2 /W:1 >nul

REM restart
start """" ""%DST%\%EXE%""
endlocal
";
        }

        private static string QuoteForCmd(string path)
        {
            path = path.Replace("\"", "");
            return $"\"{path}\"";
        }

        private static string SanitizeForCmdSet(string value)
        {
            value = value ?? "";
            value = value.Replace("\"", "");
            value = value.Replace("\r", "").Replace("\n", "");
            return value;
        }

        private static bool CanWriteToDirectory(string directoryPath, out string? errorMessage)
        {
            errorMessage = null;
            try
            {
                Directory.CreateDirectory(directoryPath);
                var testFile = Path.Combine(directoryPath, $".write_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(testFile, "ok", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Delete(testFile);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private void UpdateVpnButtonState(bool isConnected)
        {
            if (_isVpnTransition)
                return;

            SetVpnUiState(isConnected ? VpnUiState.On : VpnUiState.Off);
        }

        private static bool IsUserStatusMessage(string? msg)
        {
            msg = msg ?? "";
            if (string.IsNullOrWhiteSpace(msg))
                return false;

            // Drop noisy technical logs; keep only high-level statuses.
            if (msg.StartsWith("📢", StringComparison.Ordinal) ||
                msg.StartsWith("⚠️ stderr", StringComparison.OrdinalIgnoreCase) ||
                msg.StartsWith("stdout:", StringComparison.OrdinalIgnoreCase) ||
                msg.StartsWith("stderr:", StringComparison.OrdinalIgnoreCase) ||
                msg.StartsWith("🔎", StringComparison.Ordinal) ||
                msg.StartsWith("🧾", StringComparison.Ordinal) ||
                msg.StartsWith("📝", StringComparison.Ordinal) ||
                msg.StartsWith("🔍", StringComparison.Ordinal) ||
                msg.StartsWith("⏳", StringComparison.Ordinal) ||
                msg.StartsWith("📢 stdout", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Allow common high-level state icons.
            return msg.StartsWith("🟢", StringComparison.Ordinal) ||
                   msg.StartsWith("🔴", StringComparison.Ordinal) ||
                   msg.StartsWith("🔌", StringComparison.Ordinal) ||
                   msg.StartsWith("✅", StringComparison.Ordinal) ||
                   msg.StartsWith("❌", StringComparison.Ordinal) ||
                   msg.StartsWith("⚡", StringComparison.Ordinal) ||
                   msg.StartsWith("📡", StringComparison.Ordinal) ||
                   msg.StartsWith("🔄", StringComparison.Ordinal) ||
                   msg.StartsWith("⚙", StringComparison.Ordinal) ||
                   msg.StartsWith("VPN", StringComparison.OrdinalIgnoreCase);
        }

        private void SetVpnUiState(VpnUiState state)
        {
            var border = VpnButton.Template.FindName("border", VpnButton) as Border;
            var textBlock = VpnButton.Template.FindName("textBlock", VpnButton) as TextBlock;

            switch (state)
            {
                case VpnUiState.Off:
                    StopVpnPulse(border);
                    VpnButton.Background = new SolidColorBrush(MediaColor.FromRgb(244, 67, 54));
                    if (textBlock != null) textBlock.Text = "VPN\nВЫКЛ";
                    break;

                case VpnUiState.Connecting:
                    VpnButton.Background = new SolidColorBrush(MediaColor.FromRgb(255, 193, 7));
                    if (textBlock != null) textBlock.Text = "VPN\nПОДКЛ";
                    StartVpnPulse(border);
                    break;

                case VpnUiState.Disconnecting:
                    VpnButton.Background = new SolidColorBrush(MediaColor.FromRgb(255, 193, 7));
                    if (textBlock != null) textBlock.Text = "VPN\nОТКЛ";
                    StartVpnPulse(border);
                    break;

                case VpnUiState.On:
                    StopVpnPulse(border);
                    VpnButton.Background = new SolidColorBrush(MediaColor.FromRgb(76, 175, 80));
                    if (textBlock != null) textBlock.Text = "VPN\nВКЛ";
                    break;
            }

            if (textBlock != null)
                textBlock.Foreground = MediaBrushes.White;
        }

        private static void StartVpnPulse(Border? border)
        {
            if (border == null)
                return;

            border.BeginAnimation(UIElement.OpacityProperty, null);
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1.0,
                To = 0.65,
                Duration = new Duration(TimeSpan.FromMilliseconds(550)),
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };
            border.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private static void StopVpnPulse(Border? border)
        {
            if (border == null)
                return;
            border.BeginAnimation(UIElement.OpacityProperty, null);
            border.Opacity = 1.0;
        }

        private async Task RunPingTestOnServers()
        {
            try
            {
                LogManager.Add("📡 Запуск пинг-теста серверов...");

                var allServers = Database.Instance.GetAllServers();
                if (allServers.Count == 0)
                {
                    LogManager.Add("⚠️ Нет серверов для пинг-теста");
                    return;
                }

                LogManager.Add($"📡 Тестируем {allServers.Count} серверов...");

                var results = await FastUrlTester.TestProfilesParallel(allServers, 10, 3000);

                var successCount = 0;
                foreach (var result in results)
                {
                    var server = result.Server;
                    var ping = result.Ping;

                    LogManager.Add($"Результат: {server?.DisplayName()} = {(ping > 0 ? $"{ping}ms" : "ошибка")}");

                    if (ping > 0 && server != null)
                    {
                        successCount++;
                        Database.Instance.UpdateServerPing(server.Id, ping);
                    }
                    else if (server != null)
                    {
                        Database.Instance.UpdateServerPing(server.Id, -1);
                    }
                }

                LogManager.Add($"✅ Пинг-тест завершён: {successCount}/{results.Count} серверов отвечают");

                await LoadAllServers();
                CreateTabs();
            }
            catch (Exception ex)
            {
                LogManager.Add($"❌ Ошибка пинг-теста: {ex.Message}");
            }
        }

        private async Task RunPingTestOnActiveTab()
        {
            if (ServersTabControl.SelectedItem is not TabItem tab || tab.Tag is not long groupId || groupId <= 0)
                return;

            await RunPingTestOnGroupAsync(groupId);
        }

        private async Task LoadGroups()
        {
            try
            {
                Database.Instance.CleanupOrphanServers();
                _groups.Clear();
                var db = Database.Instance;
                await using var connection = db.GetConnection();
                await connection.OpenAsync();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT id, name, type, user_order, subscription_url FROM proxy_groups ORDER BY user_order";

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    _groups.Add(new ProxyGroup
                    {
                        Id = reader.GetInt64(0),
                        Name = reader.GetString(1),
                        Type = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                        UserOrder = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                        SubscriptionUrl = reader.IsDBNull(4) ? null : reader.GetString(4)
                    });
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка загрузки групп: {ex.Message}";
                LogManager.Add($"❌ Ошибка загрузки групп: {ex.Message}");
            }
        }

        private async Task LoadAllServers()
        {
            try
            {
                _sortedServersCache.Clear();
                _serversCache.Clear();
                var db = Database.Instance;
                await using var connection = db.GetConnection();
                await connection.OpenAsync();

                foreach (var group in _groups)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT id, group_id, bean_json, ping, speed_kbps, speed_test_bytes, speed_test_status, speed_test_error, status, name FROM proxy_entities WHERE group_id = @groupId";
                    cmd.Parameters.AddWithValue("@groupId", group.Id);

                    var servers = new List<ProxyEntity>();
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        servers.Add(new ProxyEntity
                        {
                            Id = reader.GetInt64(0),
                            GroupId = reader.GetInt64(1),
                            BeanJson = reader.GetString(2),
                            Ping = reader.GetInt32(3),
                            SpeedKbps = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                            SpeedTestBytes = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                            SpeedTestStatus = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                            SpeedTestError = reader.IsDBNull(7) ? null : reader.GetString(7),
                            Status = reader.GetInt32(8),
                            Name = reader.IsDBNull(9) ? null : reader.GetString(9)
                        });
                    }
                    _serversCache[group.Id] = servers;
                }

                LogManager.Add($"📊 Загружено серверов: {_serversCache.Values.Sum(v => v.Count)}");
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка загрузки серверов: {ex.Message}";
                LogManager.Add($"❌ Ошибка загрузки серверов: {ex.Message}");
            }
        }

        private void CreateTabs(long? forceSelectGroupId = null)
        {
            long? previouslySelectedGroupId = null;
            if (ServersTabControl.SelectedItem is TabItem prevTab && prevTab.Tag is long prevId)
                previouslySelectedGroupId = prevId;

            ServersTabControl.Items.Clear();

            // Put Favorites first, then the rest by user order.
            var orderedGroups = _groups
                .OrderBy(g => g.Id == 3 ? long.MinValue : g.UserOrder)
                .ThenBy(g => g.Id)
                .ToList();

            // Custom groups (id > 3) are shown as "Folder 1/2/3..." regardless of stored name.
            var folderNumberByGroupId = new Dictionary<long, int>();
            {
                var n = 0;
                foreach (var g in orderedGroups.Where(g => g.Id > 3))
                    folderNumberByGroupId[g.Id] = ++n;
            }

            foreach (var group in orderedGroups)
            {
                var tabItem = new TabItem
                {
                    Tag = group.Id,
                    ToolTip = group.Name
                };

                var headerGrid = new Grid
                {
                    Width = 30,
                    Height = 30
                };

                var icon = new PackIcon
                {
                    Width = 20,
                    Height = 20,
                    VerticalAlignment = VerticalAlignment.Center
                };

                switch (group.Id)
                {
                    case 1:
                        icon.Kind = PackIconKind.Earth;
                        icon.Foreground = new SolidColorBrush(MediaColor.FromRgb(33, 150, 243));
                        break;
                    case 2:
                        icon.Kind = PackIconKind.ShieldLock;
                        icon.Foreground = new SolidColorBrush(MediaColor.FromRgb(244, 67, 54));
                        break;
                    case 3:
                        icon.Kind = PackIconKind.Star;
                        icon.Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 193, 7));
                        break;
                    default:
                        icon.Kind = PackIconKind.FolderOutline;
                        icon.Foreground = new SolidColorBrush(MediaColor.FromRgb(156, 39, 176));
                        break;
                }
                headerGrid.Children.Add(icon);

                // Badge: show count of servers in the group.
                int serverCount = 0;
                if (_serversCache.TryGetValue(group.Id, out var serversForCount) && serversForCount != null)
                    serverCount = serversForCount.Count;

                var showBadge = group.Id > 3 || serverCount > 0;
                if (showBadge)
                {
                    var badge = new Border
                    {
                        Background = new SolidColorBrush(MediaColor.FromRgb(0x11, 0x11, 0x11)),
                        BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x55, 0x55, 0x55)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(9),
                        Padding = new Thickness(4, 1, 4, 1),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        VerticalAlignment = System.Windows.VerticalAlignment.Top,
                        Margin = new Thickness(0, -2, -2, 0)
                    };

                    var badgeText = group.Id > 3 && folderNumberByGroupId.TryGetValue(group.Id, out var folderN)
                        ? folderN.ToString()
                        : (serverCount > 99 ? "99+" : serverCount.ToString());

                    badge.Child = new TextBlock
                    {
                        Text = badgeText,
                        Foreground = MediaBrushes.White,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold
                    };

                    headerGrid.Children.Add(badge);
                }

                // Group actions (right click on tab icon).
                var groupForMenu = group;
                tabItem.MouseRightButtonUp += (_, e) =>
                {
                    try
                    {
                        e.Handled = true;
                        ShowGroupActionsDialog(group);
                    }
                    catch { }
                };

                tabItem.Header = headerGrid;

                // Content wrapper: keeps the visual gap between tabs and the first server row.
                var contentBorder = new Border
                {
                    Background = MediaBrushes.Transparent,
                    Padding = new Thickness(12, 12, 12, 18),
                    Tag = group.Id
                };

                // Lazy-load heavy server lists only when the tab is selected.
                contentBorder.Child = new TextBlock
                {
                    Text = "⏳ Загрузка…",
                    Foreground = MediaBrushes.Gray,
                    Margin = new Thickness(5),
                    FontSize = 12,
                    TextAlignment = TextAlignment.Center
                };

                tabItem.Content = contentBorder;
                ServersTabControl.Items.Add(tabItem);
            }

            var addTab = new TabItem
            {
                Tag = -1L,
                ToolTip = "Добавить группу"
            };

            addTab.Header = new PackIcon
            {
                Kind = PackIconKind.PlusCircleOutline,
                Width = 20,
                Height = 20,
                Foreground = MediaBrushes.White,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };

            var addPanel = new StackPanel { Margin = new Thickness(10) };
            var addButton = new Button
            {
                Content = "Добавить новую группу",
                Background = new SolidColorBrush(MediaColor.FromRgb(255, 152, 0)),
                Foreground = MediaBrushes.White,
                FontSize = 14,
                Height = 50,
                Margin = new Thickness(10),
                Cursor = Cursors.Hand
            };
            addButton.Click += CreateGroupButton_Click;
            addPanel.Children.Add(addButton);
            addTab.Content = addPanel;
            ServersTabControl.Items.Add(addTab);

            // Default selection: Favorites; preserve previous selection when rebuilding.
            long desired = forceSelectGroupId ?? previouslySelectedGroupId ?? (_lastActiveGroupId > 0 ? _lastActiveGroupId : 3L);
            if (desired == -1L) desired = 3L;

            foreach (TabItem tab in ServersTabControl.Items)
            {
                if (tab.Tag is long id && id == desired)
                {
                    ServersTabControl.SelectedItem = tab;
                    break;
                }
            }

            if (ServersTabControl.SelectedIndex < 0 && ServersTabControl.Items.Count > 0)
                ServersTabControl.SelectedIndex = 0;

            // Ensure selected tab content is created (and scroll restored) after layout.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (ServersTabControl.SelectedItem is TabItem tab)
                    {
                        EnsureTabContentLoaded(tab);
                        if (tab.Content is Border b && b.Child is ListBox list)
                            RestoreListScroll(list);
                    }
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private List<ProxyEntity> GetSortedServersForGroup(long groupId)
        {
            if (_sortedServersCache.TryGetValue(groupId, out var cached))
                return cached;

            if (!_serversCache.TryGetValue(groupId, out var servers) || servers == null || servers.Count == 0)
            {
                var empty = new List<ProxyEntity>();
                _sortedServersCache[groupId] = empty;
                return empty;
            }

            var hasSpeed = servers.Any(s => s.SpeedKbps > 0);

            // Sorting rules:
            // - If any speed test exists in this group, prefer speed (desc) always (even after ping refresh).
            // - Otherwise sort by ping (asc).
            var sorted = hasSpeed
                ? servers
                    .OrderBy(s => s.SpeedKbps > 0 ? 0 : 1) // servers without speed go last
                    .ThenByDescending(s => s.SpeedKbps)
                    .ThenBy(s => s.Ping <= 0 ? int.MaxValue : s.Ping)
                    .ToList()
                : servers
                    .OrderBy(s => s.Ping <= 0 ? int.MaxValue : s.Ping)
                    .ToList();

            _sortedServersCache[groupId] = sorted;
            return sorted;
        }

        private void EnsureTabContentLoaded(TabItem tab)
        {
            try
            {
                if (tab?.Tag is not long groupId || groupId <= 0)
                    return;

                if (tab.Content is not Border contentBorder)
                    return;

                if (contentBorder.Child is ListBox)
                    return;

                var sortedServers = GetSortedServersForGroup(groupId);
                if (sortedServers.Count == 0)
                {
                    contentBorder.Child = new TextBlock
                    {
                        Text = "Нет серверов. Нажмите 🔄",
                        Foreground = MediaBrushes.Gray,
                        Margin = new Thickness(5),
                        FontSize = 12,
                        TextAlignment = TextAlignment.Center
                    };
                    return;
                }

                var list = new ListBox
                {
                    Style = (Style)FindResource("ServerListBoxStyle"),
                    ItemContainerStyle = (Style)FindResource("ServerListBoxItemStyle"),
                    ItemTemplate = (DataTemplate)FindResource("ServerItemTemplate"),
                    ItemsSource = sortedServers,
                    Tag = groupId
                };

                list.Loaded += (_, _) =>
                {
                    try { AttachListScrollPersistence(list); } catch { }
                    try { RestoreListScroll(list); } catch { }
                };

                contentBorder.Child = list;
            }
            catch { }
        }

        private void ServersTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (ServersTabControl.SelectedItem is TabItem tab)
                {
                    if (tab.Tag is long groupId && groupId > 0)
                        _lastActiveGroupId = groupId;

                    EnsureTabContentLoaded(tab);
                    if (tab.Content is Border b && b.Child is ListBox list)
                        RestoreListScroll(list);
                }
                ShowTutorialOverlayForCurrentStep();
            }
            catch { }
        }

        private void AttachListScrollPersistence(ListBox list)
        {
            try
            {
                var sv = FindVisualChild<ScrollViewer>(list);
                if (sv == null)
                    return;

                // Avoid attaching multiple times on template reapply.
                sv.ScrollChanged -= ServerList_ScrollChanged;
                sv.ScrollChanged += ServerList_ScrollChanged;
            }
            catch { }
        }

        private void ServerList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            try
            {
                if (_suppressListScrollSave)
                    return;

                if (sender is not ScrollViewer sv)
                    return;

                if (sv.TemplatedParent is not ListBox list || list.Tag is not long groupId)
                    return;

                _savedListOffsets[groupId] = sv.VerticalOffset;
            }
            catch { }
        }

        private void RestoreListScroll(ListBox list)
        {
            var oldSuppress = _suppressListScrollSave;
            _suppressListScrollSave = true;
            try
            {
                if (list.Tag is not long groupId)
                {
                    ScrollListToTop(list);
                    return;
                }

                if (!_savedListOffsets.TryGetValue(groupId, out var offset))
                {
                    ScrollListToTop(list);
                    return;
                }

                list.UpdateLayout();
                var sv = FindVisualChild<ScrollViewer>(list);
                if (sv == null)
                {
                    ScrollListToTop(list);
                    return;
                }

                // Clamp in case item count changed.
                var max = Math.Max(0, sv.ExtentHeight - sv.ViewportHeight);
                var clamped = Math.Max(0, Math.Min(offset, max));
                sv.ScrollToVerticalOffset(clamped);
            }
            catch
            {
                try { ScrollListToTop(list); } catch { }
            }
            finally
            {
                _suppressListScrollSave = oldSuppress;
            }
        }

        private static void ScrollListToTop(ListBox list)
        {
            try
            {
                if (list.Items.Count <= 0)
                    return;

                list.UpdateLayout();
                list.ScrollIntoView(list.Items[0]);

                // Also hard-reset the internal ScrollViewer if it exists.
                var sv = FindVisualChild<ScrollViewer>(list);
                sv?.ScrollToTop();
            }
            catch { }
        }

        private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
        {
            try
            {
                var count = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    if (child is T typed)
                        return typed;

                    var result = FindVisualChild<T>(child);
                    if (result != null)
                        return result;
                }
            }
            catch { }

            return null;
        }

        private void HookTabHeaderWheel()
        {
            if (_tabHeaderWheelHooked)
                return;

            void TryAttach()
            {
                try
                {
                    ServersTabControl.ApplyTemplate();
                    var headerScroll = FindVisualChildByName<ScrollViewer>(ServersTabControl, "HeaderScrollViewer");
                    if (headerScroll == null)
                        return;

                    headerScroll.PreviewMouseWheel += (_, e) =>
                    {
                        try
                        {
                            // Wheel up/down over the tab strip scrolls left/right.
                            var next = headerScroll.HorizontalOffset - e.Delta;
                            headerScroll.ScrollToHorizontalOffset(next);
                            e.Handled = true;
                        }
                        catch { }
                    };

                    _tabHeaderWheelHooked = true;
                }
                catch { }
            }

            // The control template may not be applied yet at this moment.
            ServersTabControl.Loaded += (_, _) =>
            {
                TryAttach();
                try
                {
                    Dispatcher.BeginInvoke(new Action(TryAttach), System.Windows.Threading.DispatcherPriority.Background);
                }
                catch { }
            };

            TryAttach();
        }

        private static T? FindVisualChildByName<T>(DependencyObject root, string name) where T : FrameworkElement
        {
            try
            {
                var count = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);

                    if (child is T fe && string.Equals(fe.Name, name, StringComparison.Ordinal))
                        return fe;

                    if (child != null)
                    {
                        var result = FindVisualChildByName<T>(child, name);
                        if (result != null)
                            return result;
                    }
                }
            }
            catch { }

            return null;
        }

        private void ShowGroupActionsDialog(ProxyGroup group)
        {
            if (group == null || group.Id <= 0 || group.Id == -1)
                return;

            var dialog = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = MediaBrushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true
            };

            var overlay = new Grid { Background = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0)) };
            overlay.MouseDown += (_, __) => dialog.Close();

            var card = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x1F, 0x1F, 0x1F)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14)
            };
            card.MouseDown += (_, e) => e.Handled = true;

            var root = new StackPanel { Width = 320 };

            {
                var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                header.Children.Add(new TextBlock
                {
                    Text = group.Name,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = MediaBrushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                });

                var closeBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Background = MediaBrushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(0),
                    Content = new PackIcon
                    {
                        Kind = PackIconKind.Close,
                        Width = 18,
                        Height = 18,
                        Foreground = MediaBrushes.White,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                    }
                };

                closeBtn.Click += (_, _) => dialog.Close();
                closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B));
                closeBtn.MouseLeave += (_, _) => closeBtn.Background = MediaBrushes.Transparent;

                Grid.SetColumn(closeBtn, 1);
                header.Children.Add(closeBtn);

                root.Children.Add(header);
            }

            var btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B))));
            btnStyle.Setters.Add(new Setter(Button.ForegroundProperty, MediaBrushes.White));
            btnStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(12, 10, 12, 10)));
            btnStyle.Setters.Add(new Setter(Button.MarginProperty, new Thickness(0, 0, 0, 10)));
            btnStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            btnStyle.Setters.Add(new Setter(Button.BorderBrushProperty, new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A))));
            btnStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            btnStyle.Setters.Add(new Setter(Button.TemplateProperty, CreateRoundedButtonTemplate(12)));

            Button MakeAction(string title, PackIconKind icon, Func<Task> onClick)
            {
                var btn = new Button { Style = btnStyle };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new PackIcon
                {
                    Kind = icon,
                    Width = 18,
                    Height = 18,
                    Foreground = MediaBrushes.White,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                sp.Children.Add(new TextBlock
                {
                    Text = title,
                    Foreground = MediaBrushes.White,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });
                btn.Content = sp;

                btn.Click += async (_, _) =>
                {
                    dialog.Close();
                    try { await onClick(); }
                    catch (Exception ex) { LogManager.Add($"⚠️ Ошибка: {ex.Message}"); }
                    try
                    {
                        await LoadGroups();
                        await LoadAllServers();
                        CreateTabs();
                    }
                    catch { }
                };

                return btn;
            }

            if (group.Id > 3)
            {
                root.Children.Add(MakeAction("Редактировать", PackIconKind.PencilOutline, async () =>
                {
                    await Task.Yield();
                    ShowEditGroupDialog(group);
                }));
            }

            if (!string.IsNullOrWhiteSpace(group.SubscriptionUrl))
            {
                root.Children.Add(MakeAction("Поделиться URL", PackIconKind.ShareVariantOutline, async () =>
                {
                    System.Windows.Clipboard.SetText(group.SubscriptionUrl!.Trim());
                    StatusText.Text = "🔗 URL подписки скопирован";
                    await Task.CompletedTask;
                }));

                root.Children.Add(MakeAction("Обновить сервера", PackIconKind.Refresh, async () =>
                {
                    await UpdateSingleGroupFromSubscriptionAsync(group);
                }));
            }

            // 🔥 ДОБАВИТЬ ЭТО
            if (group.Id == 1 || group.Id == 2)
            {
                root.Children.Add(MakeAction("Обновить сервера", PackIconKind.Refresh, async () =>
                {
                    var updater = new ServerUpdater();

                    var urls = group.Id == 1
                        ? new List<string> { "http://138.124.0.190:8081/best_normal.txt" }
                        : new List<string> { "http://138.124.0.190:8081/best_blocked.txt" };

                    StatusText.Text = $"🔄 Обновляю: {group.Name}...";

                    await updater.UpdateGroup(group.Id, urls, group.Name);

                    StatusText.Text = $"✅ Обновлено: {group.Name}";

                    await LoadAllServers();
                    CreateTabs();

                    await RunPingTestOnGroupAsync(group.Id);
                }));
            }

            root.Children.Add(MakeAction("Обновить Ping", PackIconKind.AccessPointNetwork, async () =>
            {
                await RunPingTestOnGroupAsync(group.Id);
            }));

            root.Children.Add(MakeAction("Обновить скорость", PackIconKind.Speedometer, async () =>
            {
                await RunSpeedTestOnGroupAsync(group.Id);
            }));

            // 🔥 ДОБАВИТЬ ЭТО (ТОЛЬКО ДЛЯ КАСТОМНЫХ ПОДПИСОК)
            if (group.Id > 3 && !string.IsNullOrWhiteSpace(group.SubscriptionUrl))
            {
                root.Children.Add(MakeAction("Удалить плохие сервера", PackIconKind.Broom, async () =>
                {
                    try
                    {
                        var servers = Database.Instance.GetServersByGroup(group.Id);

                        // ❗ Проверяем: был ли speedtest
                        bool hasRealTest = servers.Any(s => s.SpeedTestStatus > 0);

                        if (!hasRealTest)
                        {
                            StatusText.Text = "⚠ Сначала сделайте проверку скорости";

                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(3000);
                                Dispatcher.Invoke(() => StatusText.Text = "");
                            });

                            return;
                        }

                        int removed = 0;

                        foreach (var s in servers)
                        {
                            // ❗ Удаляем ТОЛЬКО если есть ошибка
                            bool isError =
                                s.SpeedTestStatus > 1 ||           // статус ошибки
                                !string.IsNullOrWhiteSpace(s.SpeedTestError); // текст ошибки

                            if (isError)
                            {
                                Database.Instance.DeleteServer(s.Id);
                                removed++;
                            }
                        }

                        StatusText.Text = $"🧹 Удалено плохих серверов: {removed}";
                        LogManager.Add($"🧹 Удалено плохих серверов: {removed}");

                        await LoadAllServers();
                        CreateTabs();
                    }
                    catch (Exception ex)
                    {
                        LogManager.Add($"❌ Ошибка очистки серверов: {ex.Message}");
                    }
                }));
            }

            if (group.Id > 3)
            {
                root.Children.Add(MakeAction("Удалить группу", PackIconKind.DeleteOutline, async () =>
                {
                    Database.Instance.DeleteGroup(group.Id);
                    PurgeGroupFromMemory(group.Id);
                    StatusText.Text = $"🗑 Удалена группа: {group.Name}";
                    await Task.CompletedTask;
                }));
            }

            card.Child = root;
            overlay.Children.Add(card);
            dialog.Content = overlay;
            dialog.ShowDialog();
        }

        private void ShowEditGroupDialog(ProxyGroup group)
        {
            if (group == null || group.Id <= 3)
                return;

            var dialog = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = MediaBrushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true
            };

            var overlay = new Grid { Background = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0)) };
            overlay.MouseDown += (_, __) => dialog.Close();

            var card = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x1F, 0x1F, 0x1F)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(18),
                Width = 460,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            card.MouseDown += (_, e2) => e2.Handled = true;

            var root = new StackPanel();

            var title = new TextBlock
            {
                Text = "Редактировать группу",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = MediaBrushes.White,
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(title);

            var labelStyle = new Style(typeof(TextBlock));
            labelStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, MediaBrushes.Gray));
            labelStyle.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(0, 0, 0, 6)));
            labelStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));

            var tbStyle = new Style(typeof(TextBox));
            tbStyle.Setters.Add(new Setter(TextBox.BackgroundProperty, new SolidColorBrush(MediaColor.FromRgb(0x2A, 0x2A, 0x2A))));
            tbStyle.Setters.Add(new Setter(TextBox.ForegroundProperty, MediaBrushes.White));
            tbStyle.Setters.Add(new Setter(TextBox.BorderBrushProperty, new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A))));
            tbStyle.Setters.Add(new Setter(TextBox.BorderThicknessProperty, new Thickness(1)));
            tbStyle.Setters.Add(new Setter(TextBox.PaddingProperty, new Thickness(10)));
            tbStyle.Setters.Add(new Setter(TextBox.MarginProperty, new Thickness(0, 0, 0, 12)));

            root.Children.Add(new TextBlock { Text = "URL подписки (опционально)", Style = labelStyle });
            var urlBox = new TextBox { Style = tbStyle, Text = group.SubscriptionUrl ?? "" };
            root.Children.Add(urlBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var okBtn = new Button
            {
                Content = "Сохранить",
                Width = 140,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(MediaColor.FromRgb(76, 175, 80)),
                Foreground = MediaBrushes.White,
                Cursor = Cursors.Hand,
                Template = CreateRoundedButtonTemplate(14)
            };

            var cancelBtn = new Button
            {
                Content = "Отмена",
                Width = 140,
                Background = new SolidColorBrush(MediaColor.FromRgb(80, 80, 80)),
                Foreground = MediaBrushes.White,
                Cursor = Cursors.Hand,
                Template = CreateRoundedButtonTemplate(14)
            };

            okBtn.Click += async (_, _) =>
            {
                var newUrl = string.IsNullOrWhiteSpace(urlBox.Text) ? null : urlBox.Text.Trim();

                Database.Instance.UpdateGroup(group.Id, group.Name ?? "", newUrl);
                StatusText.Text = $"✅ Группа обновлена: {group.Name}";
                dialog.Close();

                await LoadGroups();
                await LoadAllServers();
                CreateTabs(forceSelectGroupId: group.Id);
            };

            cancelBtn.Click += (_, _) => dialog.Close();

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            card.Child = root;
            overlay.Children.Add(card);
            dialog.Content = overlay;
            dialog.ShowDialog();
        }

        private async Task UpdateSingleGroupFromSubscriptionAsync(ProxyGroup group)
        {
            if (group == null || group.Id <= 0)
                return;
            if (string.IsNullOrWhiteSpace(group.SubscriptionUrl))
            {
                StatusText.Text = "⚠️ У группы нет URL подписки";
                return;
            }

            try
            {
                StatusText.Text = $"🔄 Обновляю: {group.Name}...";
                var updater = new ServerUpdater();
                await updater.UpdateGroup(group.Id, new List<string> { group.SubscriptionUrl.Trim() }, group.Name);
                StatusText.Text = $"✅ Обновлено: {group.Name}";

                await LoadAllServers();
                CreateTabs();

                // Optional: refresh ping after subscription update.
                await RunPingTestOnGroupAsync(group.Id);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"❌ Ошибка обновления: {ex.Message}";
                LogManager.Add($"❌ Ошибка обновления подписки ({group.Name}): {ex.Message}");
            }
        }

        private async Task RunPingTestOnGroupAsync(long groupId)
        {
            try
            {
                if (groupId <= 0)
                    return;

                if (!_serversCache.TryGetValue(groupId, out var servers) || servers == null || servers.Count == 0)
                    return;

                foreach (var s in servers)
                    Database.Instance.UpdateServerPing(s.Id, -2);

                await LoadAllServers();
                CreateTabs();

                StatusText.Text = $"📡 Ping: {servers.Count} серверов...";

                var results = await FastUrlTester.TestProfilesParallel(servers, 10, 3000);
                var ok = 0;
                foreach (var r in results)
                {
                    if (r.Server == null)
                        continue;

                    var ping = r.Ping;
                    Database.Instance.UpdateServerPing(r.Server.Id, ping > 0 ? ping : -1);
                    if (ping > 0) ok++;
                }

                StatusText.Text = $"📡 Ping: {ok}/{results.Count}";

                await LoadAllServers();
                CreateTabs();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"❌ Ошибка ping-теста: {ex.Message}";
                LogManager.Add($"❌ Ошибка ping-теста группы: {ex.Message}");
            }
        }

        private async Task RunSpeedTestOnGroupAsync(long groupId)
        {
            try
            {
                if (!await EnsureXrayReadyAsync("group speedtest"))
                    return;

                if (groupId <= 0)
                    return;

                if (!_serversCache.TryGetValue(groupId, out var servers) || servers == null || servers.Count == 0)
                    return;

                StatusText.Text = $"⚡ Тест скорости: {servers.Count} серверов...";

                var maxConc = Math.Min(
                    servers.Count,
                    Math.Clamp(Environment.ProcessorCount * 6, 16, 48));

                var results = await ServerSpeedTester.TestServersAsync(
                    servers,
                    maxConcurrency: maxConc);

                var ok = 0;
                foreach (var r in results)
                {
                    if (r.Server == null)
                        continue;

                    Database.Instance.UpdateServerSpeed(r.Server.Id, r.SpeedKbps, r.BytesDownloaded, (int)r.Status, r.Error);
                    if (r.SpeedKbps > 0)
                        ok++;
                }

                var fail = results.Count - ok;
                StatusText.Text = fail > 0
                    ? $"⚡ Скорость обновлена: {ok}/{results.Count} (ошибка/таймаут: {fail})"
                    : $"⚡ Скорость обновлена: {ok}/{results.Count}";

                await LoadAllServers();
                CreateTabs();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"❌ Ошибка speed-теста: {ex.Message}";
                LogManager.Add($"❌ Ошибка speed-теста группы: {ex.Message}");
            }
        }

        private async void ServerItem_LeftClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (sender is not ListBoxItem item)
                    return;

                if (item.DataContext is ProxyEntity server)
                {
                    item.IsSelected = true;
                    e.Handled = true;
                    await OnServerSelected(server);
                    if (_onboardingActive && _onboardingStep == OnboardingStep.NeedSelectServer)
                        AdvanceOnboarding(OnboardingStep.NeedConnectVpn);
                }
            }
            catch { }
        }

        private void ServerItem_RightClick(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (sender is not ListBoxItem item)
                    return;

                item.IsSelected = true;

                if (item.DataContext is ProxyEntity server)
                {
                    if (_onboardingActive)
                    {
                        e.Handled = true;
                        return;
                    }
                    e.Handled = true;
                    ShowServerActionsDialog(server);
                }
            }
            catch { }
        }

        private void ShowServerActionsDialog(ProxyEntity server)
        {
            var dialog = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = MediaBrushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true
            };

            var overlay = new Grid
            {
                Background = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0))
            };
            overlay.MouseDown += (_, __) => dialog.Close();

            var card = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x1F, 0x1F, 0x1F)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14)
            };
            card.MouseDown += (_, e) => e.Handled = true;

            var root = new StackPanel { Width = 300 };

            {
                var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                header.Children.Add(new TextBlock
                {
                    Text = server.DisplayName(),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = MediaBrushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                });

                var closeBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Background = MediaBrushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(0),
                    Content = new PackIcon
                    {
                        Kind = PackIconKind.Close,
                        Width = 18,
                        Height = 18,
                        Foreground = MediaBrushes.White,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                    }
                };

                closeBtn.Click += (_, _) => dialog.Close();
                closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B));
                closeBtn.MouseLeave += (_, _) => closeBtn.Background = MediaBrushes.Transparent;

                Grid.SetColumn(closeBtn, 1);
                header.Children.Add(closeBtn);

                root.Children.Add(header);
            }

            var btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B))));
            btnStyle.Setters.Add(new Setter(Button.ForegroundProperty, MediaBrushes.White));
            btnStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(12, 10, 12, 10)));
            btnStyle.Setters.Add(new Setter(Button.MarginProperty, new Thickness(0, 0, 0, 10)));
            btnStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            btnStyle.Setters.Add(new Setter(Button.BorderBrushProperty, new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A))));
            btnStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            btnStyle.Setters.Add(new Setter(Button.TemplateProperty, CreateRoundedButtonTemplate(12)));

            Button MakeAction(string title, PackIconKind icon, Func<Task> onClick)
            {
                var btn = new Button { Style = btnStyle };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new PackIcon
                {
                    Kind = icon,
                    Width = 18,
                    Height = 18,
                    Foreground = MediaBrushes.White,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                sp.Children.Add(new TextBlock
                {
                    Text = title,
                    Foreground = MediaBrushes.White,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });
                btn.Content = sp;

                btn.Click += async (_, _) =>
                {
                    dialog.Close();
                    try { await onClick(); }
                    catch (Exception ex) { LogManager.Add($"⚠️ Ошибка: {ex.Message}"); }
                    try
                    {
                        await LoadAllServers();
                        CreateTabs();
                    }
                    catch { }
                };

                return btn;
            }

            root.Children.Add(MakeAction("Подключиться", PackIconKind.Power, async () =>
            {
                await ConnectToServerAsync(server);
            }));

            var isFavorite = server.GroupId == 3;
            root.Children.Add(MakeAction(isFavorite ? "Убрать из избранного" : "В избранное",
                isFavorite ? PackIconKind.StarOffOutline : PackIconKind.StarOutline,
                async () =>
                {
                    if (isFavorite)
                    {
                        Database.Instance.DeleteServer(server.Id);
                        StatusText.Text = $"☆ {server.DisplayName()} удалён из избранного";
                        await Task.CompletedTask;
                        return;
                    }

                    if (!Database.Instance.ServerExistsInGroup(3, server.BeanJson))
                    {
                        Database.Instance.AddServer(new ProxyEntity
                        {
                            GroupId = 3,
                            Type = server.Type,
                            UserOrder = 0,
                            Tx = 0,
                            Rx = 0,
                            SpeedKbps = 0,
                            SpeedTestBytes = 0,
                            SpeedTestStatus = 0,
                            SpeedTestError = "",
                            Status = 0,
                            Ping = 0,
                            Error = "",
                            BeanJson = server.BeanJson,
                            Name = server.Name
                        });
                    }

                    StatusText.Text = $"⭐ {server.DisplayName()} добавлен в избранное";
                    await Task.CompletedTask;
                }));

            root.Children.Add(MakeAction("Поделиться", PackIconKind.ShareVariantOutline, async () =>
            {
                await Task.Yield();
                ShowShareDialog(server);
            }));

            root.Children.Add(MakeAction("Обновить Ping", PackIconKind.AccessPointNetwork, async () =>
            {
                await RunPingTestOnServerAsync(server);
            }));

            root.Children.Add(MakeAction("Обновить скорость", PackIconKind.Speedometer, async () =>
            {
                await RunSpeedTestOnServerAsync(server);
            }));
 
            card.Child = root;
            overlay.Children.Add(card);
            dialog.Content = overlay;
            dialog.ShowDialog();
        }

        private async Task ShowNewsDialogAsync()
        {
            var dialog = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = MediaBrushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true
            };

            var overlay = new Grid { Background = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0)) };
            overlay.MouseDown += (_, __) => dialog.Close();

            var card = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x24, 0x24, 0x24)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16),
                Width = 560,
                MaxHeight = 620,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            card.MouseDown += (_, e) => e.Handled = true;

            var root = new StackPanel();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.Children.Add(new TextBlock
            {
                Text = "Новости",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = MediaBrushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });

            var closeBtn = new Button
            {
                Width = 28,
                Height = 28,
                Background = MediaBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0),
                Content = new PackIcon
                {
                    Kind = PackIconKind.Close,
                    Width = 18,
                    Height = 18,
                    Foreground = MediaBrushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                }
            };

            closeBtn.Click += (_, _) => dialog.Close();
            closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B));
            closeBtn.MouseLeave += (_, _) => closeBtn.Background = MediaBrushes.Transparent;

            Grid.SetColumn(closeBtn, 1);
            header.Children.Add(closeBtn);
            root.Children.Add(header);

            var status = new TextBlock
            {
                Text = "⏳ Загружаю новости...",
                Foreground = MediaBrushes.Gray,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(status);

            var newsPanel = new StackPanel();

            var scroll = new ScrollViewer
            {
                Height = 430,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = newsPanel
            };

            root.Children.Add(scroll);

            card.Child = root;
            overlay.Children.Add(card);
            dialog.Content = overlay;

            _ = Task.Run(async () =>
            {
                try
                {
                    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    var newsRoot = await NewsService.GetNewsAsync(cts.Token);

                    Dispatcher.Invoke(() =>
                    {
                        newsPanel.Children.Clear();

                        if (newsRoot.News == null || newsRoot.News.Count == 0)
                        {
                            status.Text = "✅ Новостей пока нет.";
                            return;
                        }

                        status.Text = newsRoot.LastUpdate.HasValue
                            ? $"✅ Обновлено: {newsRoot.LastUpdate.Value:dd.MM.yyyy HH:mm}"
                            : "✅ Новости загружены.";

                        foreach (var item in newsRoot.News.OrderByDescending(x => x.Date))
                        {
                            var itemCard = new Border
                            {
                                Background = new SolidColorBrush(MediaColor.FromRgb(0x1A, 0x1A, 0x1A)),
                                BorderBrush = item.Important
                                    ? new SolidColorBrush(MediaColor.FromRgb(0x4C, 0xAF, 0x50))
                                    : new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(14),
                                Padding = new Thickness(14),
                                Margin = new Thickness(0, 0, 0, 12)
                            };

                            var itemRoot = new StackPanel();

                            itemRoot.Children.Add(new TextBlock
                            {
                                Text = string.IsNullOrWhiteSpace(item.Title) ? "Без заголовка" : item.Title,
                                Foreground = MediaBrushes.White,
                                FontSize = 14,
                                FontWeight = FontWeights.SemiBold,
                                TextWrapping = TextWrapping.Wrap
                            });

                            if (!string.IsNullOrWhiteSpace(item.Date))
                            {
                                itemRoot.Children.Add(new TextBlock
                                {
                                    Text = item.Date,
                                    Foreground = MediaBrushes.Gray,
                                    FontSize = 11,
                                    Margin = new Thickness(0, 4, 0, 8)
                                });
                            }

                            itemRoot.Children.Add(new TextBlock
                            {
                                Text = item.Content ?? "",
                                Foreground = new SolidColorBrush(MediaColor.FromRgb(0xDD, 0xDD, 0xDD)),
                                FontSize = 13,
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 0, 0, 10)
                            });

                            if (!string.IsNullOrWhiteSpace(item.Link))
                            {
                                var linkBtn = new Button
                                {
                                    Content = "Открыть ссылку",
                                    Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B)),
                                    Foreground = MediaBrushes.White,
                                    BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                                    BorderThickness = new Thickness(1),
                                    Padding = new Thickness(10, 7, 10, 7),
                                    Cursor = Cursors.Hand,
                                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                                    Template = CreateRoundedButtonTemplate(10)
                                };

                                var url = item.Link;
                                linkBtn.Click += (_, _) =>
                                {
                                    try
                                    {
                                        Process.Start(new ProcessStartInfo
                                        {
                                            FileName = url,
                                            UseShellExecute = true
                                        });
                                    }
                                    catch (Exception ex)
                                    {
                                        StatusText.Text = $"❌ Не удалось открыть ссылку: {ex.Message}";
                                    }
                                };

                                itemRoot.Children.Add(linkBtn);
                            }

                            itemCard.Child = itemRoot;
                            newsPanel.Children.Add(itemCard);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        status.Text = $"❌ Не удалось загрузить новости: {ex.Message}";
                        newsPanel.Children.Clear();
                    });
                }
            });

            dialog.ShowDialog();
        }

        private void OpenFeedbackEmail()
        {
            var dialog = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = MediaBrushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true
            };

            var overlay = new Grid { Background = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0)) };
            overlay.MouseDown += (_, __) => dialog.Close();

            var card = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x24, 0x24, 0x24)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16),
                Width = 420,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };

            card.MouseDown += (_, e) => e.Handled = true;

            var root = new StackPanel();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Обратная связь",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = MediaBrushes.White,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };

            Grid.SetColumn(title, 0);
            header.Children.Add(title);

            var closeBtn = new Button
            {
                Width = 28,
                Height = 28,
                Background = MediaBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0),
                Content = new PackIcon
                {
                    Kind = PackIconKind.Close,
                    Width = 18,
                    Height = 18,
                    Foreground = MediaBrushes.White,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                }
            };

            closeBtn.Click += (_, _) => dialog.Close();
            closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B));
            closeBtn.MouseLeave += (_, _) => closeBtn.Background = MediaBrushes.Transparent;

            Grid.SetColumn(closeBtn, 1);
            header.Children.Add(closeBtn);

            root.Children.Add(header);

            root.Children.Add(new TextBlock
            {
                Text = "Напишите нам на почту. Скопируйте адрес ниже и вставьте в любую удобную почту: Gmail, Outlook, Proton Mail и т.д.",
                Foreground = MediaBrushes.White,
                Opacity = 0.82,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var email = "kluchshifrovaniya@proton.me";

            var emailBox = new TextBox
            {
                Text = email,
                IsReadOnly = true,
                Background = new SolidColorBrush(MediaColor.FromRgb(0x1A, 0x1A, 0x1A)),
                Foreground = MediaBrushes.White,
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };

            emailBox.PreviewMouseDown += (_, e) =>
            {
                e.Handled = true;
                emailBox.Focus();
                emailBox.SelectAll();
                System.Windows.Clipboard.SetText(email);
                StatusText.Text = "📋 Почта скопирована";
            };

            root.Children.Add(emailBox);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Vertical
            };

            var copyBtn = new Button
            {
                Content = "📋 Скопировать почту",
                Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B)),
                Foreground = MediaBrushes.White,
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 8),
                Cursor = Cursors.Hand,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                FontWeight = FontWeights.SemiBold,
                Template = CreateRoundedButtonTemplate(10)
            };

            copyBtn.Click += (_, _) =>
            {
                System.Windows.Clipboard.SetText(email);
                StatusText.Text = "📋 Почта скопирована";
            };

            buttons.Children.Add(copyBtn);

            var copyLogBtn = new Button
            {
                Content = "🧾 Скопировать технический лог",
                Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B)),
                Foreground = MediaBrushes.White,
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 8),
                Cursor = Cursors.Hand,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 8, 0, 0),
                FontWeight = FontWeights.SemiBold,
                Template = CreateRoundedButtonTemplate(10)
            };

            copyLogBtn.Click += (_, _) =>
            {
                try
                {
                    var log = LogManager.GetLog();

                    if (string.IsNullOrWhiteSpace(log))
                    {
                        StatusText.Text = "⚠️ Лог пуст";
                        return;
                    }

                    System.Windows.Clipboard.SetText(log);
                    StatusText.Text = "📋 Лог скопирован";
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"❌ Ошибка копирования лога: {ex.Message}";
                }
            };

            buttons.Children.Add(copyLogBtn);
            root.Children.Add(buttons);

            root.Children.Add(new TextBlock
            {
                Text = "1. Откройте любую почту\n2. Вставьте адрес\n3. Опишите проблему или предложение\n4. При необходимости приложите технический лог",
                Foreground = MediaBrushes.Gray,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            });

            card.Child = root;
            overlay.Children.Add(card);
            dialog.Content = overlay;

            dialog.ShowDialog();
        }

        private async Task RunPingTestOnServerAsync(ProxyEntity server)
        {
            try
            {
                if (server == null)
                    return;

                Database.Instance.UpdateServerPing(server.Id, -2);
                await LoadAllServers();
                CreateTabs();

                StatusText.Text = $"📡 Ping: {server.DisplayName()}...";

                var results = await FastUrlTester.TestProfilesParallel(new List<ProxyEntity> { server }, 1, 3000);
                var ping = results.FirstOrDefault()?.Ping ?? -1;

                Database.Instance.UpdateServerPing(server.Id, ping > 0 ? ping : -1);
                StatusText.Text = ping > 0
                    ? $"📡 Ping: {server.DisplayName()} = {ping}ms"
                    : $"📡 Ping: {server.DisplayName()} = ошибка";

                await LoadAllServers();
                CreateTabs();
            }
            catch (Exception ex)
            {
                LogManager.Add($"❌ Ошибка ping-теста: {ex.Message}");
            }
        }

        private async Task RunSpeedTestOnServerAsync(ProxyEntity server)
        {
            try
            {
                if (server == null)
                    return;

                // ❗ ВАЖНО — добавить
                if (!await EnsureXrayReadyAsync("single speedtest"))
                    return;

                StatusText.Text = $"⚡ Speedtest: {server.DisplayName()}...";

                var results = await ServerSpeedTester.TestServersAsync(new List<ProxyEntity> { server }, maxConcurrency: 1);
                var r = results.FirstOrDefault();
                if (r?.Server == null)
                    return;

                Database.Instance.UpdateServerSpeed(r.Server.Id, r.SpeedKbps, r.BytesDownloaded, (int)r.Status, r.Error);
                StatusText.Text = r.SpeedKbps > 0
                    ? $"⚡ Speedtest: {server.DisplayName()} = {(r.SpeedKbps * 8) / 1024.0:0.0} Мбит/с"
                    : $"⚡ Speedtest: {server.DisplayName()} = {r.Status}";

                await LoadAllServers();
                CreateTabs();
            }
            catch (Exception ex)
            {
                LogManager.Add($"❌ Ошибка speed-теста: {ex.Message}");
            }
        }

        private void ShowShareDialog(ProxyEntity server)
        {
            var dialog = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = MediaBrushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true
            };

            var overlay = new Grid
            {
                Background = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0))
            };
            overlay.MouseDown += (_, __) => dialog.Close();

            var card = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x1F, 0x1F, 0x1F)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14)
            };
            card.MouseDown += (_, e) => e.Handled = true;

            var root = new StackPanel { Width = 300 };

            {
                var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                header.Children.Add(new TextBlock
                {
                    Text = "Поделиться",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = MediaBrushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                });

                var closeBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Background = MediaBrushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(0),
                    Content = new PackIcon
                    {
                        Kind = PackIconKind.Close,
                        Width = 18,
                        Height = 18,
                        Foreground = MediaBrushes.White,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                    }
                };

                closeBtn.Click += (_, _) => dialog.Close();
                closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B));
                closeBtn.MouseLeave += (_, _) => closeBtn.Background = MediaBrushes.Transparent;

                Grid.SetColumn(closeBtn, 1);
                header.Children.Add(closeBtn);

                root.Children.Add(header);
            }

            root.Children.Add(new TextBlock
            {
                Text = server.DisplayName(),
                FontSize = 12,
                Foreground = MediaBrushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B))));
            btnStyle.Setters.Add(new Setter(Button.ForegroundProperty, MediaBrushes.White));
            btnStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(12, 10, 12, 10)));
            btnStyle.Setters.Add(new Setter(Button.MarginProperty, new Thickness(0, 0, 0, 10)));
            btnStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            btnStyle.Setters.Add(new Setter(Button.BorderBrushProperty, new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A))));
            btnStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            btnStyle.Setters.Add(new Setter(Button.TemplateProperty, CreateRoundedButtonTemplate(12)));

            Button MakeCopy(string title, PackIconKind icon, Func<bool> copy)
            {
                var btn = new Button { Style = btnStyle };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new PackIcon
                {
                    Kind = icon,
                    Width = 18,
                    Height = 18,
                    Foreground = MediaBrushes.White,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                sp.Children.Add(new TextBlock
                {
                    Text = title,
                    Foreground = MediaBrushes.White,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });
                btn.Content = sp;

                btn.Click += (_, _) =>
                {
                    try
                    {
                        if (copy())
                            dialog.Close();
                    }
                    catch (Exception ex)
                    {
                        LogManager.Add($"⚠️ Не удалось скопировать: {ex.Message}");
                    }
                };

                return btn;
            }

            root.Children.Add(MakeCopy("Ссылка", PackIconKind.LinkVariant, () =>
            {
                if (TryGetVlessShareLink(server, out var link))
                {
                    System.Windows.Clipboard.SetText(link);
                    StatusText.Text = "📋 Ссылка скопирована";
                    return true;
                }

                StatusText.Text = "⚠️ Нет ссылки";
                return false;
            }));

            root.Children.Add(MakeCopy("JSON", PackIconKind.CodeBraces, () =>
            {
                var pretty = GetPrettyJson(server.BeanJson);
                System.Windows.Clipboard.SetText(pretty);
                StatusText.Text = "📋 JSON скопирован";
                return true;
            }));

            card.Child = root;
            overlay.Children.Add(card);
            dialog.Content = overlay;
            dialog.ShowDialog();
        }

        private static bool TryGetVlessShareLink(ProxyEntity server, out string link)
        {
            link = "";

            if (string.IsNullOrWhiteSpace(server.BeanJson))
                return false;

            try
            {
                var obj = JObject.Parse(server.BeanJson);

                var raw = obj["raw_url"]?.Value<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(raw) && raw.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                {
                    link = raw;
                    return true;
                }

                var type = obj["type"]?.Value<string>()?.Trim().ToLowerInvariant() ?? "";
                if (type == "vless")
                    return TryBuildVlessLink(obj, server, out link);

                return false;
            }
            catch
            {
                return false;
            }
        }

        private MenuItem BuildMenuItem(PackIconKind kind, string title, RoutedEventHandler onClick)
        {
            var item = new MenuItem
            {
                Style = (Style)FindResource("RoundedMenuItemStyle")
            };

            item.Header = BuildMenuHeader(kind, title);
            item.Click += onClick;
            return item;
        }

        private static object BuildMenuHeader(PackIconKind kind, string title)
        {
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            header.Children.Add(new PackIcon
            {
                Kind = kind,
                Width = 18,
                Height = 18,
                Foreground = MediaBrushes.White,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });

            header.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = MediaBrushes.White,
                FontSize = 13,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });

            return header;
        }

        private static ControlTemplate CreateRoundedButtonTemplate(double cornerRadius)
        {
            var template = new ControlTemplate(typeof(Button));

            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Bd";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Left);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Button.PaddingProperty));
            border.AppendChild(presenter);

            template.VisualTree = border;

            var hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(MediaColor.FromRgb(0x33, 0x33, 0x33)), "Bd"));
            template.Triggers.Add(hover);

            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)), "Bd"));
            template.Triggers.Add(pressed);

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.OpacityProperty, 0.6, "Bd"));
            template.Triggers.Add(disabled);

            return template;
        }

        private static string GetPrettyJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return "";
            try
            {
                var j = JObject.Parse(json);
                return j.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch
            {
                return json!;
            }
        }

        private static bool TryBuildShareLink(ProxyEntity server, out string link)
        {
            link = "";
            if (string.IsNullOrWhiteSpace(server.BeanJson))
                return false;

            try
            {
                var obj = JObject.Parse(server.BeanJson);
                var type = obj["type"]?.Value<string>()?.Trim().ToLowerInvariant() ?? "";

                return type switch
                {
                    "vless" => TryBuildVlessLink(obj, server, out link),
                    "vmess" => TryBuildVmessLink(obj, server, out link),
                    "trojan" => TryBuildTrojanLink(obj, server, out link),
                    "shadowsocks" => TryBuildShadowsocksLink(obj, server, out link),
                    _ => false
                };
            }
            catch
            {
                return false;
            }
        }

        private static bool TryBuildVlessLink(JObject obj, ProxyEntity server, out string link)
        {
            link = "";
            var host = obj["server"]?.Value<string>() ?? "";
            var port = obj["port"]?.Value<int?>() ?? 0;
            var uuid = obj["uuid"]?.Value<string>() ?? "";
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || string.IsNullOrWhiteSpace(uuid))
                return false;

            var q = new List<string>();

            var encryption = obj["encryption"]?.Value<string>() ?? "none";
            q.Add("encryption=" + Uri.EscapeDataString(encryption));

            var flow = obj["flow"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(flow))
                q.Add("flow=" + Uri.EscapeDataString(flow));

            var network = obj["network"]?.Value<string>() ?? obj["type"]?.Value<string>() ?? "tcp";
            if (!string.IsNullOrWhiteSpace(network))
                q.Add("type=" + Uri.EscapeDataString(network));

            var security = obj["security"]?.Value<string>() ?? "";
            var hasReality = !string.IsNullOrWhiteSpace(obj["reality_pub_key"]?.Value<string>());
            if (hasReality)
            {
                q.Add("security=reality");
                var pbk = obj["reality_pub_key"]?.Value<string>() ?? "";
                var sid = obj["reality_short_id"]?.Value<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(pbk)) q.Add("pbk=" + Uri.EscapeDataString(pbk));
                if (!string.IsNullOrWhiteSpace(sid)) q.Add("sid=" + Uri.EscapeDataString(sid));
            }
            else if (!string.IsNullOrWhiteSpace(security) && security.Equals("tls", StringComparison.OrdinalIgnoreCase))
            {
                q.Add("security=tls");
            }

            var sni = obj["sni"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(sni))
                q.Add("sni=" + Uri.EscapeDataString(sni));

            var fp = obj["fingerprint"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(fp))
                q.Add("fp=" + Uri.EscapeDataString(fp));

            var alpn = obj["alpn"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(alpn))
                q.Add("alpn=" + Uri.EscapeDataString(alpn));

            if (network.Equals("ws", StringComparison.OrdinalIgnoreCase))
            {
                var path = obj["path"]?.Value<string>() ?? "/";
                q.Add("path=" + Uri.EscapeDataString(path));
                var wsHost = obj["host"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(wsHost))
                    q.Add("host=" + Uri.EscapeDataString(wsHost));
            }

            var name = Uri.EscapeDataString(server.DisplayName());
            link = $"vless://{Uri.EscapeDataString(uuid)}@{host}:{port}?{string.Join("&", q)}#{name}";
            return true;
        }

        private static bool TryBuildVmessLink(JObject obj, ProxyEntity server, out string link)
        {
            link = "";
            var host = obj["server"]?.Value<string>() ?? "";
            var port = obj["port"]?.Value<int?>() ?? 0;
            var uuid = obj["uuid"]?.Value<string>() ?? "";
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || string.IsNullOrWhiteSpace(uuid))
                return false;

            var vm = new JObject
            {
                ["v"] = "2",
                ["ps"] = server.DisplayName(),
                ["add"] = host,
                ["port"] = port.ToString(),
                ["id"] = uuid,
                ["aid"] = (obj["alterId"]?.Value<int?>() ?? 0).ToString(),
                ["scy"] = obj["security"]?.Value<string>() ?? "auto",
                ["net"] = obj["network"]?.Value<string>() ?? "tcp",
                ["type"] = "none",
                ["host"] = obj["host"]?.Value<string>() ?? "",
                ["path"] = obj["path"]?.Value<string>() ?? "",
                ["tls"] = (obj["security"]?.Value<string>()?.Equals("tls", StringComparison.OrdinalIgnoreCase) ?? false) ? "tls" : ""
            };

            var sni = obj["sni"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(sni))
                vm["sni"] = sni;

            var json = vm.ToString(Newtonsoft.Json.Formatting.None);
            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            link = "vmess://" + b64;
            return true;
        }

        private static bool TryBuildTrojanLink(JObject obj, ProxyEntity server, out string link)
        {
            link = "";
            var host = obj["server"]?.Value<string>() ?? "";
            var port = obj["port"]?.Value<int?>() ?? 0;
            var pwd = obj["password"]?.Value<string>() ?? "";
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || string.IsNullOrWhiteSpace(pwd))
                return false;

            var q = new List<string>();
            var security = obj["security"]?.Value<string>() ?? "tls";
            if (!string.IsNullOrWhiteSpace(security))
                q.Add("security=" + Uri.EscapeDataString(security));
            var sni = obj["sni"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(sni))
                q.Add("sni=" + Uri.EscapeDataString(sni));
            var fp = obj["fingerprint"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(fp))
                q.Add("fp=" + Uri.EscapeDataString(fp));
            var alpn = obj["alpn"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(alpn))
                q.Add("alpn=" + Uri.EscapeDataString(alpn));

            var name = Uri.EscapeDataString(server.DisplayName());
            link = $"trojan://{Uri.EscapeDataString(pwd)}@{host}:{port}?{string.Join("&", q)}#{name}";
            return true;
        }

        private static bool TryBuildShadowsocksLink(JObject obj, ProxyEntity server, out string link)
        {
            link = "";
            var host = obj["server"]?.Value<string>() ?? "";
            var port = obj["port"]?.Value<int?>() ?? 0;
            var method = obj["method"]?.Value<string>() ?? "";
            var pwd = obj["password"]?.Value<string>() ?? "";
            if (string.IsNullOrWhiteSpace(host) || port <= 0 || string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(pwd))
                return false;

            var name = Uri.EscapeDataString(server.DisplayName());
            link = $"ss://{Uri.EscapeDataString(method)}:{Uri.EscapeDataString(pwd)}@{host}:{port}#{name}";
            return true;
        }

        private static string GetServerAddress(ProxyEntity server)
        {
            try
            {
                if (!string.IsNullOrEmpty(server.BeanJson))
                {
                    dynamic? obj = Newtonsoft.Json.JsonConvert.DeserializeObject(server.BeanJson);
                    if (obj != null)
                    {
                        string? serverAddr = obj.server;
                        int? port = obj.port;
                        return $"{serverAddr}:{port}";
                    }
                }
            }
            catch
            {
                // Игнорируем ошибки парсинга
            }
            return "адрес не указан";
        }

        private async Task OnServerSelected(ProxyEntity server)
        {
            _selectedServer = server;
            StatusText.Text = $"Выбран: {server.DisplayName()}";
            LogManager.Add($"🖱 Выбран сервер: {server.DisplayName()}");
        }

        private async Task ConnectToServerAsync(ProxyEntity server)
        {
            if (_xrayService == null)
                return;

            if (_isVpnTransition)
                return;

            VpnButton.IsEnabled = false;
            _isVpnTransition = true;

            try
            {
                if (!await EnsureXrayReadyAsync("manual connect"))
                    return;

                await OnServerSelected(server);

                StatusText.Text = $"🔌 Подключение к {server.DisplayName()}...";
                SetVpnUiState(VpnUiState.Connecting);
                await Task.Yield();

                var success = await _xrayService.Start(server);
                if (success)
                {
                    StatusText.Text = $"✅ Подключено: {server.DisplayName()}";
                    SetVpnUiState(VpnUiState.On);
                }
                else
                {
                    StatusText.Text = "❌ Ошибка подключения";
                    SetVpnUiState(VpnUiState.Off);
                }
            }
            finally
            {
                _isVpnTransition = false;
                if (_onboardingActive)
                    ApplyOnboardingUiState();
                else
                    VpnButton.IsEnabled = true;
            }
        }

        // Selection highlighting is handled by ListBoxItem template.

        private async void AddServerButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "➕ Добавить сервер",
                Width = 520,
                Height = 380,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = MediaBrushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true
            };

            var overlay = new Grid { Background = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0)) };
            overlay.MouseDown += (_, __) => dialog.Close();

            var card = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x1F, 0x1F, 0x1F)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(18),
                Width = 460,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            card.MouseDown += (_, e2) => e2.Handled = true;

            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = "Вставьте ссылку на сервер:",
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = MediaBrushes.White
            });

            var urlBox = new TextBox
            {
                Margin = new Thickness(0, 0, 0, 12),
                Height = 120,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(MediaColor.FromRgb(0x2A, 0x2A, 0x2A)),
                Foreground = MediaBrushes.White,
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10)
            };
            panel.Children.Add(urlBox);

            var infoText = new TextBlock
            {
                Text = "Поддерживаемые форматы:\n• vless://\n• vmess://\n• trojan://\n• ss://",
                FontSize = 11,
                Foreground = MediaBrushes.Gray,
                Margin = new Thickness(0, 0, 0, 14),
                TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(infoText);

            var btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B))));
            btnStyle.Setters.Add(new Setter(Button.ForegroundProperty, MediaBrushes.White));
            btnStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(12, 10, 12, 10)));
            btnStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            btnStyle.Setters.Add(new Setter(Button.BorderBrushProperty, new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A))));
            btnStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            btnStyle.Setters.Add(new Setter(Button.TemplateProperty, CreateRoundedButtonTemplate(12)));

            var btnPanel = new Grid
            {
                Margin = new Thickness(0, 6, 0, 0)
            };
            btnPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            btnPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var okBtn = new Button
            {
                Style = btnStyle,
                Content = "Сохранить",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };

            var cancelBtn = new Button
            {
                Style = btnStyle,
                Content = "Отмена",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            Grid.SetColumn(okBtn, 0);
            Grid.SetColumn(cancelBtn, 2);

            okBtn.Click += async (_, _) =>
            {
                var link = urlBox.Text.Trim();
                if (string.IsNullOrEmpty(link))
                {
                    StatusText.Text = "❌ Введите ссылку на сервер";
                    return;
                }

                var server = LinkParser.Parse(link);
                if (server == null)
                {
                    StatusText.Text = "❌ Не удалось распознать ссылку";
                    LogManager.Add($"❌ Ошибка парсинга: {link}");
                    return;
                }

                server.GroupId = 3;
                server.Status = 0;
                server.Ping = 0;

                Database.Instance.AddServer(server);

                LogManager.Add($"✅ Добавлен сервер в избранное: {server.DisplayName()}");
                StatusText.Text = $"✅ Добавлен: {server.DisplayName()}";

                dialog.Close();

                await LoadAllServers();
                CreateTabs(forceSelectGroupId: 3L);
            };

            cancelBtn.Click += (_, _) => dialog.Close();

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            card.Child = panel;
            overlay.Children.Add(card);
            dialog.Content = overlay;
            dialog.ShowDialog();
        }

        private async void CreateGroupButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = MediaBrushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true
            };

            var overlay = new Grid { Background = new SolidColorBrush(MediaColor.FromArgb(1, 0, 0, 0)) };
            overlay.MouseDown += (_, __) => dialog.Close();

            var card = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x24, 0x24, 0x24)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16),
                Width = 420,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            card.MouseDown += (_, e2) => e2.Handled = true;

            var root = new StackPanel();

            {
                var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                header.Children.Add(new TextBlock
                {
                    Text = "Новая группа / подписка",
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = MediaBrushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                });

                var closeBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Background = MediaBrushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(0),
                    Content = new PackIcon
                    {
                        Kind = PackIconKind.Close,
                        Width = 18,
                        Height = 18,
                        Foreground = MediaBrushes.White,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                    }
                };
                closeBtn.Click += (_, _) => dialog.Close();
                closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B));
                closeBtn.MouseLeave += (_, _) => closeBtn.Background = MediaBrushes.Transparent;

                Grid.SetColumn(closeBtn, 1);
                header.Children.Add(closeBtn);
                root.Children.Add(header);
            }

            var labelStyle = new Style(typeof(TextBlock));
            labelStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, MediaBrushes.Gray));
            labelStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0));
            labelStyle.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(0, 0, 0, 6)));

            var tbStyle = new Style(typeof(TextBox));
            tbStyle.Setters.Add(new Setter(TextBox.BackgroundProperty, new SolidColorBrush(MediaColor.FromRgb(0x1A, 0x1A, 0x1A))));
            tbStyle.Setters.Add(new Setter(TextBox.ForegroundProperty, MediaBrushes.White));
            tbStyle.Setters.Add(new Setter(TextBox.BorderBrushProperty, new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A))));
            tbStyle.Setters.Add(new Setter(TextBox.BorderThicknessProperty, new Thickness(1)));
            tbStyle.Setters.Add(new Setter(TextBox.PaddingProperty, new Thickness(10)));
            tbStyle.Setters.Add(new Setter(TextBox.MarginProperty, new Thickness(0, 0, 0, 12)));

            var nameHint = new TextBlock
            {
                Text = "Имя будет создано автоматически: Папка 1 / Папка 2 / ...",
                Style = labelStyle
            };
            root.Children.Add(nameHint);

            root.Children.Add(new TextBlock { Text = "URL подписки (опционально)", Style = labelStyle });
            var urlBox = new TextBox { Style = tbStyle };
            root.Children.Add(urlBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var okBtn = new Button
            {
                Content = "✅ Создать",
                Width = 140,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(MediaColor.FromRgb(76, 175, 80)),
                Foreground = MediaBrushes.White,
                Cursor = Cursors.Hand,
                Template = CreateRoundedButtonTemplate(14)
            };

            var cancelBtn = new Button
            {
                Content = "❌ Отмена",
                Width = 140,
                Background = new SolidColorBrush(MediaColor.FromRgb(244, 67, 54)),
                Foreground = MediaBrushes.White,
                Cursor = Cursors.Hand,
                Template = CreateRoundedButtonTemplate(14)
            };

            okBtn.Click += async (_, _) =>
            {
                int nextN = 1;
                try
                {
                    var max = 0;
                    foreach (var g in _groups.Where(g => g.Id > 3))
                    {
                        var t = (g.Name ?? "").Trim();
                        if (t.StartsWith("Папка ", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(t.Substring("Папка ".Length).Trim(), out var n) &&
                            n > max)
                        {
                            max = n;
                        }
                    }
                    nextN = Math.Max(1, max + 1);
                }
                catch { }

                var groupName = $"Папка {nextN}";
                var subUrl = string.IsNullOrWhiteSpace(urlBox.Text) ? null : urlBox.Text.Trim();
                var newGroupId = Database.Instance.AddGroup(groupName, subUrl);
                LogManager.Add($"➕ Создана новая группа: {groupName}");
                StatusText.Text = $"✅ Создана группа: {groupName}";

                dialog.Close();

                await LoadGroups();
                await LoadAllServers();
                CreateTabs(forceSelectGroupId: newGroupId > 0 ? newGroupId : null);
            };

            cancelBtn.Click += (_, _) => dialog.Close();

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            card.Child = root;
            overlay.Children.Add(card);
            dialog.Content = overlay;
            dialog.ShowDialog();
        }

        private enum UpdateAction
        {
            Ping,
            Speed,
            Servers
        }

        private async void UpdateServersButton_Click(object sender, RoutedEventArgs e)
        {
            if (_onboardingActive)
            {
                if (_onboardingStep == OnboardingStep.NeedUpdateServers)
                {
                    var proceed = ShowSingleUpdateOptionDialog(
                        UpdateAction.Servers,
                        "Шаг 1/4",
                        "Выберите «Обновить сервера»");
                    if (!proceed)
                        return;

                    UpdateServersButton.IsEnabled = false;
                    try
                    {
                        StatusText.Text = "🔄 Обновляю сервера...";
                        await UpdateServersFromSourcesAsync();
                        AdvanceOnboarding(OnboardingStep.NeedUpdateSpeed);
                    }
                    finally
                    {
                        ApplyOnboardingUiState();
                    }
                    return;
                }

                if (_onboardingStep == OnboardingStep.NeedUpdateSpeed)
                {
                    var proceed = ShowSingleUpdateOptionDialog(
                        UpdateAction.Speed,
                        "Шаг 2/4",
                        "Выберите «Обновить скорость»");
                    if (!proceed)
                        return;

                    UpdateServersButton.IsEnabled = false;
                    try
                    {
                        var firstGroupWithServers = _groups.FirstOrDefault(g => _serversCache.TryGetValue(g.Id, out var list) && list != null && list.Count > 0);
                        if (firstGroupWithServers != null)
                        {
                            foreach (var item in ServersTabControl.Items)
                            {
                                if (item is TabItem t && t.Tag is long gid && gid == firstGroupWithServers.Id)
                                {
                                    ServersTabControl.SelectedItem = t;
                                    break;
                                }
                            }
                        }
                        StatusText.Text = "⚡ Обновляю скорость серверов...";
                        await RunSpeedTestOnActiveTab();
                        AdvanceOnboarding(OnboardingStep.NeedSelectServer);
                    }
                    finally
                    {
                        ApplyOnboardingUiState();
                    }
                    return;
                }

                ShowSmallInfoDialog("Обучение", "Сейчас доступен только следующий шаг обучения.");
                return;
            }

            var action = ShowUpdateOptionsDialog();
            if (action == null)
                return;

            UpdateServersButton.IsEnabled = false;
            try
            {
                switch (action.Value)
                {
                    case UpdateAction.Ping:
                        StatusText.Text = "📡 Обновляю ping...";
                        await RunPingTestOnActiveTab();
                        break;

                    case UpdateAction.Speed:
                        StatusText.Text = "⚡ Обновляю скорость серверов...";
                        await RunSpeedTestOnActiveTab();
                        break;

                    case UpdateAction.Servers:
                        StatusText.Text = "🔄 Обновляю сервера...";
                        await UpdateServersFromSourcesAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка: {ex.Message}";
                LogManager.Add($"❌ Ошибка обновления: {ex.Message}");
            }
            finally
            {
                UpdateServersButton.IsEnabled = true;
            }
        }

        private UpdateAction? ShowUpdateOptionsDialog()
        {
            var dialog = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = MediaBrushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true
            };

            var card = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x24, 0x24, 0x24)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16)
            };

            UpdateAction? result = null;

            var root = new StackPanel { Width = 320 };

            {
                var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                header.Children.Add(new TextBlock
                {
                    Text = "Обновление",
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = MediaBrushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                });

                var closeBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Background = MediaBrushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(0),
                    Content = new PackIcon
                    {
                        Kind = PackIconKind.Close,
                        Width = 18,
                        Height = 18,
                        Foreground = MediaBrushes.White,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                    }
                };

                closeBtn.Click += (_, _) => dialog.Close();
                closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B));
                closeBtn.MouseLeave += (_, _) => closeBtn.Background = MediaBrushes.Transparent;

                Grid.SetColumn(closeBtn, 1);
                header.Children.Add(closeBtn);

                root.Children.Add(header);
            }

            root.Children.Add(new TextBlock
            {
                Text = "Выберите, что обновить:",
                FontSize = 12,
                Foreground = MediaBrushes.Gray,
                Margin = new Thickness(0, 0, 0, 14)
            });

            var btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B))));
            btnStyle.Setters.Add(new Setter(Button.ForegroundProperty, MediaBrushes.White));
            btnStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(12, 10, 12, 10)));
            btnStyle.Setters.Add(new Setter(Button.MarginProperty, new Thickness(0, 0, 0, 10)));
            btnStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            btnStyle.Setters.Add(new Setter(Button.BorderBrushProperty, new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A))));
            btnStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            btnStyle.Setters.Add(new Setter(Button.TemplateProperty, CreateRoundedButtonTemplate(12)));

            Button MakeOption(string title, PackIconKind icon, UpdateAction action)
            {
                var btn = new Button
                {
                    Style = btnStyle
                };

                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new PackIcon
                {
                    Kind = icon,
                    Width = 18,
                    Height = 18,
                    Foreground = MediaBrushes.White,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                sp.Children.Add(new TextBlock
                {
                    Text = title,
                    Foreground = MediaBrushes.White,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });

                btn.Content = sp;
                btn.Click += (_, _) =>
                {
                    result = action;
                    dialog.Close();
                };

                return btn;
            }

            root.Children.Add(MakeOption("Обновить сервера", PackIconKind.Refresh, UpdateAction.Servers));
            root.Children.Add(MakeOption("Обновить скорость", PackIconKind.Speedometer, UpdateAction.Speed));
            root.Children.Add(MakeOption("Обновить Ping", PackIconKind.AccessPointNetwork, UpdateAction.Ping));

            var cancelBtn = new Button
            {
                Content = "Отмена",
                Style = btnStyle,
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(12, 8, 12, 8)
            };
            cancelBtn.Click += (_, _) => dialog.Close();
            root.Children.Add(cancelBtn);

            card.Child = root;
            dialog.Content = card;
            dialog.ShowDialog();

            return result;
        }

        private bool ShowSingleUpdateOptionDialog(UpdateAction forcedAction, string title, string subtitle)
        {
            var dialog = new Window
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = MediaBrushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                ShowInTaskbar = false,
                Topmost = true
            };

            var card = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x24, 0x24, 0x24)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16)
            };

            bool result = false;
            var root = new StackPanel { Width = 320 };
            root.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = MediaBrushes.White });
            root.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = MediaBrushes.Gray, Margin = new Thickness(0, 4, 0, 14) });

            string text = forcedAction switch
            {
                UpdateAction.Servers => "Обновить сервера",
                UpdateAction.Speed => "Обновить скорость",
                _ => "Обновить Ping"
            };
            var icon = forcedAction switch
            {
                UpdateAction.Servers => PackIconKind.Refresh,
                UpdateAction.Speed => PackIconKind.Speedometer,
                _ => PackIconKind.AccessPointNetwork
            };

            var btn = new Button
            {
                Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B)),
                Foreground = MediaBrushes.White,
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                Cursor = Cursors.Hand,
                Template = CreateRoundedButtonTemplate(12)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new PackIcon { Kind = icon, Width = 18, Height = 18, Foreground = MediaBrushes.White, Margin = new Thickness(0, 0, 10, 0) });
            sp.Children.Add(new TextBlock { Text = text, Foreground = MediaBrushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            btn.Content = sp;
            btn.Click += (_, _) => { result = true; dialog.Close(); };
            root.Children.Add(btn);

            var cancelBtn = new Button
            {
                Content = "Отмена",
                Background = new SolidColorBrush(MediaColor.FromRgb(0x2B, 0x2B, 0x2B)),
                Foreground = MediaBrushes.White,
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x3A, 0x3A)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 8, 0, 0),
                Cursor = Cursors.Hand,
                Template = CreateRoundedButtonTemplate(12)
            };
            cancelBtn.Click += (_, _) => dialog.Close();
            root.Children.Add(cancelBtn);

            card.Child = root;
            dialog.Content = card;
            dialog.ShowDialog();
            return result;
        }

        private async Task UpdateServersFromSourcesAsync()
        {
            LogManager.Add("🔄 Обновление серверов...");

            var updater = new ServerUpdater();
            var normalUrls = new List<string> { "http://138.124.0.190:8081/best_normal.txt" };
            var blockedUrls = new List<string> { "http://138.124.0.190:8081/best_blocked.txt" };

            await updater.UpdateGroup(1, normalUrls, "Обычные сервера");
            await updater.UpdateGroup(2, blockedUrls, "Специальные сервера");

            try
            {
                await LoadGroups();

                foreach (var g in _groups.Where(g => g.Id > 3 && !string.IsNullOrWhiteSpace(g.SubscriptionUrl)))
                {
                    await updater.UpdateGroup(g.Id, new List<string> { g.SubscriptionUrl!.Trim() }, g.Name);
                }
            }
            catch (Exception ex)
            {
                LogManager.Add($"⚠️ Ошибка обновления подписок: {ex.Message}");
            }

            StatusText.Text = "✅ Сервера обновлены";
            LogManager.Add("✅ Сервера обновлены");

            await LoadAllServers();
            CreateTabs();

            await RunPingTestOnServers();
        }

        private async Task RunSpeedTestOnActiveTab()
        {
            try
            {
                if (!await EnsureXrayReadyAsync("tab speedtest"))
                    return;

                if (ServersTabControl.SelectedItem is not TabItem tab || tab.Tag is not long groupId || groupId <= 0)
                {
                    return;
                }

                if (!_serversCache.TryGetValue(groupId, out var serversForGroup) || serversForGroup == null || serversForGroup.Count == 0)
                {
                    return;
                }

                StatusText.Text = $"⚡ Тест скорости: {serversForGroup.Count} серверов...";

                // One short-lived xray process for the whole tab.
                var maxConc = Math.Min(
                    serversForGroup.Count,
                    Math.Clamp(Environment.ProcessorCount * 6, 16, 48));

                var results = await ServerSpeedTester.TestServersAsync(
                    serversForGroup,
                    maxConcurrency: maxConc);

                var ok = 0;
                foreach (var r in results)
                {
                    if (r.Server == null)
                        continue;

                    Database.Instance.UpdateServerSpeed(r.Server.Id, r.SpeedKbps, r.BytesDownloaded, (int)r.Status, r.Error);
                    if (r.SpeedKbps > 0)
                        ok++;
                }
                var fail = results.Count - ok;
                StatusText.Text = fail > 0
                    ? $"⚡ Скорость обновлена: {ok}/{results.Count} (ошибка/таймаут: {fail})"
                    : $"⚡ Скорость обновлена: {ok}/{results.Count}";

                await LoadAllServers();
                CreateTabs();
            }
            catch (Exception)
            {
                StatusText.Text = "❌ Ошибка speed-теста";
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                WindowState = WindowState == WindowState.Normal ? WindowState.Maximized : WindowState.Normal;
            else
                DragMove();
        }

        private async Task<bool> EnsureGeoDataReadyAsync()
        {
            try
            {
                GeoDataUpdater.EnsureGeoDataFilesExistFromBundledAssets();

                // Always call updater: it is fast when nothing to do, but will also force-refresh
                // automatically when source URLs change (prevents "tag not found in geosite.dat").
                StatusText.Text = "⏳ Проверяю правила маршрутизации (geosite/geoip)...";
                LogManager.Add("⏳ Проверяю правила маршрутизации (geosite/geoip)...");

                var (ok, msg) = await GeoDataUpdater.UpdateGeoDataAsync(force: false);
                LogManager.Add(msg);

                if (!ok && !GeoDataUpdater.HasValidGeoData())
                {
                    StatusText.Text = "❌ Не удалось подготовить правила (geosite/geoip)";
                    return false;
                }

                StatusText.Text = "✅ Правила маршрутизации готовы";
                return true;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"❌ Ошибка geodata: {ex.Message}";
                return false;
            }
        }

        private async void VpnButton_Click(object sender, RoutedEventArgs e)
        {
            if (_onboardingActive && _onboardingStep != OnboardingStep.NeedConnectVpn)
            {
                ShowSmallInfoDialog("Обучение", "Сейчас выполните текущий шаг обучения.");
                return;
            }

            VpnButton.IsEnabled = false;
            _isVpnTransition = true;
            try
            {
            if (_xrayService!.IsRunning)
            {
                LogManager.Add("🔴 Отключение VPN...");
                SetVpnUiState(VpnUiState.Disconnecting);
                await Task.Yield();
                await _xrayService.Stop();
                StatusText.Text = "VPN отключён";
                _selectedServer = null;
                LogManager.Add("✅ VPN отключён");
                _isVpnTransition = false;
                SetVpnUiState(VpnUiState.Off);
            }
            else
            {
                if (_selectedServer == null)
                {
                    StatusText.Text = "⚠️ Выберите сервер";
                    LogManager.Add("⚠️ Нет выбранного сервера");
                    return;
                }

                    // ❗ СНАЧАЛА Xray
                    if (!await EnsureXrayReadyAsync("VPN connect"))
                        return;

                    // ❗ ПОТОМ права администратора (покажет UAC при необходимости)
                    if (!await _xrayService.EnsureElevationForVpnAsync())
                    {
                        StatusText.Text = "❌ Нужны права администратора для запуска VPN";
                        SetVpnUiState(VpnUiState.Off);
                        return;
                    }

                    // ❗ ПОТОМ geodata
                    if (!await EnsureGeoDataReadyAsync())
                        return;

                    LogManager.Add("🟢 Запуск VPN...");
                    SetVpnUiState(VpnUiState.Connecting);
                    await Task.Yield();

                    LogManager.Add($"🔌 Подключение к {_selectedServer.DisplayName()}...");
                    var success = await _xrayService.Start(_selectedServer);

                if (success)
                {
                    StatusText.Text = $"✅ Подключено: {_selectedServer?.DisplayName()}";
                    LogManager.Add("✅ VPN подключён!");
                    _isVpnTransition = false;
                    SetVpnUiState(VpnUiState.On);
                    if (_onboardingActive && _onboardingStep == OnboardingStep.NeedConnectVpn)
                        AdvanceOnboarding(OnboardingStep.Done);
                }
                else
                {
                    StatusText.Text = "❌ Ошибка подключения";
                    LogManager.Add("❌ Ошибка подключения VPN");
                    _isVpnTransition = false;
                    SetVpnUiState(VpnUiState.Off);
                }
            }
            }
            finally
            {
                _isVpnTransition = false;
                VpnButton.IsEnabled = true;
            }
        }

        private void InitTrayIcon()
        {
            System.Drawing.Icon trayIcon;
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                trayIcon = !string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath)
                    ? (System.Drawing.Icon.ExtractAssociatedIcon(exePath) ?? System.Drawing.SystemIcons.Application)
                    : System.Drawing.SystemIcons.Application;
            }
            catch
            {
                trayIcon = System.Drawing.SystemIcons.Application;
            }

            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = trayIcon,
                Visible = true,
                Text = "byeWhiteList"
            };

            _notifyIcon.DoubleClick += (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    Show();
                    WindowState = WindowState.Normal;
                });
            };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();

            var showItem = new System.Windows.Forms.ToolStripMenuItem("Показать");
            showItem.Click += (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    Show();
                    WindowState = WindowState.Normal;
                });
            };
            contextMenu.Items.Add(showItem);

            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Выйти");
            exitItem.Click += (_, _) =>
            {
                _exitOnClose = true;
                _notifyIcon?.Dispose();
                Dispatcher.Invoke(Close);
            };
            contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private void MinimizeToTrayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_notifyIcon == null)
            {
                InitTrayIcon();
            }

            Hide();
            WindowState = WindowState.Minimized;

            _notifyIcon?.ShowBalloonTip(1000, "byeWhiteList",
                "Приложение свернуто в трей. Нажмите на иконку для восстановления.",
                System.Windows.Forms.ToolTipIcon.Info);
        }

        private async void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_xrayService?.IsRunning == true)
                {
                    StatusText.Text = "⏳ Отключаю VPN перед выходом...";
                    try { await _xrayService.Stop(); } catch { }
                }
            }
            catch { }

            _exitOnClose = true;
            Close();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                if (_notifyIcon != null && !_exitOnClose)
                {
                    Hide();
                    _notifyIcon?.ShowBalloonTip(1000, "byeWhiteList",
                        "Приложение свернуто в трей. Нажмите на иконку для восстановления.",
                        System.Windows.Forms.ToolTipIcon.Info);
                }
            }
            base.OnStateChanged(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _notifyIcon?.Dispose();
            base.OnClosed(e);
        }

        // AutoMode/BlockedMode UI and logic removed.

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_shutdownInProgress)
            {
                base.OnClosing(e);
                return;
            }

            _shutdownInProgress = true;
            e.Cancel = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ShutdownAppAsync();
                }
                finally
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _exitOnClose = true;
                        Close();
                    });
                }
            });
        }

        private async Task ShutdownAppAsync()
        {
            AppSettings.Instance.LastActiveTab = ServersTabControl.SelectedIndex;
            AppSettings.Instance.Save();

            try
            {
                if (_xrayService != null)
                {
                    var stopTask = _xrayService.Stop();
                    var completed = await Task.WhenAny(stopTask, Task.Delay(3000));
                    if (completed == stopTask)
                    {
                        try { await stopTask; } catch { }
                    }
                    else
                    {
                        LogManager.Add("⚠️ Таймаут остановки Xray при выходе — принудительное завершение.");
                    }
                }
            }
            catch { }

            try
            {
                XrayService.ForceKillKnownXrayProcesses();
            }
            catch { }

            try
            {
                XrayService.ForceKillAllXrayProcesses();
            }
            catch { }
        }
    }
}
