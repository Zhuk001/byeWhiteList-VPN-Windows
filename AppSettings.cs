using System;
using System.IO;
using System.Text.Json;

namespace ByeWhiteList.Windows
{
    public class AppSettings
    {
        private static AppSettings? _instance;
        private static readonly object _lock = new object();
        private const string SETTINGS_FILE = "settings.json";

        // Настройки
        public int LastActiveTab { get; set; } = 0;
        public bool IsFirstRun { get; set; } = true;
        public string CustomGroupsJson { get; set; } = "";
        public string Language { get; set; } = "ru";

        // Geo routing profile (karing-like): choose a country ruleset once, then reuse on every connect.
        // Example values: "ru", "ua", "kz", "de", "us".
        public string GeoRoutingProfileId { get; set; } = "ru";

        // geosite.dat / geoip.dat cache (stored in LocalAppData\ByeWhiteList\geodata)
        public DateTime GeoDataLastUpdate { get; set; } = DateTime.MinValue;
        // Used to invalidate cache when URLs change.
        public string GeoDataSourceKey { get; set; } = "";

        // Auto-update geodata on app start (in background).
        public bool GeoDataAutoUpdateOnStartup { get; set; } = true;

        // Domains that must go through VPN/proxy even if they match built-in direct rules.
        // One entry per line. Supports: full:, domain:, keyword:, regexp:, or plain domain.
        public string ForceProxyDomains { get; set; } = "";

        // Domains that must always go direct (bypass VPN). One entry per line.
        // Supports: full:, domain:, keyword:, regexp:, or plain domain.
        public string ForceDirectDomains { get; set; } = "";

        // Optional local SOCKS proxy on localhost. Disabled by default to reduce attack surface.
        public bool EnableLocalSocksProxy { get; set; } = false;

        // SpeedTest endpoints (one base URL per line). Must support Cloudflare-style API:
        //   GET  <base>/__down?bytes=N
        //   POST <base>/__up
        // By default uses Cloudflare (anycast). You can add your own mirrors here.
        public string SpeedTestEndpoints { get; set; } =
            "http://138.124.0.190:8080/\n" +
            "https://speed.cloudflare.com\n" +
            "https://librespeed.org\n" +
            "https://speedtest.net/\n" +
            "https://speedtest.homeoperator.net/\n" +
            "https://openspeedtest.ru/\n" +
            "https://openspeedtest.com/\n" +
            "https://www.megabitus.com/\n" +
            "http://rtk.speedtestcustom.com/\n" +
            "https://fiber.google.com/speedtest/";

        public static AppSettings Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = Load();
                    }
                    return _instance;
                }
            }
        }

        private static AppSettings Load()
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ByeWhiteList");

            var settingsPath = Path.Combine(appData, SETTINGS_FILE);

            if (File.Exists(settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(settingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
                catch (Exception)
                {
                    return new AppSettings();
                }
            }

            return new AppSettings();
        }

        public void Save()
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ByeWhiteList");

            if (!Directory.Exists(appData))
                Directory.CreateDirectory(appData);

            var settingsPath = Path.Combine(appData, SETTINGS_FILE);
            var json = JsonSerializer.Serialize(this);
            File.WriteAllText(settingsPath, json);
        }
    }
}
