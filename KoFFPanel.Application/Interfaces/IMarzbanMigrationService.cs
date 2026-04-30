using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

/// <summary>
/// Interface for migrating users from Marzban SQL dumps to KoFFPanel.
/// </summary>
public interface IMarzbanMigrationService
{
    /// <summary>
    /// Performs the migration of users.
    /// </summary>
    /// <param name="marzbanSqlPath">Path to marzban_db.sql</param>
    /// <param name="botSqlPath">Path to bot_db.sql</param>
    /// <param name="serverIp">Target server IP for the migrated users.</param>
    /// <returns>A result containing success status and a report message.</returns>
    Task<(bool IsSuccess, string Message)> MigrateAsync(string marzbanSqlPath, string botSqlPath, string serverIp);
}
