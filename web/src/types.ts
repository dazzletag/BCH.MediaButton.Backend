export type MediaType = "Photo" | "Video";

export type MediaItem = {
  id: string;
  name?: string;
  blobPath: string;
  type: MediaType;
  contentType?: string;
  durationSeconds?: number;
  uploadedAt: string;
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
