using System.ComponentModel.DataAnnotations;

namespace MediaButtonBackend.Models;

/// <summary>
/// One broadcast programme, as seen by a care home's own aerial.
///
/// Keyed by care home rather than by device: the EPG is a property of the
/// building's aerial and transmitter, so every device in a home shares one
/// listing, and two homes on different transmitters legitimately differ.
///
/// SeriesCrid is the part that makes "keep watching Emmerdale" possible.
/// Freeview broadcasts a content reference identifier that links every
/// episode of a serial together, so a standing choice can be expressed
/// exactly rather than inferred from the title, which would break the first
/// time a broadcaster renamed an episode.
/// </summary>
public class EpgEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CareHomeId { get; set; }

    /// <summary>Channel as the tuner reports it, e.g. "BBC ONE West".</summary>
    [Required]
    [MaxLength(200)]
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Freeview channel number, when the broadcast carries one.</summary>
    public int? ChannelNumber { get; set; }

    /// <summary>TVHeadend's channel uuid on the originating device.</summary>
    [MaxLength(64)]
    public string? ChannelUuid { get; set; }

    [Required]
    [MaxLength(400)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(400)]
    public string? Subtitle { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }

    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset StopUtc { get; set; }

    /// <summary>
    /// crid://... identifying the serial. Present on roughly four fifths of
    /// events in practice; null for one-off programmes.
    /// </summary>
    [MaxLength(300)]
    public string? SeriesCrid { get; set; }

    /// <summary>crid://... identifying this specific episode.</summary>
    [MaxLength(300)]
    public string? EpisodeCrid { get; set; }

    [MaxLength(200)]
    public string? Genre { get; set; }

    public bool IsHd { get; set; }
    public bool IsSubtitled { get; set; }
    public bool IsAudioDescribed { get; set; }

    /// <summary>Which device reported it — useful when a home has several.</summary>
    [MaxLength(100)]
    public string? SourceDeviceId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
