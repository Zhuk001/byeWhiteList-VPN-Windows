using Newtonsoft.Json.Linq;
using System;

namespace ByeWhiteList.Windows.Services
{
    public static class UniversalParser
    {
        public static string GenerateConfig(string beanJson)
        {
            try
            {
                var outbound = ParseToSingBoxOutbound(beanJson);

                var config = new JObject
                {
                    ["log"] = new JObject { ["level"] = "warn" },
                    ["inbounds"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "tun",
                            ["tag"] = "tun-in",
                            ["interface_name"] = "byewhitelist0",
                            ["address"] = new JArray { "172.19.0.1/30" },
                            ["auto_route"] = true,
                            ["stack"] = "system"
                        },
                        new JObject
                        {
                            ["type"] = "socks",
                            ["tag"] = "socks-in",
                            ["listen"] = "127.0.0.1",
                            ["listen_port"] = 1080
                        },
                        new JObject
                        {
                            ["type"] = "http",
                            ["tag"] = "http-in",
                            ["listen"] = "127.0.0.1",
                            ["listen_port"] = 8080
                        }
                    },
                    ["outbounds"] = new JArray
                    {
                        outbound,
                        new JObject { ["type"] = "direct", ["tag"] = "direct" },
                        new JObject { ["type"] = "block", ["tag"] = "block" }
                    },
                    ["route"] = new JObject
                    {
                        ["final"] = "proxy",
                        ["rules"] = new JArray()
                    }
                };

                return config.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception ex)
            {
                LogManager.Add($"❌ Ошибка генерации конфига: {ex.Message}");
                return GenerateFallbackConfig();
            }
        }

        public static string GenerateTestConfig(string beanJson)
        {
            try
            {
                var outbound = ParseToSingBoxOutbound(beanJson);

                var testConfig = new JObject
                {
                    ["log"] = new JObject { ["level"] = "warn" },
                    ["inbounds"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "socks",
                            ["tag"] = "socks-in",
                            ["listen"] = "127.0.0.1",
                            ["listen_port"] = 1081
                        }
                    },
                    ["outbounds"] = new JArray { outbound, new JObject { ["type"] = "direct", ["tag"] = "direct" } },
                    ["route"] = new JObject { ["final"] = "proxy" }
                };

                return testConfig.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch (Exception ex)
            {
                LogManager.Add($"❌ Ошибка генерации тестового конфига: {ex.Message}");
                return GenerateFallbackConfig();
            }
        }

        private static string GenerateFallbackConfig()
        {
            return @"{
  ""log"": { ""level"": ""warn"" },
  ""inbounds"": [
    {
      ""type"": ""tun"",
      ""tag"": ""tun-in"",
      ""interface_name"": ""byewhitelist0"",
      ""address"": [ ""172.19.0.1/30"" ],
      ""auto_route"": true,
      ""stack"": ""system""
    },
    {
      ""type"": ""socks"",
      ""tag"": ""socks-in"",
      ""listen"": ""127.0.0.1"",
      ""listen_port"": 1080
    }
  ],
  ""outbounds"": [
    { ""type"": ""direct"", ""tag"": ""proxy"" },
    { ""type"": ""direct"", ""tag"": ""direct"" }
  ],
  ""route"": { ""final"": ""proxy"" }
}";
        }

        private static JObject ParseToSingBoxOutbound(string beanJson)
        {
            try
            {
                if (string.IsNullOrEmpty(beanJson))
                    return CreateDirectOutbound();

                var input = JObject.Parse(beanJson);
                string protocol = input["type"]?.Value<string>()?.ToLower() ?? "vless";

                var result = new JObject
                {
                    ["type"] = protocol,
                    ["tag"] = "proxy"
                };

                // Базовые поля
                string server = input["server"]?.Value<string>() ?? "";
                int port = input["port"]?.Value<int>() ?? 0;

                if (!string.IsNullOrEmpty(server) && port > 0)
                {
                    result["server"] = server;
                    result["server_port"] = port;
                }

                // Настройки в зависимости от протокола
                switch (protocol)
                {
                    case "vless":
                        string uuid = input["uuid"]?.Value<string>() ?? "";
                        if (!string.IsNullOrEmpty(uuid))
                            result["uuid"] = uuid;
                        break;

                    case "vmess":
                        uuid = input["uuid"]?.Value<string>() ?? "";
                        if (!string.IsNullOrEmpty(uuid))
                            result["uuid"] = uuid;
                        result["security"] = input["security"]?.Value<string>() ?? "auto";
                        result["alter_id"] = input["alterId"]?.Value<int>() ?? input["aid"]?.Value<int>() ?? 0;
                        break;

                    case "trojan":
                        string password = input["password"]?.Value<string>() ?? "";
                        if (!string.IsNullOrEmpty(password))
                            result["password"] = password;
                        break;

                    case "shadowsocks":
                        result["method"] = input["method"]?.Value<string>() ?? "chacha20-ietf-poly1305";
                        password = input["password"]?.Value<string>() ?? "";
                        if (!string.IsNullOrEmpty(password))
                            result["password"] = password;
                        break;
                }

                // ========== ТРАНСПОРТ (поддержка всех типов) ==========
                string network = input["network"]?.Value<string>()?.ToLower() ?? "";

                if (!string.IsNullOrEmpty(network) && network != "tcp")
                {
                    var transport = new JObject();

                    switch (network)
                    {
                        case "ws":
                        case "websocket":
                            transport["type"] = "ws";
                            transport["path"] = input["path"]?.Value<string>() ?? "/";
                            string host = input["host"]?.Value<string>() ?? "";
                            if (!string.IsNullOrEmpty(host))
                            {
                                var headers = new JObject();
                                headers["Host"] = host;
                                transport["headers"] = headers;
                            }
                            transport["max_early_data"] = input["max_early_data"]?.Value<int>() ?? 0;
                            transport["early_data_header_name"] = input["early_data_header_name"]?.Value<string>() ?? "";
                            break;

                        case "grpc":
                            transport["type"] = "grpc";
                            transport["service_name"] = input["serviceName"]?.Value<string>()
                                ?? input["service_name"]?.Value<string>()
                                ?? "";
                            transport["idle_timeout"] = input["idle_timeout"]?.Value<string>() ?? "15s";
                            break;

                        case "httpupgrade":
                            transport["type"] = "httpupgrade";
                            transport["path"] = input["path"]?.Value<string>() ?? "/";
                            host = input["host"]?.Value<string>() ?? "";
                            if (!string.IsNullOrEmpty(host))
                                transport["host"] = host;
                            break;

                        case "h2":
                        case "http":
                            transport["type"] = "http";
                            transport["path"] = input["path"]?.Value<string>() ?? "/";
                            host = input["host"]?.Value<string>() ?? "";
                            if (!string.IsNullOrEmpty(host))
                                transport["host"] = new JArray(host);
                            break;

                        case "quic":
                            transport["type"] = "quic";
                            break;
                    }

                    result["transport"] = transport;
                    LogManager.Add($"🔄 Транспорт: {network}");
                }

                // ========== TLS/REALITY НАСТРОЙКИ (универсальные) ==========
                string security = input["security"]?.Value<string>()?.ToLower() ?? "";
                string realityPubKey = input["reality_pub_key"]?.Value<string>() ?? "";
                string tlsSetting = input["tls"]?.Value<string>()?.ToLower() ?? "";
                string flow = input["flow"]?.Value<string>() ?? "";

                bool useTls = security == "tls" || security == "reality" || tlsSetting == "tls" || !string.IsNullOrEmpty(realityPubKey);
                bool useReality = security == "reality" || !string.IsNullOrEmpty(realityPubKey);

                if (useTls)
                {
                    var tls = new JObject();
                    tls["enabled"] = true;
                    tls["server_name"] = input["sni"]?.Value<string>()
                        ?? input["serverName"]?.Value<string>()
                        ?? server;
                    tls["insecure"] = input["allowInsecure"]?.Value<bool>() ?? false;
                    tls["utls"] = new JObject
                    {
                        ["enabled"] = true,
                        ["fingerprint"] = input["fingerprint"]?.Value<string>()
                            ?? input["fp"]?.Value<string>()
                            ?? "chrome"
                    };

                    // ALPN
                    string alpn = input["alpn"]?.Value<string>() ?? "";
                    if (!string.IsNullOrEmpty(alpn))
                    {
                        tls["alpn"] = new JArray(alpn.Split(','));
                    }

                    // REALITY
                    if (useReality && !string.IsNullOrEmpty(realityPubKey))
                    {
                        tls["reality"] = new JObject
                        {
                            ["enabled"] = true,
                            ["public_key"] = realityPubKey,
                            ["short_id"] = input["reality_short_id"]?.Value<string>()
                                ?? input["sid"]?.Value<string>()
                                ?? ""
                        };
                        LogManager.Add($"🔒 REALITY настроен");
                    }

                    result["tls"] = tls;
                    LogManager.Add($"🔒 TLS настроен");
                }

                LogManager.Add($"✅ Outbound сгенерирован для {protocol}");
                return result;
            }
            catch (Exception ex)
            {
                LogManager.Add($"❌ Ошибка парсинга: {ex.Message}");
                return CreateDirectOutbound();
            }
        }

        private static JObject CreateDirectOutbound()
        {
            return new JObject
            {
                ["type"] = "direct",
                ["tag"] = "proxy"
            };
        }
    }
}
