export type MediaType = "Photo" | "Video";

export type MediaItem = {
  id: string;
  name?: string;
  blobPath: string;
  type: MediaType;
  contentType?: string;
  durationSeconds?: number;
  uploadedAt: string;
  resident?: string;
  uploadedBy?: string;
};

export type PlaylistItem = {
  mediaId: string;
  name?: string;
  type: MediaType;
  url: string;
  order: number;
  durationSeconds?: number;
};

export type Playlist = {
  id: string;
  name: string;
  items: PlaylistItem[];
};

export type DevicePlaylistResponse = {
  deviceId: string;
  playlistName?: string | null;
  items: PlaylistItem[];
  config?: unknown;
};

export type ManualPlaylistResponse = {
  resident: string;
  items: string[];
  updatedAtUtc?: string | null;
  updatedBy?: string | null;
  lastPolledAt?: string | null;
};

export type AiPlaylistEnvelope = {
  resident: string;
  payload?: { playlist?: string[]; [key: string]: unknown };
  updatedAtUtc?: string | null;
};

export type ResidentList = string[];

export type Device = {
  deviceId: string;
  displayName?: string | null;
  playlistId?: string | null;
  playlistName?: string | null;
  deviceKey?: string | null;
};

// --- Pi-local video cache: mirror surfaced through the portal ---

export type CachedVideo = {
  id: string;
  deviceId: string;
  resident: string;
  source: string;            // 'yt' | 'fam' | future 'azure'
  sourceId: string;
  title: string;
  term?: string | null;
  filesizeBytes?: number | null;
  durationSeconds?: number | null;
  downloadedAt: string;
  lastPlayedAt?: string | null;
  playCount: number;
  protected: boolean;
  firstSeenAt: string;
  lastSeenAt: string;
};

export type ResidentCacheResponse = {
  resident: string;
  videos: CachedVideo[];
  devices: string[];
  lastSeenAt?: string | null;
};

export type DeviceCacheCommand = {
  id: string;
  deviceId: string;
  commandType: "delete_video" | "play_video" | "force_term";
  payload: Record<string, unknown>;
  createdAt: string;
  status: "pending" | "acked" | "applied" | "failed" | "expired";
};
