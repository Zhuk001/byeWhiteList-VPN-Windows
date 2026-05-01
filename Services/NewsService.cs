using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ByeWhiteList.Windows.Services
{
    public sealed class NewsRoot
    {
        [JsonProperty("last_update")]
        public DateTime? LastUpdate { get; set; }

        [JsonProperty("news")]
        public List<NewsItem> News { get; set; } = new();
    }

    public sealed class NewsItem
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("date")]
        public string? Date { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("content")]
        public string? Content { get; set; }

        [JsonProperty("link")]
        public string? Link { get; set; }

        [JsonProperty("important")]
        public bool Important { get; set; }
    }

    public static class NewsService
    {
        private const string NewsUrl = "https://gist.githubusercontent.com/Zhuk001/b365c8d5e9e14790938410cf9514cc17/raw/18825bd32a8c3248f8e9d2e767e0f27cc3daf637/byeWhiteList";

        private static NewsRoot? _cached;
        private static DateTime _cachedAtUtc;

        public static async Task<NewsRoot> GetNewsAsync(CancellationToken token)
        {
            if (_cached != null && (DateTime.UtcNow - _cachedAtUtc) < TimeSpan.FromMinutes(2))
                return _cached;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("byeWhiteList", "1.0"));

            var json = await http.GetStringAsync(NewsUrl, token).ConfigureAwait(false);
            var root = JsonConvert.DeserializeObject<NewsRoot>(json ?? "") ?? new NewsRoot();

            _cached = root;
            _cachedAtUtc = DateTime.UtcNow;

            return root;
        }
    }
}