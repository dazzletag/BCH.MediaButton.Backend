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

public record DeviceConfigResponse(string DeviceId, object? Config);

public record AiPlaylistPayload(
    string Resident,
    string? SurveyHash,
    string? Model,
    List<string> Playlist,
    DateTimeOffset? BuiltAt,
    Dictionary<string, object>? Meta);

public record ManualPlaylistUpdate(List<string> Items);

public record ManualPlaylistResponse(string Resident, IReadOnlyList<string> Items, DateTimeOffset? UpdatedAtUtc, string? UpdatedBy);

public record DeviceManualPlaylistResponse(string Resident, IReadOnlyList<object> Items, DateTimeOffset? UpdatedAtUtc, string? UpdatedBy);
