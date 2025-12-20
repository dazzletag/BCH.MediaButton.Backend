using System.ComponentModel.DataAnnotations;

namespace MediaButtonBackend.Models;

public enum MediaType
{
    Photo,
    Video
}

public class Device
{
    [Key]
    [MaxLength(100)]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    public Guid? PlaylistId { get; set; }

    /// <summary>
    /// JSON blob for per-device config overrides (e.g., interval, volume).
    /// </summary>
    [MaxLength(4000)]
    public string? ConfigJson { get; set; }
}

public class Playlist
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public ICollection<PlaylistItem> Items { get; set; } = new List<PlaylistItem>();
}

public class PlaylistItem
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid PlaylistId { get; set; }

    public Playlist? Playlist { get; set; }

    [Required]
    public Guid MediaId { get; set; }

    public MediaAsset? Media { get; set; }

    public int Order { get; set; }

    public int? DurationSeconds { get; set; }
}

public class MediaAsset
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(200)]
    public string? Name { get; set; }

    [Required]
    [MaxLength(500)]
    public string BlobPath { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ContentType { get; set; }

    [Required]
    public MediaType Type { get; set; }

    public int? DurationSeconds { get; set; }

    public string? UploadedBy { get; set; }

    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ResidentPlaylistSnapshot
{
    [Key]
    [MaxLength(200)]
    public string Resident { get; set; } = string.Empty;

    public string? AiPlaylistJson { get; set; }

    public DateTimeOffset? AiUpdatedAt { get; set; }

    public string? ManualPlaylistJson { get; set; }

    public DateTimeOffset? ManualUpdatedAt { get; set; }

    [MaxLength(200)]
    public string? ManualUpdatedBy { get; set; }
}
