using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ByeWhiteList.Windows.Services
{
    internal static class XrayBootstrapper
    {
        private const string LatestReleaseApi = "https://api.github.com/repos/XTLS/Xray-core/releases/latest";

        private sealed class GitHubRelease
        {
            public string? tag_name { get; set; }
            public List<GitHubAsset>? assets { get; set; }
        }

        private sealed class GitHubAsset
        {
            public string? name { get; set; }
            public string? browser_download_url { get; set; }
            public long size { get; set; }
        }

        public static string GetAppFolderXrayPath() =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xray.exe");

        public static string GetAppDataXrayPath()
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ByeWhiteList",
                "bin");
            return Path.Combine(baseDir, "xray.exe");
        }

        public static string ResolveExistingXrayPath()
        {
            var appFolder = GetAppFolderXrayPath();
            if (File.Exists(appFolder))
                return appFolder;

            var appData = GetAppDataXrayPath();
            if (File.Exists(appData))
                return appData;

            return appFolder; // default for logs
        }

        private static bool IsDirectoryWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, $".write_test_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string GetPreferredInstallPath()
        {
            // Prefer app folder (portable), but fallback to LocalAppData when app folder is not writable.
            var appFolder = Path.GetDirectoryName(GetAppFolderXrayPath())!;
            if (IsDirectoryWritable(appFolder))
                return GetAppFolderXrayPath();

            return GetAppDataXrayPath();
        }

        public static async Task<string> DownloadLatestXrayAsync(Action<string>? log, CancellationToken token)
        {
            var targetPath = GetPreferredInstallPath();
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            using var http = CreateHttpClient();
            log?.Invoke("⏳ Получаю информацию о релизе Xray…");

            var release = await GetLatestReleaseAsync(http, token).ConfigureAwait(false);
            if (release?.assets == null || release.assets.Count == 0)
                throw new InvalidOperationException("Не удалось получить список файлов релиза Xray");

            var tag = (release.tag_name ?? "").Trim();
            var zipAsset = PickWindowsZipAsset(release.assets);
            if (zipAsset?.browser_download_url == null)
                throw new InvalidOperationException("Не найден архив Xray для Windows (x64) в latest release");

            log?.Invoke($"⬇️ Скачиваю Xray {tag}…");

            var tempDir = Path.Combine(Path.GetTempPath(), $"byewhitelist_xray_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var zipPath = Path.Combine(tempDir, zipAsset.name ?? "xray.zip");

            await DownloadToFileAsync(http, zipAsset.browser_download_url, zipPath, token).ConfigureAwait(false);

            // Best-effort integrity check if digest exists.
            await TryVerifyDigestAsync(http, release.assets, zipAsset, zipPath, log, token).ConfigureAwait(false);

            var extractedExe = await ExtractXrayExeAsync(zipPath, tempDir, token).ConfigureAwait(false);
            if (extractedExe == null || !File.Exists(extractedExe))
                throw new InvalidOperationException("Не удалось извлечь xray.exe из архива");
            var sourceDir = tempDir;
            var targetDir = Path.GetDirectoryName(targetPath)!;

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(file);
                var dest = Path.Combine(targetDir, fileName);
                File.Copy(file, dest, true);
            }
            // Sanity-check it runs.
            var ver = await TryGetXrayVersionAsync(extractedExe, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(ver))
                throw new InvalidOperationException("Скачанный xray.exe не запускается (не удалось получить версию)");

            var tmpFinal = Path.Combine(Path.GetDirectoryName(targetPath)!, $"xray.new.{Guid.NewGuid():N}.exe");
            File.Copy(extractedExe, tmpFinal, overwrite: true);

            // Replace existing.
            try
            {
                if (File.Exists(targetPath))
                    File.Delete(targetPath);
            }
            catch { }

            File.Move(tmpFinal, targetPath, overwrite: true);

            log?.Invoke($"✅ Xray скачан: {ver}");
            return targetPath;
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };

            var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(2)
            };

            http.DefaultRequestHeaders.UserAgent.ParseAdd("ByeWhiteList-VPN-Windows/1.0");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        private static async Task<GitHubRelease?> GetLatestReleaseAsync(HttpClient http, CancellationToken token)
        {
            using var resp = await http.GetAsync(LatestReleaseApi, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: token).ConfigureAwait(false);
        }

        private static GitHubAsset? PickWindowsZipAsset(List<GitHubAsset> assets)
        {
            static bool IsCandidate(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return false;

                if (name.StartsWith("Source code", StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return false;

                var n = name.ToLowerInvariant();
                if (!n.Contains("windows"))
                    return false;

                return n.Contains("64") || n.Contains("amd64") || n.Contains("x86_64") || n.Contains("x64");
            }

            return assets
                .Where(a => a.name != null && IsCandidate(a.name))
                .OrderByDescending(a =>
                {
                    var n = (a.name ?? "").ToLowerInvariant();
                    if (n.Contains("xray") && (n.Contains("windows-64") || n.Contains("windows-amd64") || n.Contains("windows-x64")))
                        return 3;
                    if (n.Contains("xray"))
                        return 2;
                    return 1;
                })
                .ThenByDescending(a => a.size)
                .FirstOrDefault();
        }

        private static async Task DownloadToFileAsync(HttpClient http, string url, string path, CancellationToken token)
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await src.CopyToAsync(dst, token).ConfigureAwait(false);
        }

        private static async Task TryVerifyDigestAsync(
            HttpClient http,
            List<GitHubAsset> assets,
            GitHubAsset zipAsset,
            string zipPath,
            Action<string>? log,
            CancellationToken token)
        {
            try
            {
                var zipName = (zipAsset.name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(zipName))
                    return;

                GitHubAsset? digestAsset = null;
                foreach (var a in assets)
                {
                    var name = (a.name ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (name.Equals(zipName + ".dgst", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(zipName + ".sha256", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(zipName.Replace(".zip", ".zip.dgst", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(zipName.Replace(".zip", ".zip.sha256", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
                    {
                        digestAsset = a;
                        break;
                    }
                }

                if (digestAsset?.browser_download_url == null)
                {
                    log?.Invoke("ℹ️ Хэш-файл релиза не найден — пропускаю проверку SHA256.");
                    return;
                }

                log?.Invoke($"🔐 SHA256: найден хэш-файл `{digestAsset.name}`");

                var digestText = await http.GetStringAsync(digestAsset.browser_download_url, token).ConfigureAwait(false);
                var expected = ParseSha256FromDigest(digestText, zipName);
                if (string.IsNullOrWhiteSpace(expected))
                {
                    log?.Invoke("ℹ️ Не удалось распарсить SHA256 — пропускаю проверку.");
                    return;
                }

                var actual = ComputeSha256Hex(zipPath);
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"❌ SHA256 mismatch. expected={expected}, actual={actual}");
                    throw new InvalidOperationException($"SHA256 не совпал (expected={expected}, actual={actual})");
                }

                log?.Invoke($"✅ SHA256 OK: {actual}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Проверка SHA256 не прошла: " + ex.Message, ex);
            }
        }
private static string? ParseSha256FromDigest(string text, string fileName)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var m1 = Regex.Match(text, @"SHA256\([^)]+\)\s*=\s*([a-fA-F0-9]{64})");
            if (m1.Success)
                return m1.Groups[1].Value.Trim();

            foreach (var line in text.Split('\n'))
            {
                var l = line.Trim();
                if (string.IsNullOrWhiteSpace(l))
                    continue;

                var m2 = Regex.Match(l, @"^([a-fA-F0-9]{64})\s+(.+)$");
                if (!m2.Success)
                    continue;

                var hash = m2.Groups[1].Value.Trim();
                var name = m2.Groups[2].Value.Trim();
                if (name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                    return hash;
            }

            return null;
        }

        private static string ComputeSha256Hex(string path)
        {
            using var sha = SHA256.Create();
            using var fs = File.OpenRead(path);
            var hash = sha.ComputeHash(fs);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static async Task<string?> ExtractXrayExeAsync(string zipPath, string tempDir, CancellationToken token)
        {
            await using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

            string? extractedExePath = null;

            foreach (var entry in zip.Entries)
            {
                // Skip directories
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var fileName = Path.GetFileName(entry.FullName);
                var outPath = Path.Combine(tempDir, fileName);

                await using var es = entry.Open();
                await using var os = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await es.CopyToAsync(os, token).ConfigureAwait(false);

                if (fileName.Equals("xray.exe", StringComparison.OrdinalIgnoreCase))
                    extractedExePath = outPath;
            }

            return extractedExePath;
        }

        private static async Task<string?> TryGetXrayVersionAsync(string xrayExePath, CancellationToken token)
        {
            try
            {
                if (!File.Exists(xrayExePath))
                    return null;

                var psi = new ProcessStartInfo
                {
                    FileName = xrayExePath,
                    Arguments = "version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var p = Process.Start(psi);
                if (p == null)
                    return null;

                var outputTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();

                await Task.WhenAny(Task.WhenAll(outputTask, errTask), Task.Delay(TimeSpan.FromSeconds(5), token)).ConfigureAwait(false);
                try { await p.WaitForExitAsync(token).ConfigureAwait(false); } catch { }

                var text = (await outputTask.ConfigureAwait(false)) + "\n" + (await errTask.ConfigureAwait(false));
                var m = Regex.Match(text, @"Xray\s+(\d+\.\d+\.\d+)");
                if (m.Success)
                    return "v" + m.Groups[1].Value;

                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}

