using KoFFPanel.Infrastructure.Services;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace KoFFPanel.Tests;

public class SubscriptionConverterTests
{
    private readonly List<string> _sampleLinks = new()
    {
        "vless://e7b686d0-40e9-4e71-92be-6cf7b4478144@192.168.1.100:443?type=tcp&security=reality&pbk=AbCdEf123456&fp=chrome&sni=dl.google.com&sid=abcdef01&flow=xtls-rprx-vision#My-VLESS-Reality",
        "hy2://my-hy2-pass@192.168.1.100:8443?sni=bing.com&insecure=1&obfs=salamander&obfs-password=obfspass123&alpn=h3#My-Hysteria2",
        "tuic://e7b686d0-40e9-4e71-92be-6cf7b4478144:mypassword@192.168.1.100:8444?sni=bing.com&alpn=h3&congestion_control=bbr&allow_insecure=1#My-TUIC",
        "trojan://my-trojan-pass@192.168.1.100:2083?sni=bing.com&security=tls#My-Trojan"
    };

    [Fact]
    public void ParseUri_VlessReality_ShouldExtractFieldsCorrectly()
    {
        string vless = _sampleLinks[0];
        var parsed = SubscriptionConfigConverter.ParseUri(vless);

        Assert.NotNull(parsed);
        Assert.Equal("vless", parsed.Protocol);
        Assert.Equal("192.168.1.100", parsed.Server);
        Assert.Equal(443, parsed.Port);
        Assert.Equal("e7b686d0-40e9-4e71-92be-6cf7b4478144", parsed.UuidOrPassword);
        Assert.Equal("My-VLESS-Reality", parsed.Name);
        Assert.Equal("reality", parsed.Params["security"]);
        Assert.Equal("dl.google.com", parsed.Params["sni"]);
        Assert.Equal("AbCdEf123456", parsed.Params["pbk"]);
    }

    [Fact]
    public void ParseUri_Hysteria2_ShouldExtractFieldsCorrectly()
    {
        string hy2 = _sampleLinks[1];
        var parsed = SubscriptionConfigConverter.ParseUri(hy2);

        Assert.NotNull(parsed);
        Assert.Equal("hy2", parsed.Protocol);
        Assert.Equal("192.168.1.100", parsed.Server);
        Assert.Equal(8443, parsed.Port);
        Assert.Equal("my-hy2-pass", parsed.UuidOrPassword);
        Assert.Equal("My-Hysteria2", parsed.Name);
        Assert.Equal("salamander", parsed.Params["obfs"]);
        Assert.Equal("obfspass123", parsed.Params["obfs-password"]);
    }

    [Fact]
    public void ParseUri_Tuic_ShouldExtractFieldsCorrectly()
    {
        string tuic = _sampleLinks[2];
        var parsed = SubscriptionConfigConverter.ParseUri(tuic);

        Assert.NotNull(parsed);
        Assert.Equal("tuic", parsed.Protocol);
        Assert.Equal("192.168.1.100", parsed.Server);
        Assert.Equal(8444, parsed.Port);
        Assert.Equal("e7b686d0-40e9-4e71-92be-6cf7b4478144:mypassword", parsed.UuidOrPassword);
        Assert.Equal("My-TUIC", parsed.Name);
        Assert.Equal("bing.com", parsed.Params["sni"]);
        Assert.Equal("h3", parsed.Params["alpn"]);
    }

    [Fact]
    public void GenerateClashYaml_WithValidLinks_ShouldProduceValidClashConfiguration()
    {
        string yaml = SubscriptionConfigConverter.GenerateClashYaml(_sampleLinks);

        Assert.NotNull(yaml);
        Assert.Contains("proxies:", yaml);
        Assert.Contains("proxy-groups:", yaml);
        Assert.Contains("type: vless", yaml);
        Assert.Contains("type: hysteria2", yaml);
        Assert.Contains("type: tuic", yaml);
        Assert.Contains("type: trojan", yaml);
        Assert.Contains("🔄 AUTO - Самый быстрый", yaml);
        Assert.Contains("🛡️ РЕЗЕРВ - Failover", yaml);
        Assert.Contains("MATCH,🚀 PROXY", yaml);
    }

    [Fact]
    public void GenerateSingBoxJson_WithValidLinks_ShouldProduceValidJson()
    {
        string json = SubscriptionConfigConverter.GenerateSingBoxJson(_sampleLinks);

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("outbounds", out var outbounds));
        Assert.True(outbounds.GetArrayLength() >= 6); // vless, hy2, tuic, trojan, auto, select, direct, block
    }

    [Fact]
    public void GenerateClashYaml_EmptyLinks_ShouldNotThrowAndReturnDirectRules()
    {
        string yaml = SubscriptionConfigConverter.GenerateClashYaml(new List<string>());

        Assert.NotNull(yaml);
        Assert.Contains("MATCH,DIRECT", yaml);
    }
}
