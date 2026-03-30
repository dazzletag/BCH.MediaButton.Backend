import Dexie, { type Table } from "dexie";

export interface CachedCarePlan {
  key: string; // `${residentId}::${section}`
  residentId: string;
  section: string;
  data: string; // JSON-serialised CarePlanVersionResponse
  syncedAt: string; // ISO timestamp
}

class HomeLightDb extends Dexie {
  carePlans!: Table<CachedCarePlan, string>;

  constructor() {
    super("homelight");
    this.version(1).stores({
      carePlans: "key, residentId, section",
    });
  }
}

export const db = new HomeLightDb();
