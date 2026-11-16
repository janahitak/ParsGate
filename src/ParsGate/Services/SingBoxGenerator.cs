using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ParsGate.Models;
using ParsGate.Services;

namespace ParsGate.Services
{
    public static class SingBoxGenerator
    {
        // Xray SOCKS port must match ConfigParser.XraySocksPort
        public const int XraySocksPort = ConfigParser.XraySocksPort;

        public static string GenerateSingBoxConfigJson(AppSettings settings, string serverAddress, IEnumerable<string> resolvedServerIps, int xraySocksPort = XraySocksPort)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrWhiteSpace(serverAddress)) throw new ArgumentException("Server address cannot be empty", nameof(serverAddress));

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
                        ["address"] = new JsonArray("172.19.0.1/30"),
                        ["auto_route"] = true,
                        ["strict_route"] = true,
                        ["stack"] = "mixed",
                        ["sniff"] = true,
                        ["sniff_override_destination"] = true,
                        ["mtu"] = 1400
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
                    ["final"] = "remote-dns", // TAG of a DNS server, not "dns-out"
                    ["strategy"] = "ipv4_only"
                }
            };

            // Outbounds
            var outbounds = config["outbounds"] as JsonArray;

            // Proxy outbound -> Xray SOCKS5 (no udp_over_tcp; Xray inbound has udp:true)
            outbounds!.Add(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = "proxy",
                ["server"] = "127.0.0.1",
                ["server_port"] = xraySocksPort,
                ["version"] = "5"
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

            // Validate and set CustomRemoteDns
            var remoteDns = ValidateDnsAddress(settings.CustomRemoteDns, "https://1.1.1.1/dns-query");
            // Validate and set CustomDirectDns
            var directDns = ValidateDnsAddress(settings.CustomDirectDns, "1.1.1.1");

            // Remote DNS (via proxy) - for international domains
            dnsServers!.Add(new JsonObject
            {
                ["tag"] = "remote-dns",
                ["address"] = remoteDns,
                ["detour"] = "proxy"
            });

            // Direct DNS (via direct) - for Iranian domains
            dnsServers.Add(new JsonObject
            {
                ["tag"] = "direct-dns",
                ["address"] = directDns,
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
                    }
                    // geosite-ir is handled via rule_set in route rules, not as domain_suffix here
                },
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

            // === CRITICAL: FIRST rules to prevent routing loop ===
            // Rule 0a: xray.exe process -> direct (prevents xray's own traffic from entering TUN)
            rules!.Add(new JsonObject
            {
                ["type"] = "logical",
                ["mode"] = "or",
                ["rules"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "process",
                        ["process"] = new JsonArray("xray.exe")
                    }
                },
                ["outbound"] = "direct"
            });

            // Rule 0b: Resolved VPN server IPs -> direct (prevents loop to VPN server)
            var serverIpRules = new JsonArray();
            foreach (var ip in resolvedServerIps)
            {
                if (IPAddress.TryParse(ip, out var addr))
                {
                    serverIpRules.Add(new JsonObject
                    {
                        ["type"] = "ip_cidr",
                        ["ip_cidr"] = new JsonArray($"{ip}/32")
                    });
                }
            }
            // Also add the original server address (domain) for DNS resolution via direct
            // This is handled by a DNS rule below, but we also add it here as a fallback
            if (!serverAddress.All(c => char.IsDigit(c) || c == '.' || c == ':'))
            {
                // It's a domain, add DNS rule for it to resolve via direct DNS
                dnsRules!.Insert(0, new JsonObject
                {
                    ["type"] = "domain",
                    ["domain"] = new JsonArray(serverAddress),
                    ["server"] = "direct-dns"
                });
            }

            if (serverIpRules.Count > 0)
            {
                rules.Add(new JsonObject
                {
                    ["type"] = "logical",
                    ["mode"] = "or",
                    ["rules"] = serverIpRules,
                    ["outbound"] = "direct"
                });
            }

            // Rule 0c: Loopback and private ranges -> direct
            rules.Add(new JsonObject
            {
                ["type"] = "logical",
                ["mode"] = "or",
                ["rules"] = new JsonArray
                {
                    new JsonObject { ["type"] = "ip_cidr", ["ip_cidr"] = new JsonArray("127.0.0.0/8", "::1/128") },
                    new JsonObject { ["type"] = "ip_cidr", ["ip_cidr"] = new JsonArray("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7") },
                    new JsonObject { ["type"] = "ip_cidr", ["ip_cidr"] = new JsonArray("169.254.0.0/16", "fe80::/10") }
                },
                ["outbound"] = "direct"
            });

            // Rule 1: Block QUIC (UDP 443) to force Chrome/Edge to fall back to TCP/HTTP2 over Xray SOCKS5
            rules!.Add(new JsonObject
            {
                ["type"] = "logical",
                ["mode"] = "or",
                ["rules"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "protocol",
                        ["protocol"] = new JsonArray("quic")
                    },
                    new JsonObject
                    {
                        ["type"] = "port",
                        ["port"] = 443,
                        ["network"] = "udp"
                    }
                },
                ["outbound"] = "block"
            });

            // Rule 2: Iran Direct (rule_set for geoip-ir, geosite-ir) -> direct
            if (settings.IranDirectEnabled)
            {
                var iranRules = new JsonArray();

                // Add rule_set references if files exist
                var rulesBaseDir = GetRulesBaseDirectory();
                var geoipSrs = Path.Combine(rulesBaseDir, "geoip-ir.srs");
                var geositeSrs = Path.Combine(rulesBaseDir, "geosite-ir.srs");

                bool hasGeoip = File.Exists(geoipSrs);
                bool hasGeosite = File.Exists(geositeSrs);

                if (hasGeoip)
                {
                    iranRules.Add(new JsonObject
                    {
                        ["type"] = "rule_set",
                        ["rule_set"] = new JsonArray("geoip-ir"),
                        ["action"] = "direct" // sing-box 1.12+ uses action
                    });
                }
                else
                {
                    LogWarning("geoip-ir.srs not found at " + geoipSrs + "; skipping geoip rule");
                }

                if (hasGeosite)
                {
                    iranRules.Add(new JsonObject
                    {
                        ["type"] = "rule_set",
                        ["rule_set"] = new JsonArray("geosite-ir"),
                        ["action"] = "direct"
                    });
                }
                else
                {
                    LogWarning("geosite-ir.srs not found at " + geositeSrs + "; skipping geosite rule");
                }

                // Always add .ir domain suffix rule
                iranRules.Add(new JsonObject
                {
                    ["type"] = "domain_suffix",
                    ["domain_suffix"] = new JsonArray(".ir"),
                    ["action"] = "direct"
                });

                if (iranRules.Count > 0)
                {
                    rules!.Add(new JsonObject
                    {
                        ["type"] = "logical",
                        ["mode"] = "or",
                        ["rules"] = iranRules,
                        ["outbound"] = "direct"
                    });
                }
            }

            // Rule 3: Split Tunneling
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
                    var mode = settings.SplitTunnelMode;
                    // Migrate legacy values
                    if (mode == "ExcludeApps") mode = "ExcludeSelected";
                    else if (mode == "ExcludeSelectedApps") mode = "ExcludeSelected";
                    else if (mode == "OnlySelectedApps") mode = "OnlySelected";

                    if (mode == "OnlySelected")
                    {
                        // Only selected apps go through proxy, rest direct
                        rules!.Add(new JsonObject
                        {
                            ["type"] = "logical",
                            ["mode"] = "or",
                            ["rules"] = processRules,
                            ["outbound"] = "proxy"
                        });
                        // Important: for OnlySelected, route.final should be "direct"
                        route["final"] = "direct";
                    }
                    else // ExcludeSelected (default)
                    {
                        // Selected apps go direct, rest through proxy
                        rules!.Add(new JsonObject
                        {
                            ["type"] = "logical",
                            ["mode"] = "or",
                            ["rules"] = processRules,
                            ["outbound"] = "direct"
                        });
                        // route.final stays "proxy"
                    }
                }
            }

            // Rule 4: Custom domains/IPs for split tunneling
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

                    var mode = settings.SplitTunnelMode;
                    if (mode == "ExcludeApps") mode = "ExcludeSelected";
                    else if (mode == "ExcludeSelectedApps") mode = "ExcludeSelected";
                    else if (mode == "OnlySelectedApps") mode = "OnlySelected";

                    if (mode == "OnlySelected")
                    {
                        rules!.Add(new JsonObject
                        {
                            ["type"] = "logical",
                            ["mode"] = "or",
                            ["rules"] = combinedRules,
                            ["outbound"] = "proxy"
                        });
                    }
                    else // ExcludeSelected
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

            // Add rule_set definitions at the top level (for geoip-ir and geosite-ir)
            // These are referenced in the route rules above
            var ruleSets = new JsonArray();
            var rulesBaseDir = GetRulesBaseDirectory();
            var geoipSrsPath = Path.Combine(rulesBaseDir, "geoip-ir.srs");
            var geositeSrsPath = Path.Combine(rulesBaseDir, "geosite-ir.srs");

            if (File.Exists(geoipSrsPath))
            {
                ruleSets.Add(new JsonObject
                {
                    ["tag"] = "geoip-ir",
                    ["type"] = "local",
                    ["format"] = "binary",
                    ["path"] = geoipSrsPath
                });
            }
            if (File.Exists(geositeSrsPath))
            {
                ruleSets.Add(new JsonObject
                {
                    ["tag"] = "geosite-ir",
                    ["type"] = "local",
                    ["format"] = "binary",
                    ["path"] = geositeSrsPath
                });
            }

            if (ruleSets.Count > 0)
            {
                config["rule_set"] = ruleSets;
            }

            return JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }

        private static string GetRulesBaseDirectory()
        {
            // At runtime, rules are in %ProgramData%\ParsGate\rules\
            // At build time (CI), they're in src\ParsGate\publish\rules\
            // We check both, preferring ProgramData for runtime
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var runtimeDir = Path.Combine(programData, "ParsGate", "rules");
            if (Directory.Exists(runtimeDir)) return runtimeDir;

            // Fallback to base directory (for development/testing)
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var localDir = Path.Combine(baseDir, "rules");
            if (Directory.Exists(localDir)) return localDir;

            // Fallback to publish directory structure
            var publishDir = Path.Combine(baseDir, "..", "publish", "rules");
            publishDir = Path.GetFullPath(publishDir);
            if (Directory.Exists(publishDir)) return publishDir;

            return runtimeDir; // Return the expected runtime path even if it doesn't exist yet
        }

        private static string ValidateDnsAddress(string input, string fallback)
        {
            if (string.IsNullOrWhiteSpace(input)) return fallback;

            var trimmed = input.Trim();

            // Check if it's a valid IP address
            if (IPAddress.TryParse(trimmed, out _))
                return trimmed;

            // Check if it's a valid DoH/DoT URL
            if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("tls://", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
                    return trimmed;
            }

            // Check if it's a valid hostname (for plain DNS)
            if (Uri.CheckHostName(trimmed) != UriHostNameType.Unknown)
                return trimmed;

            // Invalid, use fallback
            LogWarning($"Invalid DNS address '{input}', using fallback '{fallback}'");
            return fallback;
        }

        private static bool IsCidr(string input)
        {
            return input.Contains('/') && IPAddress.TryParse(input.Split('/')[0], out _);
        }

        private static void LogWarning(string message)
        {
            // In a real app, this would use ILogger or write to log file
            // For now, write to Console which gets captured by CoreManager log files
            Console.WriteLine($"[WARN] {message}");
        }
    }
}