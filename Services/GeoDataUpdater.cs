using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ByeWhiteList.Windows.Services
{
    public static class GeoDataUpdater
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly SemaphoreSlim _updateLock = new SemaphoreSlim(1, 1);

        // Default sources. Can be changed later (and source key will invalidate cache).
        // We download classic Xray/V2Ray geodata assets:
        // - geoip.dat
        // - geosite.dat
        // Russia-focused sources (updated frequently):
        // - geoip.dat: includes classic country geoip + RU-specific categories (ru-blocked, yandex, etc.)
        // - geosite.dat: includes domain-list-community + RU-specific categories (ru-available-only-inside, etc.)
        private const string GEOIP_URL = "https://raw.githubusercontent.com/runetfreedom/russia-blocked-geoip/release/geoip.dat";
        private const string GEOSITE_URL = "https://raw.githubusercontent.com/runetfreedom/russia-blocked-geosite/release/geosite.dat";

        public static string GetGeoDataDir()
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ByeWhiteList");
            return Path.Combine(appData, "geodata");
        }

        public static string GetGeoIpPath() => Path.Combine(GetGeoDataDir(), "geoip.dat");
        public static string GetGeoSitePath() => Path.Combine(GetGeoDataDir(), "geosite.dat");

        public static void EnsureGeoDataFilesExistFromBundledAssets()
        {
            try
            {
                Directory.CreateDirectory(GetGeoDataDir());

                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var bundledGeoIp = Path.Combine(baseDir, "geoip.dat");
                var bundledGeoSite = Path.Combine(baseDir, "geosite.dat");

                if (!File.Exists(GetGeoIpPath()) && File.Exists(bundledGeoIp))
                    File.Copy(bundledGeoIp, GetGeoIpPath(), overwrite: false);

                if (!File.Exists(GetGeoSitePath()) && File.Exists(bundledGeoSite))
                    File.Copy(bundledGeoSite, GetGeoSitePath(), overwrite: false);
            }
            catch
            {
                // best-effort
            }
        }

        public static bool HasValidGeoData()
        {
            try
            {
                return File.Exists(GetGeoIpPath()) && new FileInfo(GetGeoIpPath()).Length > 0 &&
                       File.Exists(GetGeoSitePath()) && new FileInfo(GetGeoSitePath()).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<(bool Ok, string Message)> UpdateGeoDataAsync(bool force = false)
        {
            await _updateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // UA helps GitHub in some environments.
                if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
                    _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ByeWhiteList.Windows/1.0");

                EnsureGeoDataFilesExistFromBundledAssets();

                var lastUpdate = AppSettings.Instance.GeoDataLastUpdate;
                var hoursSinceUpdate = (DateTime.Now - lastUpdate).TotalHours;

                var currentSourceKey = $"geoip:{GEOIP_URL}\ngeosite:{GEOSITE_URL}";
                var storedSourceKey = AppSettings.Instance.GeoDataSourceKey ?? "";
                if (!string.Equals(storedSourceKey, currentSourceKey, StringComparison.Ordinal))
                    force = true;

                // Auto-update cadence: weekly. We still keep manual "force" update in UI.
                const double autoUpdateHours = 24 * 7;
                bool filesOk = HasValidGeoData();
                if (!force && lastUpdate != DateTime.MinValue && hoursSinceUpdate < autoUpdateHours && filesOk)
                    return (true, $"✅ geodata актуальна ({hoursSinceUpdate / 24:F0} д.)");

                Directory.CreateDirectory(GetGeoDataDir());

                // Download to temp + atomic replace to avoid corrupting assets on crash.
                var geoipTmp = Path.Combine(GetGeoDataDir(), $"geoip.{Guid.NewGuid():N}.tmp");
                var geositeTmp = Path.Combine(GetGeoDataDir(), $"geosite.{Guid.NewGuid():N}.tmp");

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    await DownloadToFileAsync(GEOIP_URL, geoipTmp, cts.Token).ConfigureAwait(false);
                    await DownloadToFileAsync(GEOSITE_URL, geositeTmp, cts.Token).ConfigureAwait(false);

                    ReplaceFile(geoipTmp, GetGeoIpPath());
                    ReplaceFile(geositeTmp, GetGeoSitePath());

                    AppSettings.Instance.GeoDataLastUpdate = DateTime.Now;
                    AppSettings.Instance.GeoDataSourceKey = currentSourceKey;
                    AppSettings.Instance.Save();

                    return (true, "✅ geodata обновлена (geoip/geosite)");
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(geoipTmp)) File.Delete(geoipTmp); } catch { }
                    try { if (File.Exists(geositeTmp)) File.Delete(geositeTmp); } catch { }

                    // If we already have valid files, keep working.
                    if (HasValidGeoData())
                        return (true, $"⚠️ Не удалось обновить geodata, но локальная версия есть: {ex.Message}");

                    return (false, $"❌ Не удалось скачать geodata: {ex.Message}");
                }
            }
            finally
            {
                _updateLock.Release();
            }
        }

        private static async Task DownloadToFileAsync(string url, string path, CancellationToken ct)
        {
            using var resp = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        private static void ReplaceFile(string tmpPath, string finalPath)
        {
            try
            {
                // File.Replace gives us atomic-ish semantics on NTFS.
                if (File.Exists(finalPath))
                {
                    var backup = finalPath + ".bak";
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                    File.Replace(tmpPath, finalPath, backup, ignoreMetadataErrors: true);
                    try { if (File.Exists(backup)) File.Delete(backup); } catch { }
                }
                else
                {
                    File.Move(tmpPath, finalPath);
                }
            }
            catch
            {
                // Fallback to move+overwrite.
                try { File.Copy(tmpPath, finalPath, overwrite: true); } catch { }
                try { File.Delete(tmpPath); } catch { }
            }
        }
    }
}
