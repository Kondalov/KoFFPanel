using KoFFPanel.Domain.Entities;

namespace KoFFPanel.Presentation.Messages;

// Сообщение, которое отправляется после успешной установки ядра
public class CoreDeployedMessage
{
    public VpnProfile Server { get; }

    public CoreDeployedMessage(VpnProfile server)
    {
        Server = server;
    }
}