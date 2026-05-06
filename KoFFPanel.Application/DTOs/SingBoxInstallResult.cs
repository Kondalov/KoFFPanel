using KoFFPanel.Application.Templates;

namespace KoFFPanel.Application.DTOs;

public class SingBoxInstallResult
{
    public string Uuid { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int Port { get; set; }
    public string Sni { get; set; } = "";

    // ИСПРАВЛЕНИЕ: Оставляем хардкод для HTTP-подписки по просьбе клиента
    public string HttpLink => $"https://link.partherhr.ru/{Uuid}";

    // ИСПРАВЛЕНИЕ: Возвращаем свойства для поддержки кастомных доменов в VLESS и совместимости
    public string? CustomDomain { get; set; }
    public string? ConnectionNode { get; set; }

    public string DisplayServer => !string.IsNullOrWhiteSpace(ConnectionNode) ? ConnectionNode.Trim() : IpAddress;

    public string VlessLink => $"vless://{Uuid}@{DisplayServer}:{Port}?type=tcp&security=reality&pbk={PublicKey}&fp=chrome&sni={Sni}&sid={ShortId}&spx=%2F&flow=xtls-rprx-vision&alpn=h2#SingBox_{IpAddress}";
    public string ClientJson => SingBoxRealityConfigTemplate.GenerateClientConfig(DisplayServer, Port, Uuid, Sni, PublicKey, ShortId);
}
