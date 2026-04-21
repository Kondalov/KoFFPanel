using System;
using System.Text.RegularExpressions;

namespace KoFFPanel.Infrastructure.Services;

public static partial class SshGuard
{
    // === СТРОГАЯ ВАЛИДАЦИЯ UUID ===
    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex UuidRegex();

    // === СТРОГАЯ ВАЛИДАЦИЯ EMAIL / USERNAME ===
    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.@]+$")]
    private static partial Regex EmailRegex();

    public static bool IsValidUuid(string? uuid) => !string.IsNullOrWhiteSpace(uuid) && UuidRegex().IsMatch(uuid);
    
    public static bool IsValidEmail(string? email) => !string.IsNullOrWhiteSpace(email) && EmailRegex().IsMatch(email);

    // === БЕЗОПАСНОЕ ЭКРАНИРОВАНИЕ ДЛЯ BASH (Single Quote Escape) ===
    public static string Escape(string? argument)
    {
        if (string.IsNullOrEmpty(argument)) return "''";
        // В Bash внутри одинарных кавычек ничего не интерпретируется. 
        // Чтобы вставить саму кавычку, нужно выйти из кавычек, вставить экранированную кавычку и зайти обратно: '\''
        return "'" + argument.Replace("'", "'\\''") + "'";
    }

    public static void ThrowIfInvalid(string? email, string? uuid)
    {
        if (!IsValidEmail(email)) throw new ArgumentException($"КРИТИЧЕСКАЯ ОШИБКА: Недопустимый формат email/имени: {email}");
        if (uuid != null && !IsValidUuid(uuid)) throw new ArgumentException($"КРИТИЧЕСКАЯ ОШИБКА: Недопустимый формат UUID: {uuid}");
    }
}
