using System;

namespace KoFFPanel.Domain.Entities;

public class ClientConnectionLog
{
    public int Id { get; set; }
    public string ServerIp { get; set; } = "";
    public string Email { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Country { get; set; } = "";
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}