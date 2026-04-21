using System;

namespace KoFFPanel.Presentation.Features.Bot;

public class PendingUserDto
{
    public string Uuid { get; set; } = "";
    public string Email { get; set; } = "";
    public string ServerIp { get; set; } = "";
    public long TrafficLimitBytes { get; set; }
    public bool IsActive { get; set; }
    public DateTime? ExpiryDate { get; set; }
}
