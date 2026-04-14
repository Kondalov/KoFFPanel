using System;

namespace KoFFPanel.Domain.Entities;

// Класс, описывающий один глобальный порт на сервере
public class ServerInbound
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // Название для удобства (например: "Основной VLESS", "Резервный Hysteria")
    public string Tag { get; set; } = "";

    // Тип протокола: vless, hysteria2, trusttunnel
    public string Protocol { get; set; } = "vless";

    // Тот самый порт, который будет проверять наш Умный алгоритм
    public int Port { get; set; } = 443;

    // JSON-строка, в которой лежат специфичные ключи (Сертификаты, ShortID, SNI и т.д.)
    // Это позволяет не плодить 100 полей в классе, а хранить любые настройки гибко!
    public string SettingsJson { get; set; } = "{}";
}