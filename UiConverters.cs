using ByeWhiteList.Windows.Models;
using Newtonsoft.Json;
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ByeWhiteList.Windows
{
    // Note: project enables both WPF and WinForms, so we fully-qualify WPF types
    // that have System.Drawing/System.Windows.Forms twins (Brushes/Color/Binding).
    public sealed class PingTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not int ping)
                return "—";

            if (ping > 0) return $"{ping}ms";
            if (ping == -2) return "тест…";
            return "—";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            System.Windows.Data.Binding.DoNothing;
    }

    public sealed class PingBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not int ping)
                return System.Windows.Media.Brushes.Gray;

            if (ping == -1) return System.Windows.Media.Brushes.IndianRed;
            if (ping == -2) return System.Windows.Media.Brushes.Gray;
            if (ping <= 0) return System.Windows.Media.Brushes.Gray;

            if (ping < 100) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x5E, 0x20));
            if (ping < 200) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
            if (ping < 300) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFB, 0xC0, 0x2D));
            if (ping < 500) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x70, 0x43));
            return new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x39, 0x35));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            System.Windows.Data.Binding.DoNothing;
    }

    public sealed class SpeedTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not ProxyEntity server)
                return "—";

            return server.SpeedTestStatus switch
            {
                2 => "таймаут",
                3 => "ошибка",
                _ => server.SpeedKbps > 0 ? $"{(server.SpeedKbps * 8) / 1024.0:0.0} Мбит/с" : "—"
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            System.Windows.Data.Binding.DoNothing;
    }

    public sealed class SpeedBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not ProxyEntity server)
                return System.Windows.Media.Brushes.Gray;

            return server.SpeedTestStatus switch
            {
                2 => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFB, 0xC0, 0x2D)),
                3 => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x53, 0x50)),
                _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x90, 0xCA, 0xF9))
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            System.Windows.Data.Binding.DoNothing;
    }

    public sealed class ServerAddressConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not ProxyEntity server || string.IsNullOrWhiteSpace(server.BeanJson))
                return "адрес не указан";

            try
            {
                dynamic? obj = JsonConvert.DeserializeObject(server.BeanJson);
                if (obj == null) return "адрес не указан";

                string? serverAddr = obj.server;
                int? port = obj.port;
                if (!string.IsNullOrWhiteSpace(serverAddr))
                    return $"{serverAddr}:{port}";
            }
            catch
            {
                // ignore
            }

            return "адрес не указан";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            System.Windows.Data.Binding.DoNothing;
    }
}
