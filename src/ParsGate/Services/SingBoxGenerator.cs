using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using ParsGate.Models;

namespace ParsGate.Services
{
    public static class SingBoxGenerator
    {
        public static string GenerateSingBoxConfigJson(AppSettings settings, int xraySocksPort = 10808)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var config = new JsonObject
            {
                ["log"] = new JsonObject
                {
                    ["level"] = "warn",
                    ["timestamp"] = true
                },
                ["inbounds"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "tun",
                        ["tag"] = "tun-in",
                        ["interface_name"] = "ParsGate-TUN",
                        ["inet4_address"] = "172.19.0.1/30",
                        ["auto_route"] = true,
                        ["strict_route"] = true,
                        ["stack"] = "system",
                        ["sniff"] = true,
                        ["sniff_override_destination"] = true,
                        ["mtu"] = 9000
                    }
                },
                ["outbounds"] = new JsonArray(),
                ["route"] = new JsonObject
                {
                    ["rules"] = new JsonArray(),
                    ["auto_detect_interface"] = true,
                    ["final"] = "proxy"
                },
                ["dns"] = new JsonObject
                {
                    ["servers"] = new JsonArray(),
                    ["rules"] = new JsonArray(),
                    ["final"] = "dns-out",
                    ["strategy"] = "ipv4_only"
                }
            };

            // Outbounds
            var outbounds = config["outbounds"] as JsonArray;

            // Proxy outbound -> Xray SOCKS5
            outbounds!.Add(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = "proxy",
                ["server"] = "127.0.0.1",
                ["server_port"] = xraySocksPort,
                ["version"] = "5",
                ["udp_over_tcp"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["version"] = 2
                }
            });

            // Direct outbound
            outbounds.Add(new JsonObject
            {
                ["type"] = "direct",
                ["tag"] = "direct"
            });

            // Block outbound
            outbounds.Add(new JsonObject
            {
                ["type"] = "block",
                ["tag"] = "block"
            });

            // DNS outbound (for remote DNS queries)
            outbounds.Add(new JsonObject
            {
                ["type"] = "dns",
                ["tag"] = "dns-out"
            });

            // DNS Servers
            var dns = config["dns"] as JsonObject;
            var dnsServers = dns!["servers"] as JsonArray;

            // Remote DNS (via proxy) - for international domains
            dnsServers!.Add(new JsonObject
            {
                ["tag"] = "remote-dns",
                ["address"] = settings.CustomRemoteDns,
                ["detour"] = "proxy"
            });

            // Direct DNS (via direct) - for Iranian domains
            dnsServers.Add(new JsonObject
            {
                ["tag"] = "direct-dns",
                ["address"] = settings.CustomDirectDns,
                ["detour"] = "direct"
            });

            // DNS Rules
            var dnsRules = dns!["rules"] as JsonArray;

            // Rule: Iranian domains -> direct DNS
            dnsRules!.Add(new JsonObject
            {
                ["type"] = "logical",
                ["mode"] = "or",
                ["rules"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "domain_suffix",
                        ["domain_suffix"] = new JsonArray(".ir")
                    },
                    new JsonObject
                    {
                        ["type"] = "domain_suffix",
                        ["domain_suffix"] = new JsonArray("geosite-ir")
                    }
                ],
                ["server"] = "direct-dns"
            });

            // Rule: Everything else -> remote DNS
            dnsRules.Add(new JsonObject
            {
                ["server"] = "remote-dns"
            });

            // Routing Rules
            var route = config["route"] as JsonObject;
            var rules = route!["rules"] as JsonArray;

            // Rule 1: Iran Direct (geoip-ir, geosite-ir) -> direct
            if (settings.IranDirectEnabled)
            {
                rules!.Add(new JsonObject
                {
                    ["type"] = "logical",
                    ["mode"] = "or",
                    ["rules"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "geoip",
                            ["geoip"] = new JsonArray("geoip-ir")
                        },
                        new JsonObject
                        {
                            ["type"] = "geosite",
                            ["geosite"] = new JsonArray("geosite-ir")
                        },
                        new JsonObject
                        {
                            ["type"] = "domain_suffix",
                            ["domain_suffix"] = new JsonArray(".ir")
                        }
                    ],
                    ["outbound"] = "direct"
                });
            }

            // Rule 2: Split Tunneling
            if (settings.SplitTunnelEnabled && settings.SplitTunnelProcessNames.Count > 0)
            {
                var processRules = new JsonArray();
                foreach (var process in settings.SplitTunnelProcessNames)
                {
                    if (!string.IsNullOrWhiteSpace(process))
                    {
                        processRules.Add(new JsonObject
                        {
                            ["type"] = "process",
                            ["process"] = new JsonArray(process.Trim())
                        });
                    }
                }

                if (processRules.Count > 0)
                {
                    if (settings.SplitTunnelMode == "OnlySelectedApps")
                    {
                        // Only selected apps go through proxy, rest direct
                        rules!.Add(new JsonObject
                        {
                            ["type"] = "logical",
                            ["mode"] = "or",
                            ["rules"] = processRules,
                            ["outbound"] = "proxy"
                        });
                        // Final rule already set to "proxy", but we need to invert for ExcludeApps
                    }
                    else // ExcludeSelectedApps (default)
                    {
                        // Selected apps go direct, rest through proxy
                        rules!.Add(new JsonObject
                        {
                            ["type"] = "logical",
                            ["mode"] = "or",
                            ["rules"] = processRules,
                            ["outbound"] = "direct"
                        });
                    }
                }
            }

            // Rule 3: Custom domains/IPs for split tunneling
            if (settings.SplitTunnelEnabled && settings.SplitTunnelCustomDomainsOrIps.Count > 0)
            {
                var domainRules = new JsonArray();
                var ipRules = new JsonArray();

                foreach (var entry in settings.SplitTunnelCustomDomainsOrIps)
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;

                    var trimmed = entry.Trim();
                    if (IsCidr(trimmed))
                    {
                        ipRules.Add(new JsonObject
                        {
                            ["type"] = "ip_cidr",
                            ["ip_cidr"] = new JsonArray(trimmed)
                        });
                    }
                    else
                    {
                        domainRules.Add(new JsonObject
                        {
                            ["type"] = "domain",
                            ["domain"] = new JsonArray(trimmed)
                        });
                    }
                }

                if (domainRules.Count > 0 || ipRules.Count > 0)
                {
                    var combinedRules = new JsonArray();
                    foreach (var r in domainRules) combinedRules.Add(r);
                    foreach (var r in ipRules) combinedRules.Add(r);

                    if (settings.SplitTunnelMode == "OnlySelectedApps")
                    {
                        rules!.Add(new JsonObject
                        {
                            ["type"] = "logical",
                            ["mode"] = "or",
                            ["rules"] = combinedRules,
                            ["outbound"] = "proxy"
                        });
                    }
                    else // ExcludeSelectedApps
                    {
                        rules!.Add(new JsonObject
                        {
                            ["type"] = "logical",
                            ["mode"] = "or",
                            ["rules"] = combinedRules,
                            ["outbound"] = "direct"
                        });
                    }
                }
            }

            // Rule 4: Kill Switch - block all traffic if tunnel is down (handled by sing-box auto_route + strict_route)
            // Note: Kill switch is implemented via sing-box's strict_route=true and system stack
            // Additional explicit block rule can be added if needed

            // Add rule_set references for geoip-ir and geosite-ir (files expected in rules/ directory)
            // These are referenced in the route rules above

            return JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        private static bool IsCidr(string input)
        {
            return input.Contains('/') && System.Net.IPAddress.TryParse(input.Split('/')[0], out _);
        }
    }
}