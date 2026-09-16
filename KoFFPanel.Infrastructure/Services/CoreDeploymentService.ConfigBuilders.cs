using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;

namespace KoFFPanel.Infrastructure.Services;

public partial class CoreDeploymentService
{
    private JsonObject? BuildSingBoxInbound(ServerInbound inboundDb, JsonNode? settings)
    {
        string protocol = inboundDb.Protocol.ToLower();

        // FOOLPROOF ЗАЩИТА: Принудительная конвертация порта в int, чтобы избежать ошибки парсера
        int safePort = Convert.ToInt32(inboundDb.Port);

        if (protocol == "vless")
        {
            return new JsonObject
            {
                ["type"] = "vless",
                ["tag"] = inboundDb.Tag,
                ["listen"] = "::",
                ["listen_port"] = safePort,
                ["users"] = new JsonArray { new JsonObject { ["name"] = "init", ["uuid"] = "00000000-0000-0000-0000-000000000000", ["flow"] = "xtls-rprx-vision" } },
                ["tls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["server_name"] = settings?["sni"]?.ToString() ?? "google.com",
                    ["reality"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["handshake"] = new JsonObject { ["server"] = settings?["sni"]?.ToString() ?? "google.com", ["server_port"] = 443 },
                        ["private_key"] = settings?["privateKey"]?.ToString(),
                        ["short_id"] = new JsonArray { settings?["shortId"]?.ToString() }
                    }
                }
            };
        }
        else if (protocol == "hysteria2")
        {
            return new JsonObject
            {
                ["type"] = "hysteria2",
                ["tag"] = inboundDb.Tag,
                ["listen"] = "::",
                ["listen_port"] = safePort,
                ["users"] = new JsonArray { new JsonObject { ["name"] = "init", ["password"] = "init_pass" } },
                ["ignore_client_bandwidth"] = true,
                ["tls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["alpn"] = new JsonArray { "h3" },
                    ["certificate_path"] = settings?["certPath"]?.ToString(),
                    ["key_path"] = settings?["keyPath"]?.ToString()
                },
                ["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = settings?["obfsPassword"]?.ToString() ?? "obfs_pass" }
            };
        }
        else if (protocol == "tuic")
        {
            return new JsonObject
            {
                ["type"] = "tuic",
                ["tag"] = inboundDb.Tag,
                ["listen"] = "::",
                ["listen_port"] = safePort,
                ["users"] = new JsonArray { new JsonObject { ["name"] = "init", ["uuid"] = "00000000-0000-0000-0000-000000000000", ["password"] = "init_pass" } },
                ["congestion_control"] = settings?["congestionControl"]?.ToString() ?? "bbr",
                ["tls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["server_name"] = settings?["sni"]?.ToString() ?? "bing.com",
                    ["alpn"] = new JsonArray { "h3" },
                    ["certificate_path"] = settings?["certPath"]?.ToString(),
                    ["key_path"] = settings?["keyPath"]?.ToString()
                }
            };
        }
        else if (protocol == "trojan")
        {
            return new JsonObject
            {
                ["type"] = "trojan",
                ["tag"] = inboundDb.Tag,
                ["listen"] = "::",
                ["listen_port"] = safePort,
                ["users"] = new JsonArray { new JsonObject { ["name"] = "init", ["password"] = "init_pass" } },
                ["tls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["server_name"] = settings?["sni"]?.ToString() ?? "bing.com",
                    ["alpn"] = new JsonArray { "h2", "http/1.1" },
                    ["certificate_path"] = settings?["certPath"]?.ToString(),
                    ["key_path"] = settings?["keyPath"]?.ToString()
                }
            };
        }
        return null;
    }

    private JsonObject? BuildXrayInbound(ServerInbound inboundDb, JsonNode? settings)
    {
        string protocol = inboundDb.Protocol.ToLower();
        int safePort = Convert.ToInt32(inboundDb.Port);

        var sniffingObj = new JsonObject
        {
            ["enabled"] = true,
            ["destOverride"] = new JsonArray { "http", "tls", "quic" },
            ["routeOnly"] = true
        };

        if (protocol == "vless")
        {
            string sni = settings?["sni"]?.ToString() ?? "dl.google.com";
            return new JsonObject
            {
                ["protocol"] = "vless",
                ["listen"] = "0.0.0.0",
                ["port"] = safePort,
                ["settings"] = new JsonObject
                {
                    ["clients"] = new JsonArray { new JsonObject { ["email"] = "init", ["id"] = "00000000-0000-0000-0000-000000000000" } },
                    ["decryption"] = "none"
                },
                ["streamSettings"] = new JsonObject
                {
                    ["network"] = "tcp",
                    ["security"] = "reality",
                    ["realitySettings"] = new JsonObject
                    {
                        ["show"] = false,
                        ["dest"] = $"{sni}:443",
                        ["serverNames"] = new JsonArray { sni },
                        ["privateKey"] = settings?["privateKey"]?.ToString(),
                        ["shortIds"] = new JsonArray { settings?["shortId"]?.ToString() }
                    }
                },
                ["sniffing"] = sniffingObj
            };
        }
        else if (protocol == "trojan")
        {
            return new JsonObject
            {
                ["protocol"] = "trojan",
                ["listen"] = "0.0.0.0",
                ["port"] = safePort,
                ["settings"] = new JsonObject
                {
                    ["clients"] = new JsonArray { new JsonObject { ["email"] = "init", ["password"] = "init_pass" } }
                },
                ["streamSettings"] = new JsonObject
                {
                    ["network"] = "tcp",
                    ["security"] = "tls",
                    ["tlsSettings"] = new JsonObject
                    {
                        ["certificates"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["certificateFile"] = settings?["certPath"]?.ToString(),
                                ["keyFile"] = settings?["keyPath"]?.ToString()
                            }
                        }
                    }
                },
                ["sniffing"] = sniffingObj
            };
        }
        return null;
    }
}