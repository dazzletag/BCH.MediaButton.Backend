using System.Text.Json;
using MediaButtonBackend.Models;

namespace MediaButtonBackend.Api.Models;

public record MediaUploadRequest(string FileName, MediaType Type, string Resident, string? ContentType, int? DurationSeconds);

public record MediaUploadResponse(Uri UploadUrl, string BlobPath, DateTimeOffset ExpiresAtUtc);

public record MediaRegisterRequest(string BlobPath, MediaType Type, string Resident, string? Name, string? ContentType, int? DurationSeconds);

public record PlaylistCreateRequest(string Name, List<PlaylistItemRequest> Items);

public record PlaylistItemRequest(Guid MediaId, int Order, int? DurationSeconds);

public record PlaylistResponse(Guid Id, string Name, IReadOnlyList<PlaylistItemResponse> Items);

public record PlaylistItemResponse(Guid MediaId, string? Name, MediaType Type, Uri Url, int Order, int? DurationSeconds);

public record DevicePlaylistResponse(string DeviceId, string? PlaylistName, IReadOnlyList<PlaylistItemResponse> Items, object? Config);

public record DeviceSharedConfig(
    string? TenantId,
    string? ClientId,
    string? ClientSecret,
    string? DriveId,
    string? ItemId,
    string? ItemPath,
    string? PlaylistFlowUrl);

public record DeviceConfigResponse(string DeviceId, object? Config, DeviceSharedConfig? SharedConfig = null);

public record AiPlaylistPayload(
    string Resident,
    string? SurveyHash,
    string? Model,
    List<string> Playlist,
    DateTimeOffset? BuiltAt,
    Dictionary<string, object>? Meta);

/// <summary>
/// A playlist save. BaseUpdatedAtUtc is the ManualUpdatedAt the editor loaded;
/// when it is older than what is stored, someone else has saved in the
/// meantime and this request would silently discard their work, so it is
/// rejected with 409 rather than applied. Omit it only for callers that
/// genuinely intend to overwrite whatever is there.
/// </summary>
public record ManualPlaylistUpdate(List<JsonElement> Items, DateTimeOffset? BaseUpdatedAtUtc = null);

public record SuggestTermsPayload(List<string> Terms);

public record ResidentPlaylistAssignment(string? PlaylistId, List<string>? RadioFavorites, List<string>? PlaylistUrls, string? SeasonalTheme, string? Resident);

public record ManualPlaylistResponse(string Resident, IReadOnlyList<object?> Items, DateTimeOffset? UpdatedAtUtc, string? UpdatedBy, DateTimeOffset? LastPolledAt);

public record DeviceManualPlaylistResponse(string Resident, IReadOnlyList<object?> Items, DateTimeOffset? UpdatedAtUtc, string? UpdatedBy);

public record MediaPatchRequest(string? Name, string? BlobPath);

public record DeviceListItem(string DeviceId, string? DisplayName, Guid? PlaylistId, string? PlaylistName, string? DeviceKey, string? ResidentKey, string? MobizioId);

public record DeviceCreateRequest(string DeviceId, string? DisplayName);

public record DeviceRenameRequest(string? DisplayName);

public record RegisterResidentRequest(string ResidentName, string? CaseId, string? TenantCaseId);

// -------------------------------------------------------------------------
// Pi-local video cache: snapshot push + admin queries + command queue
// -------------------------------------------------------------------------

/// <summary>One video as reported by the Pi in its periodic snapshot.</summary>
public record CachedVideoSnapshotItem(
    string Source,
    string SourceId,
    string Title,
    string? Term,
    long? FilesizeBytes,
    int? DurationSeconds,
    DateTimeOffset DownloadedAt,
    DateTimeOffset? LastPlayedAt,
    int PlayCount,
    bool Protected);

public record CacheSnapshotRequest(
    DateTimeOffset SnapshotAt,
    string Resident,
    IReadOnlyList<CachedVideoSnapshotItem> Videos);

public record CachedVideoView(
    Guid Id,
    string DeviceId,
    string Resident,
    string Source,
    string SourceId,
    string Title,
    string? Term,
    long? FilesizeBytes,
    int? DurationSeconds,
    DateTimeOffset DownloadedAt,
    DateTimeOffset? LastPlayedAt,
    int PlayCount,
    bool Protected,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public record ResidentCacheResponse(
    string Resident,
    IReadOnlyList<CachedVideoView> Videos,
    IReadOnlyList<string> Devices,
    DateTimeOffset? LastSeenAt);

public record DeviceCacheCommandView(
    Guid Id,
    string DeviceId,
    string CommandType,
    object Payload,
    DateTimeOffset CreatedAt,
    string Status);

public record ForceTermRequest(string Term, int? Count);

public record CommandAckRequest(string Status, object? Result);

