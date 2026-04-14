using System;
using System.Collections.Generic;
using System.Linq;

namespace KoFFPanel.Domain.Entities;

public class VpnProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "root";
    public string Password { get; set; } = "";
    public string? KeyPath { get; set; }

    // === НОВАЯ АРХИТЕКТУРА ===
    public List<ServerInbound> Inbounds { get; set; } = new();

    // ИСПРАВЛЕНИЕ: Жестко храним тип установленного ядра ("xray" или "sing-box")
    public string CoreType { get; set; } = "xray";

    [Obsolete("Используйте коллекцию Inbounds.")]
    public int VpnPort { get; set; } = 443;
    [Obsolete] public string Uuid { get; set; } = "";
    [Obsolete] public string PrivateKey { get; set; } = "";
    [Obsolete] public string PublicKey { get; set; } = "";
    [Obsolete] public string ShortId { get; set; } = "";
    [Obsolete] public string Sni { get; set; } = "www.microsoft.com";

    public void MigrateLegacyData()
    {
        if (!Inbounds.Any() && !string.IsNullOrEmpty(PublicKey))
        {
            var legacySettings = new
            {
                uuid = string.IsNullOrEmpty(Uuid) ? Guid.NewGuid().ToString() : Uuid,
                privateKey = PrivateKey,
                publicKey = PublicKey,
                shortId = ShortId,
                sni = Sni
            };

            Inbounds.Add(new ServerInbound
            {
                Tag = "vless-reality",
                Protocol = "vless",
                Port = VpnPort > 0 ? VpnPort : 443,
                SettingsJson = System.Text.Json.JsonSerializer.Serialize(legacySettings)
            });

            CoreType = "xray"; // Старые сервера всегда были на Xray
            PublicKey = "";
            PrivateKey = "";
        }
    }
}