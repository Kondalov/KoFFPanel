namespace KoFFPanel.Presentation.Features.Bot;

public class TrafficSyncDto
{
    public string Uuid { get; set; } = "";
    public long TrafficUsedBytes { get; set; }
    public long TrafficLimitBytes { get; set; }
}
