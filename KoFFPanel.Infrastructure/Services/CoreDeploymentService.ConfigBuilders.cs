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
    public static string GenerateTrustTunnelVpnToml(ServerInbound inbound)
    {
        string toml = $@"listen_address = ""0.0.0.0:{inbound.Port}""
ipv6_available = true
allow_private_network_connections = false
tls_handshake_timeout_secs = 10
client_listener_timeout_secs = 600
connection_establishment_timeout_secs = 30
tcp_connections_timeout_secs = 604800
udp_connections_timeout_secs = 300
credentials_file = ""credentials.toml""
rules_file = ""rules.toml""

[listen_protocols]

[listen_protocols.http2]
initial_connection_window_size = 8388608
initial_stream_window_size = 131072
max_concurrent_streams = 1000

[listen_protocols.quic]
recv_udp_payload_size = 1350
send_udp_payload_size = 1350
initial_max_data = 104857600
initial_max_stream_data_bidi_local = 1048576
initial_max_stream_data_bidi_remote = 1048576
initial_max_streams_bidi = 4096
enable_early_data = true

[forward_protocol]
direct = {{}}";

        return toml.Replace("\r", "");
    }

    public static string GenerateTrustTunnelHostsToml(string sni, string certPath, string keyPath)
    {
        string toml = $@"[[main_hosts]]
hostname = ""{sni}""
cert_chain_path = ""certs/cert.pem""
private_key_path = ""certs/key.pem""";

        return toml.Replace("\r", "");
    }

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
                    ["certificate_path"] = settings?["certPath"]?.ToString(),
                    ["key_path"] = settings?["keyPath"]?.ToString()
                }
            };
        }
        else if (protocol == "shadowsocks")
        {
            return new JsonObject
            {
                ["type"] = "shadowsocks",
                ["tag"] = inboundDb.Tag,
                ["listen"] = "::",
                ["listen_port"] = safePort,
                ["method"] = settings?["method"]?.ToString() ?? "aes-256-gcm",
                ["users"] = new JsonArray { new JsonObject { ["name"] = "init", ["password"] = "init_pass" } }
            };
        }
        return null;
    }

    private JsonObject? BuildXrayInbound(ServerInbound inboundDb, JsonNode? settings)
    {
        string protocol = inboundDb.Protocol.ToLower();
        int safePort = Convert.ToInt32(inboundDb.Port);

        if (protocol == "vless")
        {
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
                        ["dest"] = $"{settings?["sni"]}:443",
                        ["serverNames"] = new JsonArray { settings?["sni"]?.ToString() },
                        ["privateKey"] = settings?["privateKey"]?.ToString(),
                        ["shortIds"] = new JsonArray { settings?["shortId"]?.ToString() }
                    }
                }
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
                }
            };
        }
        else if (protocol == "shadowsocks")
        {
            return new JsonObject
            {
                ["protocol"] = "shadowsocks",
                ["listen"] = "0.0.0.0",
                ["port"] = safePort,
                ["settings"] = new JsonObject
                {
                    ["method"] = settings?["method"]?.ToString() ?? "aes-256-gcm",
                    ["clients"] = new JsonArray { new JsonObject { ["email"] = "init", ["password"] = "init_pass" } },
                    ["network"] = "tcp,udp"
                }
            };
        }
        return null;
    }
}