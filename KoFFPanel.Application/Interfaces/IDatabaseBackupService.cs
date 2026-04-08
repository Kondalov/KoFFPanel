using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IDatabaseBackupService
{
    Task CreateBackupAsync();
}