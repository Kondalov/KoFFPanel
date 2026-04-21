using KoFFPanel.Application.Interfaces;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface ISmartPortValidator
{
    Task<(bool IsValid, string ErrorMessage)> ValidatePortAsync(ISshService ssh, string serverId, int port, string protocolType);
    Task<int> SuggestBestPortAsync(ISshService ssh, string serverId, string protocolType);
}
