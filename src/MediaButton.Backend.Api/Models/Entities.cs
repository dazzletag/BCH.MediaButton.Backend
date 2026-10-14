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

    /// <summary>
    /// Which care home this device physically sits in. Distinct from the
    /// resident: the EPG a device receives is a property of the building's
    /// aerial and transmitter, so it is shared by every device in the home and
    /// survives the device being reassigned to a different resident.
    /// </summary>
    public Guid? CareHomeId { get; set; }
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

public enum WelcomeImageStatus
{
    /// <summary>Generated and waiting for somebody to look at it.</summary>
    Candidate,
    /// <summary>Chosen by a person; this is the one devices will show.</summary>
    Approved,
    /// <summary>Looked at and turned down. Kept so the same thing is not re-offered.</summary>
    Rejected
}

/// <summary>
/// A generated welcome picture for one resident's screen.
///
/// These are reviewed before any resident sees them. The device used to
/// generate its own image at runtime, which meant nobody ever looked at it
/// first — not acceptable for a picture shown to someone who may not be able
/// to tell us they find it upsetting. So candidates are generated here, a
/// person approves one, and only the approved one reaches a device.
///
/// The prompt is stored alongside the image because when a picture turns out
/// wrong, the prompt is the thing that needs fixing, and six months later
/// nobody will remember what was asked for.
/// </summary>
public class ResidentWelcomeImage
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(200)]
    public string Resident { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string BlobPath { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ContentType { get; set; }

    [Required]
    public WelcomeImageStatus Status { get; set; } = WelcomeImageStatus.Candidate;

    /// <summary>What was asked for, so a poor result can be diagnosed.</summary>
    public string? Prompt { get; set; }

    /// <summary>Which recorded interest the picture came from, if any.</summary>
    [MaxLength(200)]
    public string? Subject { get; set; }

    [MaxLength(200)]
    public string? GeneratedBy { get; set; }

    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(200)]
    public string? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAtUtc { get; set; }
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

    /// <summary>
    /// Superseded by <see cref="CareHomeGrants"/>, which allows more than one.
    /// Still read, and still written when exactly one home is chosen, so that
    /// anything looking at this column keeps working.
    /// </summary>
    public Guid? CareHomeId { get; set; }
    public CareHome? CareHome { get; set; }

    /// <summary>
    /// Every home, including ones opened after this was set.
    ///
    /// Listing homes individually would mean an activities manager quietly
    /// losing sight of a new home on the day it opens, and nobody noticing
    /// until a resident there is missed.
    /// </summary>
    public bool AllCareHomes { get; set; }

    /// <summary>The homes an Activities user may see. Empty means none.</summary>
    public ICollection<UserCareHomeGrant> CareHomeGrants { get; set; } = new List<UserCareHomeGrant>();

    public ICollection<ResidentAccessGrant> AccessGrants { get; set; } = new List<ResidentAccessGrant>();
}

/// <summary>
/// One care home an Activities user may see. There is a row per home, because
/// some people work across several — the activities manager across all of them.
/// </summary>
public class UserCareHomeGrant
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid UserProfileId { get; set; }
    public UserProfile? UserProfile { get; set; }

    [Required]
    public Guid CareHomeId { get; set; }
    public CareHome? CareHome { get; set; }

    public DateTimeOffset GrantedAt { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(200)]
    public string? GrantedBy { get; set; }
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
