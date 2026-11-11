using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ParsGate.Models;

namespace ParsGate.Services
{
    public static class ConfigParser
    {
        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        public static VpnConfigItem ParseUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                throw new ArgumentException("URI cannot be empty", nameof(uri));

            var item = new VpnConfigItem { RawUri = uri.Trim() };

            try
            {
                if (uri.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                {
                    item.Protocol = "vless";
                    ParseVless(uri, item);
                }
                else if (uri.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
                {
                    item.Protocol = "vmess";
                    ParseVmess(uri, item);
                }
                else if (uri.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
                {
                    item.Protocol = "trojan";
                    ParseTrojan(uri, item);
                }
                else
                {
                    throw new ArgumentException($"Unsupported protocol: {uri.Substring(0, Math.Min(10, uri.Length))}...", nameof(uri));
                }

                // Generate display name if not set
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    item.Name = $"{item.Protocol.ToUpper()}: {item.ServerAddress}:{item.Port}";
                }
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Failed to parse URI: {ex.Message}", nameof(uri), ex);
            }

            return item;
        }

        private static void ParseVless(string uri, VpnConfigItem item)
        {
            var regex = new Regex(@"^vless://([^@]+)@([^:]+):(\d+)\??(.*)?#?(.*)?$");
            var match = regex.Match(uri);
            if (!match.Success)
                throw new ArgumentException("Invalid VLESS URI format");

            var uuid = match.Groups[1].Value;
            item.ServerAddress = match.Groups[2].Value;
            item.Port = int.Parse(match.Groups[3].Value);
            var query = match.Groups[4].Value;
            var name = Uri.UnescapeDataString(match.Groups[5].Value);
            if (!string.IsNullOrWhiteSpace(name)) item.Name = name;

            var queryParams = ParseQueryString(query);
            item.RawUri = uri; // Store original

            // Extract fragment parameters if present
            if (queryParams.TryGetValue("fragment", out var fragment) ||
                queryParams.TryGetValue("fragmentLength", out fragment) ||
                queryParams.TryGetValue("fragmentInterval", out fragment))
            {
                // Fragment params exist in URI
            }
        }

        private static void ParseVmess(string uri, VpnConfigItem item)
        {
            var base64 = uri.Substring("vmess://".Length);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var vmess = JsonSerializer.Deserialize<JsonObject>(json);

            if (vmess == null) throw new ArgumentException("Invalid VMess JSON");

            item.ServerAddress = vmess["add"]?.ToString() ?? string.Empty;
            item.Port = int.TryParse(vmess["port"]?.ToString(), out var port) ? port : 0;
            item.Name = vmess["ps"]?.ToString() ?? string.Empty;
            item.RawUri = uri;
        }

        private static void ParseTrojan(string uri, VpnConfigItem item)
        {
            var regex = new Regex(@"^trojan://([^@]+)@([^:]+):(\d+)\??(.*)?#?(.*)?$");
            var match = regex.Match(uri);
            if (!match.Success)
                throw new ArgumentException("Invalid Trojan URI format");

            var password = match.Groups[1].Value;
            item.ServerAddress = match.Groups[2].Value;
            item.Port = int.Parse(match.Groups[3].Value);
            var query = match.Groups[4].Value;
            var name = Uri.UnescapeDataString(match.Groups[5].Value);
            if (!string.IsNullOrWhiteSpace(name)) item.Name = name;

            var queryParams = ParseQueryString(query);
            item.RawUri = uri;
        }

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query)) return dict;

            var pairs = query.Split('&');
            foreach (var pair in pairs)
            {
                var kv = pair.Split('=');
                if (kv.Length == 2)
                {
                    dict[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
                }
            }
            return dict;
        }

        public static async Task<int> FetchAndUpdateSubscriptions(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var allConfigs = new List<VpnConfigItem>();
            var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var url in settings.SubscriptionUrls)
            {
                try
                {
                    var configs = await FetchSubscription(url);
                    foreach (var config in configs)
                    {
                        if (seenUris.Add(config.RawUri))
                        {
                            allConfigs.Add(config);
                        }
                    }
                }
                catch
                {
                    // Skip failed subscription, continue with others
                }
            }

            settings.Configs = allConfigs;
            SettingsManager.Save(settings);
            return allConfigs.Count;
        }

        private static async Task<List<VpnConfigItem>> FetchSubscription(string url)
        {
            var response = await HttpClient.GetStringAsync(url);
            var configs = new List<VpnConfigItem>();

            // Try to decode as Base64 first
            var lines = new List<string>();
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(response));
                lines.AddRange(decoded.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
            }
            catch
            {
                // Not Base64, treat as plain text
                lines.AddRange(response.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
            }

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                try
                {
                    var config = ParseUri(trimmed);
                    configs.Add(config);
                }
                catch
                {
                    // Skip invalid lines
                }
            }

            return configs;
        }

        public static string GenerateXrayConfigJson(VpnConfigItem item, AppSettings settings, int localSocksPort = 10808)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var config = new JsonObject
            {
                ["log"] = new JsonObject
                {
                    ["loglevel"] = "warning"
                },
                ["inbounds"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["port"] = localSocksPort,
                        ["listen"] = "127.0.0.1",
                        ["protocol"] = "socks",
                        ["settings"] = new JsonObject
                        {
                            ["udp"] = true,
                            ["auth"] = "noauth"
                        },
                        ["sniffing"] = new JsonObject
                        {
                            ["enabled"] = true,
                            ["destOverride"] = new JsonArray("http", "tls", "quic"),
                            ["routeOnly"] = false
                        }
                    }
                },
                ["outbounds"] = new JsonArray(),
                ["routing"] = new JsonObject
                {
                    ["domainStrategy"] = "IPIfNonMatch",
                    ["rules"] = new JsonArray()
                }
            };

            // Build the main outbound
            var outbound = BuildOutbound(item, settings);
            config["outbounds"]!.AsArray().Add(outbound);

            // Add direct and block outbounds
            config["outbounds"]!.AsArray().Add(new JsonObject
            {
                ["protocol"] = "freedom",
                ["tag"] = "direct"
            });
            config["outbounds"]!.AsArray().Add(new JsonObject
            {
                ["protocol"] = "blackhole",
                ["tag"] = "block"
            });

            // Add fragment outbound if needed
            bool hasFragmentInUri = item.RawUri.Contains("fragment", StringComparison.OrdinalIgnoreCase);
            bool forceFragment = settings.ForceFragmentEnabled;

            if (hasFragmentInUri || forceFragment)
            {
                var fragmentLength = ParseRange(settings.FragmentLength);
                var fragmentInterval = ParseRange(settings.FragmentInterval);

                config["outbounds"]!.AsArray().Add(new JsonObject
                {
                    ["protocol"] = "freedom",
                    ["tag"] = "fragment-out",
                    ["settings"] = new JsonObject
                    {
                        ["domainStrategy"] = "UseIP",
                        ["fragment"] = new JsonObject
                        {
                            ["packets"] = "tlshello",
                            ["length"] = fragmentLength,
                            ["interval"] = fragmentInterval
                        }
                    }
                });

                // Modify main outbound to use fragment dialer
                if (outbound.ContainsKey("streamSettings"))
                {
                    var streamSettings = outbound["streamSettings"] as JsonObject;
                    if (streamSettings != null)
                    {
                        streamSettings["sockopt"] = new JsonObject
                        {
                            ["dialerProxy"] = "fragment-out"
                        };
                    }
                }
            }

            // Add routing rules
            var routing = config["routing"] as JsonObject;
            var rules = routing!["rules"] as JsonArray;

            // Default rule: all traffic through proxy
            rules!.Add(new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = "proxy",
                ["network"] = "tcp,udp"
            });

            return JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        private static JsonObject BuildOutbound(VpnConfigItem item, AppSettings settings)
        {
            var outbound = new JsonObject
            {
                ["protocol"] = item.Protocol,
                ["tag"] = "proxy",
                ["settings"] = new JsonObject()
            };

            var settingsObj = outbound["settings"] as JsonObject;
            var streamSettings = new JsonObject();

            switch (item.Protocol.ToLowerInvariant())
            {
                case "vless":
                    settingsObj!["vnext"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["address"] = item.ServerAddress,
                            ["port"] = item.Port,
                            ["users"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = ExtractVlessUuid(item.RawUri),
                                    ["encryption"] = "none",
                                    ["flow"] = ExtractQueryParam(item.RawUri, "flow") ?? ""
                                }
                            }
                        }
                    };
                    break;

                case "vmess":
                    settingsObj!["vnext"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["address"] = item.ServerAddress,
                            ["port"] = item.Port,
                            ["users"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["id"] = ExtractVmessId(item.RawUri),
                                    ["alterId"] = 0,
                                    ["security"] = "auto"
                                }
                            }
                        }
                    };
                    break;

                case "trojan":
                    settingsObj!["servers"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["address"] = item.ServerAddress,
                            ["port"] = item.Port,
                            ["password"] = ExtractTrojanPassword(item.RawUri)
                        }
                    };
                    break;
            }

            // Stream settings
            var security = ExtractQueryParam(item.RawUri, "security") ?? "none";
            var type = ExtractQueryParam(item.RawUri, "type") ?? "tcp";

            streamSettings["network"] = type;
            streamSettings["security"] = security;

            if (security == "tls" || security == "reality")
            {
                var tlsSettings = new JsonObject
                {
                    ["serverName"] = ExtractQueryParam(item.RawUri, "sni") ?? item.ServerAddress,
                    ["allowInsecure"] = false,
                    ["fingerprint"] = ExtractQueryParam(item.RawUri, "fp") ?? "chrome"
                };

                if (security == "reality")
                {
                    tlsSettings["publicKey"] = ExtractQueryParam(item.RawUri, "pbk") ?? "";
                    tlsSettings["shortId"] = ExtractQueryParam(item.RawUri, "sid") ?? "";
                }

                streamSettings["tlsSettings"] = tlsSettings;
            }

            // Transport settings
            switch (type)
            {
                case "ws":
                    streamSettings["wsSettings"] = new JsonObject
                    {
                        ["path"] = ExtractQueryParam(item.RawUri, "path") ?? "/",
                        ["headers"] = new JsonObject
                        {
                            ["Host"] = ExtractQueryParam(item.RawUri, "host") ?? item.ServerAddress
                        }
                    };
                    break;

                case "grpc":
                    streamSettings["grpcSettings"] = new JsonObject
                    {
                        ["serviceName"] = ExtractQueryParam(item.RawUri, "serviceName") ?? "",
                        ["multiMode"] = false
                    };
                    break;

                case "http":
                case "xhttp":
                    streamSettings["httpSettings"] = new JsonObject
                    {
                        ["path"] = ExtractQueryParam(item.RawUri, "path") ?? "/",
                        ["host"] = new JsonArray(ExtractQueryParam(item.RawUri, "host") ?? item.ServerAddress)
                    };
                    break;
            }

            outbound["streamSettings"] = streamSettings;

            return outbound;
        }

        private static string ExtractVlessUuid(string uri)
        {
            var match = Regex.Match(uri, @"^vless://([^@]+)@");
            return match.Success ? match.Groups[1].Value : "";
        }

        private static string ExtractVmessId(string uri)
        {
            try
            {
                var base64 = uri.Substring("vmess://".Length);
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                var vmess = JsonSerializer.Deserialize<JsonObject>(json);
                return vmess?["id"]?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string ExtractTrojanPassword(string uri)
        {
            var match = Regex.Match(uri, @"^trojan://([^@]+)@");
            return match.Success ? match.Groups[1].Value : "";
        }

        private static string? ExtractQueryParam(string uri, string key)
        {
            var match = Regex.Match(uri, $@"[?&]{Regex.Escape(key)}=([^&#]+)");
            return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
        }

        private static int ParseRange(string range)
        {
            if (string.IsNullOrWhiteSpace(range)) return 10;
            var parts = range.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out var min) && int.TryParse(parts[1], out var max))
            {
                var random = new Random();
                return random.Next(min, max + 1);
            }
            return int.TryParse(range, out var single) ? single : 10;
        }
    }
}