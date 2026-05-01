using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ByeWhiteList.Windows.Services;

namespace ByeWhiteList.Windows
{
    public partial class RoutingSettingsWindow : Window
    {
        public RoutingSettingsWindow()
        {
            InitializeComponent();

            GeoProfileComboBox.ItemsSource = GeoRoutingProfiles.Profiles;
            GeoProfileComboBox.SelectedValue = (AppSettings.Instance.GeoRoutingProfileId ?? "").Trim().ToLowerInvariant();
            GeoAutoUpdateCheckBox.IsChecked = AppSettings.Instance.GeoDataAutoUpdateOnStartup;

            // By default (fresh install) исключения должны быть полностью пустыми.
            // On non-first runs we restore saved values.
            if (AppSettings.Instance.IsFirstRun)
            {
                ForceDirectDomainsTextBox.Text = "";
                ForceProxyDomainsTextBox.Text = "";
            }
            else
            {
                ForceDirectDomainsTextBox.Text = AppSettings.Instance.ForceDirectDomains ?? "";
                ForceProxyDomainsTextBox.Text = AppSettings.Instance.ForceProxyDomains ?? "";
            }

            System.Windows.DataObject.AddPastingHandler(ForceDirectDomainsTextBox, OnPasteDomains);
            System.Windows.DataObject.AddPastingHandler(ForceProxyDomainsTextBox, OnPasteDomains);
            ForceDirectDomainsTextBox.PreviewKeyDown += DomainsTextBox_PreviewKeyDown;
            ForceProxyDomainsTextBox.PreviewKeyDown += DomainsTextBox_PreviewKeyDown;

            Loaded += async (_, _) =>
            {
                try
                {
                    UpdateGeoDataStatusText();

                    // Background auto-update (never blocks the window from opening).
                    if (GeoAutoUpdateCheckBox.IsChecked == true)
                    {
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            try { await GeoDataUpdater.UpdateGeoDataAsync(force: false); } catch { }
                        });
                    }
                }
                catch { }

                ForceDirectDomainsTextBox.Focus();
            };
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (e.ClickCount == 1 && e.ButtonState == MouseButtonState.Pressed)
                    DragMove();
            }
            catch { }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Instance.GeoRoutingProfileId = (GeoProfileComboBox.SelectedValue as string ?? "ru").Trim().ToLowerInvariant();
            AppSettings.Instance.GeoDataAutoUpdateOnStartup = GeoAutoUpdateCheckBox.IsChecked == true;
            AppSettings.Instance.ForceDirectDomains = NormalizeDomainsText(ForceDirectDomainsTextBox.Text ?? "");
            AppSettings.Instance.ForceProxyDomains = NormalizeDomainsText(ForceProxyDomainsTextBox.Text ?? "");
            AppSettings.Instance.Save();
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                FindNext();
                e.Handled = true;
            }
        }

        private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchTextBox.Text = "";
            GetActiveTextBox().Focus();
        }

        private async void UpdateGeoDataButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateGeoDataButton.IsEnabled = false;
                GeoDataStatusText.Text = "⏳ Обновляю...";

                var (ok, msg) = await GeoDataUpdater.UpdateGeoDataAsync(force: true);
                GeoDataStatusText.Text = msg;
                if (!ok)
                    System.Media.SystemSounds.Hand.Play();
            }
            catch (Exception ex)
            {
                GeoDataStatusText.Text = $"❌ Ошибка: {ex.Message}";
            }
            finally
            {
                UpdateGeoDataButton.IsEnabled = true;
            }
        }

        private void UpdateGeoDataStatusText()
        {
            try
            {
                GeoDataUpdater.EnsureGeoDataFilesExistFromBundledAssets();
                var ok = GeoDataUpdater.HasValidGeoData();
                var last = AppSettings.Instance.GeoDataLastUpdate;
                if (!ok)
                {
                    GeoDataStatusText.Text = "⚠️ geodata не найдена";
                    return;
                }

                if (last == DateTime.MinValue)
                    GeoDataStatusText.Text = "✅ geodata есть (локально)";
                else
                    GeoDataStatusText.Text = $"✅ geodata есть (обновлялась {last:dd.MM HH:mm})";
            }
            catch
            {
                GeoDataStatusText.Text = "";
            }
        }

        private void FindNext()
        {
            var needle = SearchTextBox.Text ?? "";
            if (string.IsNullOrWhiteSpace(needle))
                return;

            var tb = GetActiveTextBox();
            var text = tb.Text ?? "";
            var start = tb.SelectionStart + tb.SelectionLength;
            var idx = text.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0 && start > 0)
                idx = text.IndexOf(needle, 0, StringComparison.OrdinalIgnoreCase);

            if (idx >= 0)
            {
                tb.Focus();
                tb.Select(idx, needle.Length);
                tb.ScrollToLine(tb.GetLineIndexFromCharacterIndex(idx));
            }
        }

        private System.Windows.Controls.TextBox GetActiveTextBox()
        {
            try
            {
                var selected = ListsTabControl?.SelectedIndex ?? 0;
                return selected == 1 ? ForceProxyDomainsTextBox : ForceDirectDomainsTextBox;
            }
            catch
            {
                return ForceDirectDomainsTextBox;
            }
        }

        private void DomainsTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            // If user typed a TLD shortcut like ".ru", convert it to regexp:.*\.ru$
            try
            {
                var tb = (System.Windows.Controls.TextBox)sender;
                int caret = tb.CaretIndex;
                int lineIndex = tb.GetLineIndexFromCharacterIndex(caret);
                int lineStart = tb.GetCharacterIndexFromLineIndex(lineIndex);
                int lineLen = tb.GetLineLength(lineIndex);
                var line = tb.Text.Substring(lineStart, lineLen).Trim();

                var replacement = TldShortcutToRegexp(line);
                if (replacement != null)
                {
                    tb.Select(lineStart, lineLen);
                    tb.SelectedText = replacement;
                    tb.CaretIndex = lineStart + replacement.Length;
                }
            }
            catch
            {
                // best-effort
            }
        }

        private void OnPasteDomains(object sender, DataObjectPastingEventArgs e)
        {
            try
            {
                if (!e.SourceDataObject.GetDataPresent(System.Windows.DataFormats.UnicodeText, true))
                    return;

                var raw = e.SourceDataObject.GetData(System.Windows.DataFormats.UnicodeText) as string ?? "";
                var normalized = NormalizeDomainsText(raw);
                if (string.IsNullOrWhiteSpace(normalized))
                    return;

                // Ensure we paste as new lines.
                e.CancelCommand();

                var tb = (System.Windows.Controls.TextBox)sender;
                int caret = tb.CaretIndex;
                string prefix = "";
                if (caret > 0 && tb.Text.Length >= caret)
                {
                    char prev = tb.Text[caret - 1];
                    if (prev != '\n' && prev != '\r')
                        prefix = Environment.NewLine;
                }

                tb.SelectedText = prefix + normalized + Environment.NewLine;
            }
            catch
            {
                // best-effort
            }
        }

        private static string NormalizeDomainsText(string raw)
        {
            raw = (raw ?? "").Replace("\r\n", "\n");
            var parts = raw
                .Split(new[] { '\n', ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeToken)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return string.Join(Environment.NewLine, parts);
        }

        private static string? NormalizeToken(string token)
        {
            token = (token ?? "").Trim();
            if (string.IsNullOrWhiteSpace(token))
                return null;
            if (token.StartsWith("#"))
                return null;

            // Keep explicit xray matchers as-is.
            if (token.StartsWith("full:", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("domain:", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("keyword:", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase))
            {
                return token;
            }

            var rx = TldShortcutToRegexp(token);
            if (rx != null)
                return rx;

            // URL -> host
            if (Uri.TryCreate(token, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                token = uri.Host;
            }

            token = token.Trim().Trim('.');
            if (token.StartsWith("*."))
                token = token.Substring(2);
            if (token.StartsWith("."))
                token = token.Substring(1);

            if (token.IndexOfAny(new[] { ' ', '\t', '/', '\\' }) >= 0)
                return null;
            if (!token.Contains('.'))
                return null;

            return token;
        }

        private static string? TldShortcutToRegexp(string token)
        {
            token = (token ?? "").Trim();
            if (token.StartsWith("."))
                token = token.Substring(1);

            if (token.Equals("ru", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("su", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("рф", StringComparison.OrdinalIgnoreCase))
            {
                return $"regexp:.*\\.{token}$";
            }

            return null;
        }
    }
}
