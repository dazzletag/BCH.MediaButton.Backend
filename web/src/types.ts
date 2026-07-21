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

export type CareHome = {
  id: string;
  name: string;
};

export type UserProfile = {
  id: string;
  azureAdEmail: string;
  displayName?: string | null;
  role: "Activities" | "Relative";
  careHomeId?: string | null;
  careHomeName?: string | null;
  residents: string[];
};

export type ResidentAccessGrant = {
  id: string;
  userProfileId: string;
  residentKey: string;
  grantedAt: string;
  grantedBy?: string | null;
};

// --- Reporting (engagement & usage) ---

export type ReportKpis = {
  totalPlays: number;
  estimatedWatchSeconds: number;
  uniqueVideos: number;
  libraryBytes: number;
  activeResidents: number;
  totalResidents: number;
  onlineDevices: number;
  activeDevices: number;
  deviceCount: number;
  neverPlayedVideos: number;
  avgPlaysPerActiveResident: number;
};

export type ReportCareHome = {
  careHomeId: string | null;
  name: string;
  residents: number;
  activeResidents: number;
  devices: number;
  plays: number;
  watchSeconds: number;
  videos: number;
  libraryBytes: number;
  lastActive: string | null;
};

export type ReportResident = {
  resident: string;
  careHomeName: string | null;
  plays: number;
  watchSeconds: number;
  videos: number;
  libraryBytes: number;
  devices: number;
  lastActive: string | null;
  topTerm: string | null;
};

export type ReportVideo = {
  title: string;
  source: string;
  sourceId: string;
  term: string | null;
  resident: string;
  plays: number;
  watchSeconds: number;
  lastPlayedAt: string | null;
};

export type ReportTerm = { term: string; plays: number; videos: number };
export type ReportSource = { source: string; plays: number; videos: number };
export type ReportGrowthPoint = { date: string; added: number; bytes: number };
export type ReportRecency = { last7: number; last30: number; older: number; never: number };

export type ReportSummary = {
  scope: {
    isAdmin: boolean;
    residentCount: number;
    careHomeCount: number;
    deviceCount: number;
    generatedAtUtc: string;
  };
  kpis: ReportKpis;
  careHomes: ReportCareHome[];
  residents: ReportResident[];
  topVideos: ReportVideo[];
  topTerms: ReportTerm[];
  sourceSplit: ReportSource[];
  libraryGrowth: ReportGrowthPoint[];
  playRecency: ReportRecency;
};
