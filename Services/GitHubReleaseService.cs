using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ByeWhiteList.Windows.Services
{
    public sealed record GitHubAsset(string Name, string DownloadUrl, long Size);

    public sealed record GitHubReleaseInfo(
        string Tag,
        Version Version,
        string Name,
        string Body,
        IReadOnlyList<GitHubAsset> Assets);

    public static class GitHubReleaseService
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/Zhuk001/byeWhiteList-VPN-Windows/releases/latest";

        public static async Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken token)
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            // GitHub API requires a User-Agent.
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("byeWhiteList", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await http.GetAsync(LatestReleaseUrl, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? (tagEl.GetString() ?? "") : "";
            var name = root.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "") : "";
            var body = root.TryGetProperty("body", out var bodyEl) ? (bodyEl.GetString() ?? "") : "";

            if (!TryParseTagVersion(tag, out var ver))
                return null;

            var assets = new List<GitHubAsset>();
            if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assetsEl.EnumerateArray())
                {
                    var an = a.TryGetProperty("name", out var anEl) ? (anEl.GetString() ?? "") : "";
                    var url = a.TryGetProperty("browser_download_url", out var urlEl) ? (urlEl.GetString() ?? "") : "";
                    var size = a.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var s) ? s : 0;
                    if (string.IsNullOrWhiteSpace(an) || string.IsNullOrWhiteSpace(url))
                        continue;
                    assets.Add(new GitHubAsset(an, url, size));
                }
            }

            return new GitHubReleaseInfo(tag, ver, string.IsNullOrWhiteSpace(name) ? tag : name, body ?? "", assets);
        }

        public static bool TryParseTagVersion(string tag, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(tag))
                return false;

            tag = tag.Trim();
            if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                tag = tag.Substring(1);

            // Accept "1.2.3" or "1.2" etc.
            if (!Version.TryParse(tag, out var parsed) || parsed == null)
                return false;

            version = parsed;
            return true;
        }

        public static GitHubAsset? PickBestAsset(IReadOnlyList<GitHubAsset> assets)
        {
            if (assets == null || assets.Count == 0)
                return null;

            // Prefer portable ZIP for safe self-update.
            var zip = assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (zip != null)
                return zip;

            // Fallback: installer EXE (will be launched, but not silently upgraded by default).
            var exe = assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            return exe;
        }

        public static string GetCurrentVersionString()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();

                // Prefer informational version (maps to <Version>/<InformationalVersion> in csproj).
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    var normalized = info.Trim();
                    // Allow "1.2.3+sha" or "1.2.3" or "v1.2.3"
                    var plus = normalized.IndexOf('+');
                    if (plus >= 0) normalized = normalized.Substring(0, plus);
                    if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(1);
                    if (Version.TryParse(normalized, out var vInfo) && vInfo != null)
                        return $"{vInfo.Major}.{vInfo.Minor}.{Math.Max(0, vInfo.Build)}";
                }

                // Fallback: assembly version.
                var v = asm.GetName().Version;
                if (v != null)
                    return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            }
            catch { }
            return "0.0.0";
        }

        public static bool IsNewerThanCurrent(Version latest)
        {
            try
            {
                var curStr = GetCurrentVersionString();
                if (!Version.TryParse(curStr, out var cur) || cur == null)
                    return true;

                return latest > cur;
            }
            catch
            {
                return true;
            }
        }

        public static async Task DownloadFileAsync(string url, string destinationPath, Action<long, long?>? progress, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(10);
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("byeWhiteList", "1.0"));

            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var dst = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[128 * 1024];
            long readTotal = 0;
            while (true)
            {
                var read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read <= 0)
                    break;

                await dst.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                readTotal += read;
                progress?.Invoke(readTotal, total);
            }
        }
    }
}
