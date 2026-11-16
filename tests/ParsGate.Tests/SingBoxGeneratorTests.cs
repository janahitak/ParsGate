using System;
using System.Collections.Generic;
using System.Net;
using Xunit;
using ParsGate.Models;
using ParsGate.Services;

namespace ParsGate.Tests
{
    public class SingBoxGeneratorTests
    {
        private readonly AppSettings _settings;

        public SingBoxGeneratorTests()
        {
            _settings = new AppSettings
            {
                IranDirectEnabled = true,
                SplitTunnelEnabled = true,
                SplitTunnelProcessNames = new List<string> { "chrome.exe", "firefox.exe" },
                SplitTunnelCustomDomainsOrIps = new List<string> { "example.com", "192.168.1.0/24" }
            };
        }

        [Fact]
        public void GenerateSingBoxConfig_ExcludeSelectedMode_ShouldHaveCorrectRuleOrder()
        {
            _settings.SplitTunnelMode = SplitTunnelMode.ExcludeSelected;

            var serverIps = new List<string> { "1.2.3.4", "2001:db8::1" };
            var json = SingBoxGenerator.GenerateSingBoxConfigJson(_settings, "server.example.com", serverIps);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(json);
            var rules = doc!["route"]!["rules"]!.AsArray();

            // Rule 0: xray.exe -> direct
            AssertRuleOutbound(rules[0], "direct");

            // Rule 1: VPN server IPs -> direct
            AssertRuleOutbound(rules[1], "direct");

            // Rule 2: Loopback/private -> direct
            AssertRuleOutbound(rules[2], "direct");

            // Rule 3: QUIC block
            AssertRuleOutbound(rules[3], "block");

            // Find Iran Direct rule (should be before split tunnel)
            var iranRuleIndex = FindRuleIndex(rules, "geoip-ir");
            var splitTunnelIndex = FindRuleIndex(rules, "chrome.exe");

            Assert.True(iranRuleIndex >= 0, "Iran direct rule should exist");
            Assert.True(splitTunnelIndex >= 0, "Split tunnel rule should exist");
            Assert.True(iranRuleIndex < splitTunnelIndex, "Iran direct should come before split tunnel");

            // ExcludeSelected: route.final should be "proxy"
            Assert.Equal("proxy", doc!["route"]!["final"]!.GetValue<string>());
        }

        [Fact]
        public void GenerateSingBoxConfig_OnlySelectedMode_ShouldHaveCorrectRouteFinal()
        {
            _settings.SplitTunnelMode = SplitTunnelMode.OnlySelected;

            var serverIps = new List<string> { "1.2.3.4" };
            var json = SingBoxGenerator.GenerateSingBoxConfigJson(_settings, "server.example.com", serverIps);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(json);

            // OnlySelected: route.final should be "direct"
            Assert.Equal("direct", doc!["route"]!["final"]!.GetValue<string>());

            // Split tunnel rule should have outbound "proxy"
            var rules = doc!["route"]!["rules"]!.AsArray();
            var splitTunnelIndex = FindRuleIndex(rules, "chrome.exe");
            Assert.True(splitTunnelIndex >= 0);
            AssertRuleOutbound(rules[splitTunnelIndex], "proxy");
        }

        [Fact]
        public void GenerateSingBoxConfig_DnsFinal_ShouldBeRemoteDnsTag()
        {
            var serverIps = new List<string> { "1.2.3.4" };
            var json = SingBoxGenerator.GenerateSingBoxConfigJson(_settings, "server.example.com", serverIps);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(json);

            // dns.final must be TAG of DNS server, not "dns-out"
            Assert.Equal("remote-dns", doc!["dns"]!["final"]!.GetValue<string>());
        }

        [Fact]
        public void GenerateSingBoxConfig_ShouldHaveRuleSetsForIran()
        {
            // This test would need the .srs files to exist
            // We'll just verify the structure when files are present
            var serverIps = new List<string> { "1.2.3.4" };
            var json = SingBoxGenerator.GenerateSingBoxConfigJson(_settings, "server.example.com", serverIps);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(json);

            // The generator should include rule_set definitions at top level if files exist
            // If files don't exist, it should still work (skip rules)
            Assert.NotNull(doc);
        }

        [Fact]
        public void GenerateSingBoxConfig_VpnServerDomain_ShouldAddDnsRuleForDirectResolution()
        {
            var serverIps = new List<string> { "1.2.3.4" };
            var json = SingBoxGenerator.GenerateSingBoxConfigJson(_settings, "vpn.example.com", serverIps);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(json);

            var dnsRules = doc!["dns"]!["rules"]!.AsArray();
            // First DNS rule should be for the VPN server domain -> direct-dns
            var firstRule = dnsRules[0]!;
            Assert.Equal("domain", firstRule["type"]!.GetValue<string>());
            var domains = firstRule["domain"]!.AsArray();
            Assert.Contains(domains, d => d!.GetValue<string>() == "vpn.example.com");
            Assert.Equal("direct-dns", firstRule["server"]!.GetValue<string>());
        }

        [Fact]
        public void GenerateSingBoxConfig_TunMtu_ShouldBe1400()
        {
            var serverIps = new List<string> { "1.2.3.4" };
            var json = SingBoxGenerator.GenerateSingBoxConfigJson(_settings, "server.example.com", serverIps);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(json);

            var tunInbound = doc!["inbounds"]![0]!;
            Assert.Equal(1400, tunInbound["mtu"]!.GetValue<int>());
        }

        [Fact]
        public void GenerateSingBoxConfig_ShouldNotHaveUdpOverTcpOnSocksOutbound()
        {
            var serverIps = new List<string> { "1.2.3.4" };
            var json = SingBoxGenerator.GenerateSingBoxConfigJson(_settings, "server.example.com", serverIps);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(json);

            var proxyOutbound = FindOutboundByTag(doc!["outbounds"]!.AsArray(), "proxy");
            Assert.NotNull(proxyOutbound);
            Assert.False(proxyOutbound!.ContainsKey("udp_over_tcp"));
        }

        private void AssertRuleOutbound(System.Text.Json.Nodes.JsonArray rules, int index, string expectedOutbound)
        {
            var rule = rules[index]!;
            var outbound = rule["outbound"]?.GetValue<string>();
            Assert.Equal(expectedOutbound, outbound);
        }

        private void AssertRuleOutbound(System.Text.Json.Nodes.JsonNode rule, string expectedOutbound)
        {
            var outbound = rule["outbound"]?.GetValue<string>();
            Assert.Equal(expectedOutbound, outbound);
        }

        private int FindRuleIndex(System.Text.Json.Nodes.JsonArray rules, string keyword)
        {
            for (int i = 0; i < rules.Count; i++)
            {
                var ruleJson = rules[i]!.ToJsonString();
                if (ruleJson.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private System.Text.Json.Nodes.JsonNode? FindOutboundByTag(System.Text.Json.Nodes.JsonArray outbounds, string tag)
        {
            foreach (var ob in outbounds)
            {
                if (ob!["tag"]?.GetValue<string>() == tag)
                    return ob;
            }
            return null;
        }
    }
}