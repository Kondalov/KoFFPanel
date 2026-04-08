using System;

namespace KoFFPanel.Domain.Entities;

public class ClientTrafficLog
{
    public int Id { get; set; }
    public string ServerIp { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime Date { get; set; }
    public long BytesUsed { get; set; }
}