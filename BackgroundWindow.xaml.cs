using Microsoft.Web.WebView2.Wpf;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace ByeWhiteList.Windows
{
    public partial class BackgroundWindow : Window
    {
        public BackgroundWindow()
        {
            InitializeComponent();
            Loaded += BackgroundWindow_Loaded;
        }

        private async void BackgroundWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeWebView();
        }

        private async Task InitializeWebView()
        {
            try
            {
                await webView.EnsureCoreWebView2Async();

                // Получаем HWND WebView2 и принудительно опускаем его
                var hwnd = (HwndSource)PresentationSource.FromVisual(webView);
                if (hwnd != null)
                {
                    SetWindowLong(hwnd.Handle, -8, (int)(GetWindowLong(hwnd.Handle, -8) | 0x80));
                    SetWindowPos(hwnd.Handle, (IntPtr)1, 0, 0, 0, 0, 0x0002 | 0x0001);
                }

                webView.CoreWebView2.Settings.IsScriptEnabled = true;

                string htmlPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "wwwroot", "index.html");

                if (System.IO.File.Exists(htmlPath))
                {
                    string html = System.IO.File.ReadAllText(htmlPath);
                    webView.NavigateToString(html);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        // WinAPI функции (должны быть ВНУТРИ класса, но после using)
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    }
}
