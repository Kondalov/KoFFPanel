using System;

namespace KoFFPanel.Presentation.Features.Bot;

public class LegacyUserDto 
{ 
    public string Uuid { get; set; } = ""; 
    public string Email { get; set; } = "";
    public string ServerIp { get; set; } = "";
    public long TrafficLimitBytes { get; set; }
    public long? ReffererId { get; set; }
    public DateTime? ExpiryDate { get; set; }
    }
