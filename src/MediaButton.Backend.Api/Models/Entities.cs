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

    /// <summary>
    /// Secret key used by the Pi to authenticate. Stored plaintext; admin-only readable.
    /// </summary>
    [MaxLength(200)]
    public string? DeviceKey { get; set; }

    /// <summary>
    /// The resident this device is currently assigned to (set by the Pi setup wizard).
    /// </summary>
    [MaxLength(200)]
    public string? ResidentKey { get; set; }

    /// <summary>
    /// Mobizio numeric case ID for the assigned resident.
    /// </summary>
    [MaxLength(100)]
    public string? MobizioId { get; set; }

    /// <summary>
    /// Mobizio tenant case ID (e.g. CASE-29959) — required for case-scoped API endpoints.
    /// </summary>
    [MaxLength(100)]
    public string? MobizioTenantId { get; set; }
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

/// <summary>
/// A single video the Pi has cached locally on disk, as reported by the
/// device's periodic snapshot push. The backend stores these so staff and
/// relatives can see what's actually on each Pi and act on it (delete,
/// trigger playback, force more downloads for a term).
///
/// Authoritative state lives on the Pi; this is a mirror that lags by up
/// to the snapshot interval (~5 minutes by default). LastSeenAt lets us
/// drop rows whose file the Pi has stopped reporting.
/// </summary>
public class CachedVideo
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    public string DeviceId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Resident { get; set; } = string.Empty;

    /// <summary>'yt', 'fam', or a future value like 'azure'.</summary>
    [Required]
    [MaxLength(20)]
    public string Source { get; set; } = "yt";

    [Required]
    [MaxLength(200)]
    public string SourceId { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    /// <summary>First term that linked this video on the Pi, if reported.</summary>
    [MaxLength(500)]
    public string? Term { get; set; }

    public long? FilesizeBytes { get; set; }

    public int? DurationSeconds { get; set; }

    public DateTimeOffset DownloadedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastPlayedAt { get; set; }

    public int PlayCount { get; set; }

    public bool Protected { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A pending action to be applied on the Pi. Created by admin/relative
/// users via the portal, polled by the Pi on its sync tick, and acked once
/// applied. Three command types so far: delete_video, play_video,
/// force_term. PayloadJson schema depends on type.
/// </summary>
public class DeviceCacheCommand
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>delete_video | play_video | force_term</summary>
    [Required]
    [MaxLength(40)]
    public string CommandType { get; set; } = string.Empty;

    [Required]
    public string PayloadJson { get; set; } = "{}";

    [MaxLength(200)]
    public string? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>pending | acked | applied | failed | expired</summary>
    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "pending";

    public DateTimeOffset? AckedAt { get; set; }

    public string? ResultJson { get; set; }
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

    public DateTimeOffset? LastPolledAt { get; set; }

    /// <summary>
    /// Mobizio numeric case ID for this resident (populated by the Pi setup wizard).
    /// </summary>
    [MaxLength(100)]
    public string? MobizioId { get; set; }

    /// <summary>
    /// Mobizio tenant case ID (e.g. CASE-29959) — required for case-scoped API endpoints.
    /// </summary>
    [MaxLength(100)]
    public string? MobizioTenantId { get; set; }

    /// <summary>Care home this resident is assigned to (used to scope Activities-team access).</summary>
    public Guid? CareHomeId { get; set; }
}

public class CareHome
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Portal user profile. Relative users see only their granted residents;
/// Activities users see all residents at their assigned care home.
/// Admins (Azure AD Admin role) bypass this table entirely.
/// </summary>
public class UserProfile
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Azure AD object ID (oid claim). Populated on first sign-in; null until then.</summary>
    [MaxLength(100)]
    public string? AzureAdObjectId { get; set; }

    /// <summary>Azure AD UPN / email — used as the lookup key when OID is not yet set.</summary>
    [Required]
    [MaxLength(200)]
    public string AzureAdEmail { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    /// <summary>Activities | Relative</summary>
    [Required]
    [MaxLength(20)]
    public string Role { get; set; } = "Relative";

    /// <summary>For Activities users: restricts access to residents at this care home.</summary>
    public Guid? CareHomeId { get; set; }
    public CareHome? CareHome { get; set; }

    public ICollection<ResidentAccessGrant> AccessGrants { get; set; } = new List<ResidentAccessGrant>();
}

/// <summary>Grants a Relative user access to a specific resident.</summary>
public class ResidentAccessGrant
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid UserProfileId { get; set; }

    public UserProfile? UserProfile { get; set; }

    [Required]
    [MaxLength(200)]
    public string ResidentKey { get; set; } = string.Empty;

    public DateTimeOffset GrantedAt { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(200)]
    public string? GrantedBy { get; set; }
}
