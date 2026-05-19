using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Models;

public class UserSettings
{
    [Key]
    [ForeignKey(nameof(UserWhitelist))]
    public int UserId { get; set; }

    public bool IsAutoStatusEnabled { get; set; } = true;

    public bool IsRouteOptimizationEnabled { get; set; }

    public bool IsAutoAcceptOrdersEnabled { get; set; }

    public UserWhitelist UserWhitelist { get; set; } = null!;
}
