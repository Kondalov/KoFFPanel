namespace KoFFPanel.Application.Interfaces;

public class UserOnlineInfo
{
    public string Email { get; set; } = "";
    public string LastIp { get; set; } = "";
    public int ActiveSessions { get; set; }
    public string Country { get; set; } = "";
}
