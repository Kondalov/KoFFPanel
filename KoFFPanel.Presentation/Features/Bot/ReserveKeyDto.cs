namespace KoFFPanel.Presentation.Features.Bot;

public class ReserveKeyDto 
{ 
    public string Uuid { get; set; } = ""; 
    public string ServerIp { get; set; } = ""; 
    public long TrafficLimitBytes { get; set; } 
}
