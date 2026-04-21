using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public partial class CoreDeploymentService : ICoreDeploymentService
{
    private readonly IAppLogger _logger;
    private readonly IProfileRepository _profileRepository;

    public CoreDeploymentService(IAppLogger logger, IProfileRepository profileRepository)
    {
        _logger = logger;
        _profileRepository = profileRepository;
    }

    public async Task<(bool IsSuccess, string Message)> RunPreFlightChecksAsync(ISshService ssh)
    {
        if (!ssh.IsConnected) return (false, "Нет SSH подключения");
        string checkScript = @"
if [ ""$EUID"" -ne 0 ]; then echo 'ERROR|Нужны права root (sudo).'; exit 0; fi
if ! command -v systemctl >/dev/null 2>&1; then echo 'ERROR|Сервер не поддерживает systemd.'; exit 0; fi
if ! command -v curl >/dev/null 2>&1; then echo 'ERROR|Пакет curl не установлен.'; exit 0; fi
echo 'READY|Сервер готов к установке.'
";
        string result = (await ssh.ExecuteCommandAsync(checkScript)).Trim();
        if (result.StartsWith("ERROR|")) return (false, result.Split('|')[1]);
        return (true, "Сервер готов.");
    }

    public async Task<string> GetInstalledXrayVersionAsync(ISshService ssh) => ssh.IsConnected ? (await ssh.ExecuteCommandAsync("xray version | head -n 1 | awk '{print $2}'")).Trim() : "Отключен";
    public async Task<string> GetInstalledSingBoxVersionAsync(ISshService ssh) => ssh.IsConnected ? (await ssh.ExecuteCommandAsync("sing-box version | grep 'version' | awk '{print $3}'")).Trim() : "Отключен";
    public async Task<string> GetInstalledTrustTunnelVersionAsync(ISshService ssh) => ssh.IsConnected ? (await ssh.ExecuteCommandAsync("trusttunnel --version 2>/dev/null | awk '{print $2}'")).Trim() : "Отключен";

    public async Task<(bool IsSuccess, string Log)> InstallXrayAsync(ISshService ssh, string targetVersion = "latest")
    {
        string cmd = targetVersion == "latest" ? "bash -c \"$(curl -L https://github.com/XTLS/Xray-install/raw/main/install-release.sh)\" @ install" : $"bash -c \"$(curl -L https://github.com/XTLS/Xray-install/raw/main/install-release.sh)\" @ install --version {targetVersion}";
        string log = await ssh.ExecuteCommandAsync(cmd, TimeSpan.FromMinutes(3));
        return (log.Contains("installed") || log.Contains("success"), log);
    }

    public async Task<(bool IsSuccess, string Log)> InstallSingBoxAsync(ISshService ssh, string targetVersion = "latest")
    {
        string installCmd = @"
LATEST_VERSION=$(curl -sL https://api.github.com/repos/SagerNet/sing-box/releases/latest | grep '""tag_name"":' | sed -E 's/.*""v([^""]+)"".*/\1/')
if [ -z ""$LATEST_VERSION"" ] || [[ ""$LATEST_VERSION"" == *""http""* ]]; then LATEST_VERSION=""1.8.11""; fi
bash <(curl -fsSL https://sing-box.app/install.sh) install ""$LATEST_VERSION""
if command -v sing-box >/dev/null 2>&1; then echo 'SUCCESS_INSTALLED'; else echo 'FAIL_INSTALL'; fi
".Replace("\r", "");
        string log = await ssh.ExecuteCommandAsync(installCmd, TimeSpan.FromMinutes(3));
        return (log.Contains("SUCCESS_INSTALLED") || log.Contains("installed") || log.Contains("already"), log);
    }

    public async Task<(bool IsSuccess, string Log)> InstallTrustTunnelAsync(ISshService ssh, string targetVersion = "latest")
    {
        string installCmd = @"
wget -qO /tmp/trusttunnel.tar.gz https://github.com/TrustTunnel/TrustTunnel/releases/latest/download/trusttunnel-linux-amd64.tar.gz || \
wget -qO /usr/local/bin/trusttunnel https://github.com/TrustTunnel/TrustTunnel/releases/latest/download/trusttunnel-linux-amd64
if [ -f /tmp/trusttunnel.tar.gz ]; then tar -xzf /tmp/trusttunnel.tar.gz -C /tmp/; mv /tmp/trusttunnel /usr/local/bin/trusttunnel; rm /tmp/trusttunnel.tar.gz; fi
chmod +x /usr/local/bin/trusttunnel
if command -v trusttunnel >/dev/null 2>&1; then echo 'success'; else echo 'failed'; fi
".Replace("\r", "");
        string log = await ssh.ExecuteCommandAsync(installCmd, TimeSpan.FromMinutes(3));
        return (log.Contains("success"), log);
    }

    public async Task<(bool IsSuccess, string Log)> DeployFullStackAsync(ISshService ssh, VpnProfile profile, string coreType, List<(IProtocolBuilder Builder, int Port)> protocols)
    {
        try
        {
            _logger.Log("DEPLOY-TRACE", "[1/7] Начало развертывания...");
            await ssh.ExecuteCommandAsync("systemctl stop sing-box xray trusttunnel 2>/dev/null; systemctl disable sing-box xray trusttunnel 2>/dev/null; killall -9 sing-box xray trusttunnel 2>/dev/null; rm -rf /usr/local/etc/xray /etc/sing-box /etc/trusttunnel; mkdir -p /etc/sing-box /usr/local/etc/xray /etc/trusttunnel");

            var installRes = coreType.ToLower() == "sing-box" ? await InstallSingBoxAsync(ssh) : (coreType.ToLower() == "trusttunnel" ? await InstallTrustTunnelAsync(ssh) : await InstallXrayAsync(ssh));
            if (!installRes.IsSuccess) return (false, $"Ошибка установки ядра: {installRes.Log}");

            var existingInbounds = profile.Inbounds.ToList(); profile.Inbounds.Clear();
            foreach (var p in protocols)
            {
                await ssh.ExecuteCommandAsync($@"if command -v ufw >/dev/null 2>&1; then ufw allow {p.Port}/tcp && ufw allow {p.Port}/udp || true; fi; iptables -I INPUT 1 -p tcp --dport {p.Port} -j ACCEPT || true; iptables-save > /etc/iptables/rules.v4 2>/dev/null || true");
                var existingDb = existingInbounds.FirstOrDefault(i => i.Protocol.ToLower() == p.Builder.ProtocolType.ToLower());
                ServerInbound inboundDb;
                if (existingDb != null && existingDb.Port == p.Port) { await SmartRestoreCertsAsync(ssh, existingDb); inboundDb = existingDb; }
                else inboundDb = await p.Builder.GenerateNewInboundAsync(ssh, p.Port);
                profile.Inbounds.Add(inboundDb);
            }

            if (coreType.ToLower() == "trusttunnel") await DeployTrustTunnelConfigAsync(ssh, profile);
            else await DeployJsonCoreConfigAsync(ssh, profile, coreType.ToLower());

            profile.CoreType = coreType.ToLower(); _profileRepository.UpdateProfile(profile);
            await ssh.ExecuteCommandAsync($"systemctl enable {coreType.ToLower()} --now && systemctl restart {coreType.ToLower()}");
            return (true, $"Успешно развернуто! Ядро: {coreType.ToUpper()}.");
        }
        catch (Exception ex) { _logger.Log("DEPLOY-ERROR", $"[FAIL] {ex.Message}"); return (false, ex.Message); }
    }

    private async Task SmartRestoreCertsAsync(ISshService ssh, ServerInbound existingDb)
    {
        try {
            var settings = JsonNode.Parse(existingDb.SettingsJson);
            string cp = settings?["certPath"]?.ToString(); string kp = settings?["keyPath"]?.ToString();
            if (!string.IsNullOrWhiteSpace(cp) && !string.IsNullOrWhiteSpace(kp))
                await ssh.ExecuteCommandAsync($@"if [ ! -f ""{cp}"" ] || [ ! -f ""{kp}"" ]; then mkdir -p $(dirname ""{cp}""); openssl ecparam -genkey -name prime256v1 -out ""{kp}""; openssl req -new -x509 -days 36500 -key ""{kp}"" -out ""{cp}"" -subj ""/CN=bing.com""; fi");
        } catch { }
    }

    private async Task DeployTrustTunnelConfigAsync(ISshService ssh, VpnProfile profile)
    {
        var inbound = profile.Inbounds.FirstOrDefault(i => i.Protocol.ToLower() == "trusttunnel");
        if (inbound == null) return;
        var settings = JsonNode.Parse(inbound.SettingsJson);
        string sni = settings?["sni"]?.ToString() ?? "vpn.local";
        string cp = settings?["certPath"]?.ToString() ?? ""; string kp = settings?["keyPath"]?.ToString() ?? "";
        await ssh.ExecuteCommandAsync($"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(GenerateTrustTunnelVpnToml(inbound)))}' | base64 -d > /etc/trusttunnel/vpn.toml");
        await ssh.ExecuteCommandAsync($"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(GenerateTrustTunnelHostsToml(sni, cp, kp)))}' | base64 -d > /etc/trusttunnel/hosts.toml");
        await ssh.ExecuteCommandAsync("echo '[[rule]]\naction = \"allow\"\n' > /etc/trusttunnel/rules.toml; touch /etc/trusttunnel/credentials.toml");
        await ssh.ExecuteCommandAsync(@"cat > /etc/systemd/system/trusttunnel.service <<EOF
[Unit]
Description=TrustTunnel VPN Server
After=network.target
[Service]
Type=simple
User=root
WorkingDirectory=/etc/trusttunnel
ExecStart=/usr/local/bin/trusttunnel --config /etc/trusttunnel/vpn.toml
Restart=on-failure
RestartSec=5
[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload".Replace("\r", ""));
    }

    private async Task DeployJsonCoreConfigAsync(ISshService ssh, VpnProfile profile, string core)
    {
        var inboundsArray = new JsonArray();
        foreach (var inbound in profile.Inbounds) {
            var settings = JsonNode.Parse(inbound.SettingsJson);
            var node = core == "sing-box" ? BuildSingBoxInbound(inbound, settings) : BuildXrayInbound(inbound, settings);
            if (node != null) inboundsArray.Add(node);
        }
        var baseConfig = new JsonObject();
        if (core == "sing-box") {
            baseConfig["log"] = new JsonObject { ["level"] = "info" }; baseConfig["inbounds"] = inboundsArray;
            baseConfig["outbounds"] = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = "direct" }, new JsonObject { ["type"] = "block", ["tag"] = "block" } };
            baseConfig["route"] = new JsonObject { ["rules"] = new JsonArray() };
        } else {
            baseConfig["log"] = new JsonObject { ["loglevel"] = "warning" }; baseConfig["inbounds"] = inboundsArray;
            baseConfig["outbounds"] = new JsonArray { new JsonObject { ["protocol"] = "freedom", ["tag"] = "direct" }, new JsonObject { ["protocol"] = "blackhole", ["tag"] = "block" } };
            baseConfig["routing"] = new JsonObject { ["rules"] = new JsonArray() };
        }
        string path = core == "sing-box" ? "/etc/sing-box/config.json" : "/usr/local/etc/xray/config.json";
        await ssh.ExecuteCommandAsync($"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(baseConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true })))}' | base64 -d > {path}");
        string bin = core == "sing-box" ? "sing-box" : "xray";
        string exec = core == "sing-box" ? $"$(command -v sing-box) run -c {path}" : $"$(command -v xray) run -config {path}";
        await ssh.ExecuteCommandAsync($@"cat > /etc/systemd/system/{bin}.service <<EOF
[Unit]
Description={bin} Service
After=network.target network-online.target
[Service]
User=root
ExecStart={exec}
Restart=on-failure
RestartSec=5
[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload".Replace("\r", ""));
    }
}
