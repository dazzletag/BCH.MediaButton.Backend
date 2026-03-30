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

// Care Plan types

export interface AssessmentField {
  key: string;
  label: string;
  value?: string | null;
  type: "short" | "narrative";
  isPrimary: boolean;
}

export interface CareStep {
  action: string;
  who?: string | null;
}

export interface CareRoutine {
  title: string;
  frequency?: string | null;
  time?: string | null;
  steps?: CareStep[] | null;
}

export interface CareActionsData {
  criticalPreferences?: string[] | null;
  routines?: CareRoutine[] | null;
}

export interface SignOffData {
  completedById?: string | null;
  completedByName?: string | null;
  completedByRole?: string | null;
  completedAt?: string | null;
  residentInvolved?: string | null;
  nextReviewDate?: string | null;
  notes?: string | null;
}

export interface CarePlanVersionResponse {
  id: string;
  carePlanId: string;
  residentId: string;
  section: string;
  versionNumber: number;
  status: "draft" | "submitted" | "signed_off";
  assessmentData?: AssessmentField[] | null;
  careActionsData?: CareActionsData | null;
  signOff?: SignOffData | null;
  createdById?: string | null;
  createdByName?: string | null;
  createdAt: string;
  isCurrent: boolean;
}

export interface CarePlanVersionSummary {
  id: string;
  versionNumber: number;
  status: string;
  createdByName?: string | null;
  createdAt: string;
}

export type CarePlanSection = {
  key: string;
  label: string;
};

export const CARE_PLAN_SECTIONS: CarePlanSection[] = [
  { key: "personal_information", label: "Personal Information" },
  { key: "eating_drinking", label: "Eating & Drinking" },
  { key: "mobility_moving", label: "Mobility & Moving" },
  { key: "personal_care", label: "Personal Care" },
  { key: "skin_integrity", label: "Skin Integrity" },
  { key: "continence", label: "Continence" },
  { key: "communication", label: "Communication" },
  { key: "breathing", label: "Breathing" },
  { key: "mental_health", label: "Mental Health & Wellbeing" },
  { key: "medication", label: "Medication" },
  { key: "sleep_rest", label: "Sleep & Rest" },
  { key: "social_activities", label: "Social & Activities" },
  { key: "end_of_life", label: "End of Life" },
];
