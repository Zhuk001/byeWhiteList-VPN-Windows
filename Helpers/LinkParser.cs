using System;
using System.Text;
using System.Web;
using ByeWhiteList.Windows.Models;
using Newtonsoft.Json.Linq;

namespace ByeWhiteList.Windows.Helpers
{
    public class LinkParser
    {
        public static ProxyEntity? Parse(string link)
        {
            if (string.IsNullOrEmpty(link)) return null;

            try
            {
                if (link.StartsWith("vless://"))
                    return ParseVless(link);
                if (link.StartsWith("vmess://"))
                    return ParseVmess(link);
                if (link.StartsWith("trojan://"))
                    return ParseTrojan(link);
                if (link.StartsWith("ss://"))
                    return ParseShadowsocks(link);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка парсинга: {ex.Message}");
            }

            return null;
        }

        private static string? ExtractNameFromFragment(string link)
        {
            try
            {
                var hashIndex = link.IndexOf('#');
                if (hashIndex >= 0 && hashIndex + 1 < link.Length)
                {
                    var fragment = link.Substring(hashIndex + 1);
                    if (!string.IsNullOrEmpty(fragment))
                    {
                        return HttpUtility.UrlDecode(fragment);
                    }
                }
            }
            catch { }
            return null;
        }

        private static string TruncateName(string name, int maxLength = 35)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (name.Length <= maxLength) return name;
            return name.Substring(0, maxLength - 3) + "...";
        }

        private static ProxyEntity? ParseVless(string link)
        {
            var uri = new Uri(link);
            var name = ExtractNameFromFragment(link);

            if (string.IsNullOrEmpty(name))
            {
                name = $"{uri.Host}:{uri.Port}";
            }
            else
            {
                name = TruncateName(name);
            }

            var query = HttpUtility.ParseQueryString(uri.Query);
            var encryption = query["encryption"] ?? "none";
            var flow = query["flow"] ?? "";
            var security = query["security"] ?? "";
            var sni = query["sni"] ?? uri.Host;
            var pbk = query["pbk"] ?? "";
            var sid = query["sid"] ?? "";
            var type = query["type"] ?? "tcp";
            var fp = query["fp"] ?? "chrome";
            var path = query["path"] ?? "/";
            var host = query["host"] ?? uri.Host;
            var alpn = query["alpn"] ?? "";

            // Создаём универсальный BeanJson для UniversalParser
            var bean = new JObject();
            bean["type"] = "vless";
            bean["server"] = uri.Host;
            bean["port"] = uri.Port;
            bean["uuid"] = uri.UserInfo;
            bean["encryption"] = encryption;
            bean["flow"] = flow;
            bean["network"] = type;

            if (type == "ws")
            {
                bean["path"] = path;
                bean["host"] = host;
            }

            if (security == "tls" || security == "reality")
            {
                bean["security"] = "tls";
                bean["sni"] = sni;
                bean["fingerprint"] = fp;

                if (security == "reality")
                {
                    bean["reality_pub_key"] = pbk;
                    bean["reality_short_id"] = sid;
                }
            }

            if (!string.IsNullOrEmpty(alpn))
            {
                bean["alpn"] = alpn;
            }

            bean["raw_url"] = link;

            var beanJson = bean.ToString(Newtonsoft.Json.Formatting.None);

            System.Diagnostics.Debug.WriteLine($"✅ VLESS парсинг: {name}, server={uri.Host}, port={uri.Port}");

            return new ProxyEntity
            {
                Type = 4,
                BeanJson = beanJson,
                Name = name
            };
        }

        private static ProxyEntity? ParseVmess(string link)
        {
            try
            {
                var base64 = link.Replace("vmess://", "");
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                var obj = JObject.Parse(json);

                var name = obj["ps"]?.Value<string>() ?? "VMess";
                name = TruncateName(name);

                var server = obj["add"]?.Value<string>() ?? "";
                var port = obj["port"]?.Value<int>() ?? 0;
                var uuid = obj["id"]?.Value<string>() ?? "";
                var aid = obj["aid"]?.Value<int>() ?? 0;
                var security = obj["scy"]?.Value<string>() ?? "auto";
                var network = obj["net"]?.Value<string>() ?? "tcp";
                var path = obj["path"]?.Value<string>() ?? "/";
                var host = obj["host"]?.Value<string>() ?? "";
                var tls = obj["tls"]?.Value<string>() ?? "";
                var sni = obj["sni"]?.Value<string>() ?? server;

                var bean = new JObject();
                bean["type"] = "vmess";
                bean["server"] = server;
                bean["port"] = port;
                bean["uuid"] = uuid;
                bean["alterId"] = aid;
                bean["security"] = security;
                bean["network"] = network;

                if (network == "ws")
                {
                    bean["path"] = path;
                    if (!string.IsNullOrEmpty(host))
                        bean["host"] = host;
                }

                if (tls == "tls")
                {
                    bean["security"] = "tls";
                    bean["sni"] = sni;
                }

                bean["raw_url"] = link;

                var beanJson = bean.ToString(Newtonsoft.Json.Formatting.None);

                System.Diagnostics.Debug.WriteLine($"✅ VMess парсинг: {name}, server={server}, port={port}");

                return new ProxyEntity
                {
                    Type = 4,
                    BeanJson = beanJson,
                    Name = name
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VMess парсинг ошибка: {ex.Message}");
                return null;
            }
        }

        private static ProxyEntity? ParseTrojan(string link)
        {
            var uri = new Uri(link);
            var name = ExtractNameFromFragment(link);
            if (string.IsNullOrEmpty(name))
                name = $"Trojan - {uri.Host}:{uri.Port}";
            else
                name = TruncateName(name);

            var query = HttpUtility.ParseQueryString(uri.Query);
            var security = query["security"] ?? "tls";
            var sni = query["sni"] ?? uri.Host;
            var alpn = query["alpn"] ?? "";
            var fp = query["fp"] ?? "chrome";

            var bean = new JObject();
            bean["type"] = "trojan";
            bean["server"] = uri.Host;
            bean["port"] = uri.Port;
            bean["password"] = uri.UserInfo;
            bean["security"] = security;
            bean["sni"] = sni;
            bean["fingerprint"] = fp;

            if (!string.IsNullOrEmpty(alpn))
            {
                bean["alpn"] = alpn;
            }

            bean["raw_url"] = link;

            var beanJson = bean.ToString(Newtonsoft.Json.Formatting.None);

            System.Diagnostics.Debug.WriteLine($"✅ Trojan парсинг: {name}, server={uri.Host}, port={uri.Port}");

            return new ProxyEntity
            {
                Type = 6,
                BeanJson = beanJson,
                Name = name
            };
        }

        private static ProxyEntity? ParseShadowsocks(string link)
        {
            var uri = new Uri(link);
            var name = ExtractNameFromFragment(link);
            if (string.IsNullOrEmpty(name))
                name = $"SS - {uri.Host}:{uri.Port}";
            else
                name = TruncateName(name);

            // Для Shadowsocks парсим метод и пароль из UserInfo
            var userInfo = uri.UserInfo;
            var method = "chacha20-ietf-poly1305";
            var password = userInfo;

            if (userInfo.Contains(":"))
            {
                var parts = userInfo.Split(':');
                if (parts.Length >= 2)
                {
                    method = parts[0];
                    password = parts[1];
                }
            }

            var bean = new JObject();
            bean["type"] = "shadowsocks";
            bean["server"] = uri.Host;
            bean["port"] = uri.Port;
            bean["method"] = method;
            bean["password"] = password;

            bean["raw_url"] = link;

            var beanJson = bean.ToString(Newtonsoft.Json.Formatting.None);

            System.Diagnostics.Debug.WriteLine($"✅ SS парсинг: {name}, server={uri.Host}, port={uri.Port}, method={method}");

            return new ProxyEntity
            {
                Type = 2,
                BeanJson = beanJson,
                Name = name
            };
        }
    }
}
