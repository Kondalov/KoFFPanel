using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace KoFFPanel.Domain.Entities;

public class ClientBehaviorLog
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string ServerIp { get; set; } = "";

    [Required]
    public string Email { get; set; } = "";

    [Required]
    public DateTime Date { get; set; } // Округляется до начала дня (00:00:00)

    public int MaxConcurrentSessions { get; set; }
    public int UniqueAsnCount { get; set; }
    public int GeoJumpsCount { get; set; }
    public long BytesUsedSpike { get; set; }

    // Риск-фактор в процентах (0-100+)
    public int RiskScore { get; set; }

    [NotMapped]
    public bool IsBanned => RiskScore >= 100;
}