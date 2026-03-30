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

public record ManualPlaylistUpdate(List<JsonElement> Items);

public record ResidentPlaylistAssignment(string? PlaylistId, List<string>? RadioFavorites, List<string>? PlaylistUrls, string? SeasonalTheme, string? Resident);

public record ManualPlaylistResponse(string Resident, IReadOnlyList<object?> Items, DateTimeOffset? UpdatedAtUtc, string? UpdatedBy, DateTimeOffset? LastPolledAt);

public record DeviceManualPlaylistResponse(string Resident, IReadOnlyList<object?> Items, DateTimeOffset? UpdatedAtUtc, string? UpdatedBy);

public record MediaPatchRequest(string? Name, string? BlobPath);

public record DeviceListItem(string DeviceId, string? DisplayName, Guid? PlaylistId, string? PlaylistName, string? DeviceKey, string? ResidentKey, string? MobizioId);

public record DeviceCreateRequest(string DeviceId, string? DisplayName);

public record DeviceRenameRequest(string? DisplayName);

public record RegisterResidentRequest(string ResidentName, string? CaseId);

// Care Plan DTOs
public record CarePlanVersionResponse(
    Guid Id,
    Guid CarePlanId,
    string ResidentId,
    string Section,
    int VersionNumber,
    string Status,
    List<AssessmentField>? AssessmentData,
    CareActionsData? CareActionsData,
    SignOffData? SignOff,
    string? CreatedById,
    string? CreatedByName,
    DateTimeOffset CreatedAt,
    bool IsCurrent);

public record CarePlanVersionSummary(
    Guid Id,
    int VersionNumber,
    string Status,
    string? CreatedByName,
    DateTimeOffset CreatedAt);

public record AssessmentField(
    string Key,
    string Label,
    string? Value,
    string Type,
    bool IsPrimary);

public record CareActionsData(
    List<string>? CriticalPreferences,
    List<CareRoutine>? Routines);

public record CareRoutine(
    string Title,
    string? Frequency,
    string? Time,
    List<CareStep>? Steps);

public record CareStep(
    string Action,
    string? Who);

public record SignOffData(
    string? CompletedById,
    string? CompletedByName,
    string? CompletedByRole,
    DateTimeOffset? CompletedAt,
    string? ResidentInvolved,
    string? NextReviewDate,
    string? Notes);

public record CarePlanDraftRequest(
    List<AssessmentField>? AssessmentData,
    CareActionsData? CareActionsData);

public record CarePlanSignOffRequest(
    List<AssessmentField>? AssessmentData,
    CareActionsData? CareActionsData,
    string? ResidentInvolved,
    string? NextReviewDate,
    string? Notes);
