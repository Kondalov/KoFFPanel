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
    private string GenerateTrustTunnelVpnToml(ServerInbound inbound)
    {
        return $@"
listen_address = ""0.0.0.0:{inbound.Port}""
ipv6_available = true
allow_private_network_connections = false
tls_handshake_timeout_secs = 10
client_listener_timeout_secs = 600
connection_establishment_timeout_secs = 30
tcp_connections_timeout_secs = 604800
udp_connections_timeout_secs = 300
credentials_file = ""credentials.toml""
rules_file = ""rules.toml""

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
direct = {{}}
";
    }

    private string GenerateTrustTunnelHostsToml(string sni, string certPath, string keyPath)
    {
        return $@"
[[main_hosts]]
hostname = ""{sni}""
cert_chain_path = ""{certPath}""
private_key_path = ""{keyPath}""

[[ping_hosts]]
hostname = ""ping.{sni}""
cert_chain_path = ""{certPath}""
private_key_path = ""{keyPath}""
";
    }

    private JsonObject BuildSingBoxInbound(ServerInbound inboundDb, JsonNode settings)
    {
        string protocol = inboundDb.Protocol.ToLower();
        if (protocol == "vless")
        {
            return new JsonObject
            {
                ["type"] = "vless",
                ["tag"] = inboundDb.Tag,
                ["listen"] = "0.0.0.0",
                ["listen_port"] = inboundDb.Port,
                ["users"] = new JsonArray(),
                ["tls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["server_name"] = settings?["sni"]?.ToString(),
                    ["reality"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["handshake"] = new JsonObject { ["server"] = settings?["sni"]?.ToString(), ["server_port"] = 443 },
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
                ["listen"] = "0.0.0.0",
                ["listen_port"] = inboundDb.Port,
                ["users"] = new JsonArray(),
                ["tls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["alpn"] = new JsonArray { "h3" },
                    ["certificate_path"] = settings?["certPath"]?.ToString(),
                    ["key_path"] = settings?["keyPath"]?.ToString()
                },
                ["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = settings?["obfsPassword"]?.ToString() }
            };
        }
        return null;
    }

    private JsonObject BuildXrayInbound(ServerInbound inboundDb, JsonNode settings)
    {
        if (inboundDb.Protocol.ToLower() == "vless")
        {
            return new JsonObject
            {
                ["protocol"] = "vless",
                ["listen"] = "0.0.0.0",
                ["port"] = inboundDb.Port,
                ["settings"] = new JsonObject { ["clients"] = new JsonArray(), ["decryption"] = "none" },
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
        return null;
    }
}
