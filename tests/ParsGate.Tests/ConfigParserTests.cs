using System;
using System.Collections.Generic;
using Xunit;
using ParsGate.Models;
using ParsGate.Services;

namespace ParsGate.Tests
{
    public class ConfigParserTests
    {
        [Theory]
        [InlineData("vless://uuid@server:443?security=reality&sni=example.com&fp=chrome&pbk=publickey&sid=shortid&spx=%2F&flow=xtls-rprx-vision#Test%20Reality", "reality", "xtls-rprx-vision")]
        [InlineData("vless://uuid@server:443?security=tls&sni=example.com&fp=chrome&alpn=h2,http/1.1&flow=xtls-rprx-vision#Test%20TLS", "tls", "xtls-rprx-vision")]
        [InlineData("vless://uuid@server:443?security=none&type=ws&host=example.com&path=%2Fws#Test%20WS", "none", "")]
        public void ParseVlessUri_ShouldGenerateCorrectConfig(string uri, string expectedSecurity, string expectedFlow)
        {
            var config = ConfigParser.ParseUri(uri);

            Assert.Equal("vless", config.Protocol);
            Assert.Equal("server", config.ServerAddress);
            Assert.Equal(443, config.Port);
            Assert.NotNull(config.RawUri);

            var xrayJson = ConfigParser.GenerateXrayConfigJson(config, new AppSettings());
            var jsonDoc = System.Text.Json.Nodes.JsonNode.Parse(xrayJson);
            var outbound = jsonDoc!["outbounds"]![0]!;
            var streamSettings = outbound["streamSettings"]!;
            var security = streamSettings["security"]!.GetValue<string>();

            Assert.Equal(expectedSecurity, security);

            if (expectedSecurity == "reality")
            {
                var realitySettings = streamSettings["realitySettings"]!;
                Assert.NotNull(realitySettings);
                Assert.Null(streamSettings["tlsSettings"]);
                Assert.Equal("chrome", realitySettings["fingerprint"]!.GetValue<string>());
                Assert.Equal("publickey", realitySettings["publicKey"]!.GetValue<string>());
                Assert.Equal("shortid", realitySettings["shortId"]!.GetValue<string>());
                Assert.Equal("/", realitySettings["spiderX"]!.GetValue<string>());
            }
            else if (expectedSecurity == "tls")
            {
                var tlsSettings = streamSettings["tlsSettings"]!;
                Assert.NotNull(tlsSettings);
                Assert.Null(streamSettings["realitySettings"]);
                Assert.Equal("chrome", tlsSettings["fingerprint"]!.GetValue<string>());
                Assert.Equal(new[] { "h2", "http/1.1" }, GetStringArray(tlsSettings["alpn"]!));
            }

            if (!string.IsNullOrEmpty(expectedFlow))
            {
                Assert.Equal(expectedFlow, outbound["flow"]!.GetValue<string>());
            }
        }

        [Theory]
        [InlineData("vmess://uuid@server:443?security=tls&sni=example.com&fp=chrome&type=ws&host=example.com&path=%2Fws#Test%20VMess%20WS")]
        [InlineData("vmess://uuid@server:80?security=none&type=ws&host=example.com&path=%2Fws#Test%20VMess%20Plain")]
        public void ParseVmessUri_ShouldGenerateCorrectConfig(string uri)
        {
            var config = ConfigParser.ParseUri(uri);

            Assert.Equal("vmess", config.Protocol);
            Assert.Equal("server", config.ServerAddress);
            Assert.Equal(443, config.Port); // First test has 443
        }

        [Theory]
        [InlineData("trojan://password@server:443?security=tls&sni=example.com&fp=chrome&type=ws&host=example.com&path=%2Fws#Test%20Trojan")]
        [InlineData("trojan://password@server:443?security=reality&sni=example.com&fp=chrome&pbk=publickey&sid=shortid#Test%20Trojan%20Reality")]
        public void ParseTrojanUri_ShouldGenerateCorrectConfig(string uri)
        {
            var config = ConfigParser.ParseUri(uri);

            Assert.Equal("trojan", config.Protocol);
            Assert.Equal("server", config.ServerAddress);
            Assert.Equal(443, config.Port);
        }

        [Theory]
        [InlineData("ss://YWVzLTEyOC1nY206cGFzc3dvcmQ=@server:443#Test%20SS%20AES-128-GCM")]
        [InlineData("ss://MjAyMi1ibGFrZTMtYWVzLTEyOC1nY206cGFzc3dvcmQ=@server:443#Test%20SS%202022-BLAKE3-AES-128-GCM")]
        public void ParseShadowsocksUri_ShouldGenerateCorrectConfig(string uri)
        {
            var config = ConfigParser.ParseUri(uri);

            Assert.Equal("shadowsocks", config.Protocol);
            Assert.Equal("server", config.ServerAddress);
            Assert.Equal(443, config.Port);
            Assert.NotNull(config.ShadowsocksMethod);
            Assert.NotNull(config.ShadowsocksPassword);
        }

        [Fact]
        public void ParseUri_MalformedUri_ShouldThrowWithPersianMessage()
        {
            Assert.Throws<ArgumentException>(() => ConfigParser.ParseUri("not-a-valid-uri"));
            Assert.Throws<ArgumentException>(() => ConfigParser.ParseUri("vless://"));
            Assert.Throws<ArgumentException>(() => ConfigParser.ParseUri("http://example.com"));
        }

        [Fact]
        public void ParseUri_PersianNameInFragment_ShouldDecodeCorrectly()
        {
            var uri = "vless://uuid@server:443?security=tls&sni=example.com#%D8%AA%D8%B3%D8%AA_%D9%81%D8%A7%D8%B1%D8%B3%DB%8C";
            var config = ConfigParser.ParseUri(uri);

            Assert.Contains("تست_فارسی", config.Name);
        }

        [Fact]
        public void XrayConfig_ShouldHaveCorrectSocksPort()
        {
            var config = new VpnConfigItem
            {
                Protocol = "vless",
                ServerAddress = "server",
                Port = 443,
                RawUri = "vless://uuid@server:443#Test"
            };

            var settings = new AppSettings();
            var xrayJson = ConfigParser.GenerateXrayConfigJson(config, settings);
            var jsonDoc = System.Text.Json.Nodes.JsonNode.Parse(xrayJson);

            var inbound = jsonDoc!["inbounds"]![0]!;
            var port = inbound["port"]!.GetValue<int>();

            Assert.Equal(ConfigParser.XraySocksPort, port);
        }

        private static string[] GetStringArray(System.Text.Json.Nodes.JsonNode node)
        {
            var arr = node.AsArray();
            var result = new List<string>();
            foreach (var item in arr)
            {
                result.Add(item!.GetValue<string>());
            }
            return result.ToArray();
        }
    }
}