using System;
using System.Collections.Generic;
using System.Linq;

namespace ByeWhiteList.Windows.Services
{
    public sealed record GeoRoutingProfile(
        string Id,
        string DisplayName,
        string GeoSiteTag,
        string GeoIpTag);

    public static class GeoRoutingProfiles
    {
        // NOTE:
        // We use classic Xray/V2Ray tags:
        // - geosite:xxx (from geosite.dat)
        // - geoip:xx   (from geoip.dat)
        //
        // Important: geosite tag names depend on the geosite.dat build you use.
        //
        // GeoSiteTag can contain multiple tags separated by newline (they will be split by the config generator).
        public static readonly IReadOnlyList<GeoRoutingProfile> Profiles = new List<GeoRoutingProfile>
        {
            // RU (recommended): direct for Russian geoip + RU-only whitelist domains.
            // - geoip:ru is used for IP-based routing
            // - geosite:ru-available-only-inside is a curated list of services that usually break behind VPN
            new("ru", "🇷🇺 Россия (DIRECT для RU)", "geosite:ru-available-only-inside", "geoip:ru"),

            // For most other countries, geoip is always available. geosite country-specific tags are not guaranteed.
            // Keep GeoSiteTag empty and rely on geoip, plus user overrides in settings.
            new("ua", "🇺🇦 Украина (DIRECT для UA)", "", "geoip:ua"),
            new("by", "🇧🇾 Беларусь (DIRECT для BY)", "", "geoip:by"),
            new("kz", "🇰🇿 Казахстан (DIRECT для KZ)", "", "geoip:kz"),
            new("de", "🇩🇪 Германия (DIRECT для DE)", "", "geoip:de"),
            new("nl", "🇳🇱 Нидерланды (DIRECT для NL)", "", "geoip:nl"),
            new("fr", "🇫🇷 Франция (DIRECT для FR)", "", "geoip:fr"),
            new("tr", "🇹🇷 Турция (DIRECT для TR)", "", "geoip:tr"),
            new("us", "🇺🇸 США (DIRECT для US)", "", "geoip:us"),

            // CN has GEOLOCATION-CN in many geosite builds, so we can use it.
            new("cn", "🇨🇳 Китай (DIRECT для CN)", "geosite:geolocation-cn", "geoip:cn"),
        };

        public static GeoRoutingProfile Get(string? id)
        {
            id = (id ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(id))
                return Profiles[0];

            return Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                   ?? Profiles[0];
        }
    }
}

