// Typed client for the linkarr API (same origin in production, rewritten to the local backend in dev).

export interface Rights {
  admin: boolean;
  watch: boolean;
  allLibraries: boolean;
  libraries: string[];
  request: boolean;
  manageDownloads: boolean;
  modularrAccess: boolean;
}

export interface Me {
  id: number;
  username: string;
  displayName: string | null;
  admin: boolean;
  rights: Rights;
  // An app missed the last password: entering it again (Account page) sends it.
  passwordPending: boolean;
  created: string;
  avatar: string | null;
}

export interface AuthState {
  setupRequired: boolean;
  // LINKARR_SETUP_TOKEN is set: setup asks for it.
  setupTokenRequired?: boolean;
  user: Me | null;
}

export type SyncStatus = "Pending" | "Synced" | "OutOfSync";

export interface ManagedUser {
  id: number;
  username: string;
  displayName: string | null;
  disabled: boolean;
  rights: Rights;
  created: string;
  avatar: string | null;
  visularrId: string | null;
  modularrId: number | null;
  syncStatus: SyncStatus;
  syncError: string | null;
  lastSynced: string | null;
  passwordPending: boolean;
  lastSignIn: string | null;
}

// A signed-in browser. id is a hash of the session, never the cookie's secret; ip is where it signed in, lastIp where it was last seen.
export interface SessionInfo {
  id: string;
  created: string;
  lastSeen: string;
  ip: string | null;
  lastIp: string | null;
  userAgent: string | null;
  current: boolean;
}

export type AppName = "visularr" | "modularr";

export interface UnmanagedUser {
  app: AppName;
  remoteId: string;
  name: string;
  admin: boolean;
  // modularr users created outside linkarr cannot be deleted through it, only adopted.
  linkable: boolean;
}

export interface Library {
  itemId: string;
  name: string;
  collectionType: string | null;
}

export type CatalogState = "Available" | "Downloading" | "NotIndexed" | "Untracked" | "Missing" | "Unmonitored";
export type TitleKind = "Movie" | "Series";

export interface CatalogItem {
  key: string;
  kind: TitleKind;
  title: string;
  year: number;
  tmdbId: number | null;
  tvdbId: number | null;
  state: CatalogState;
  monitored: boolean;
  hasFile: boolean;
  modularrId: number | null;
  modularrPath: string | null;
  visularrId: string | null;
  progress: number | null;
  poster: string | null;
}

export interface DiscoverItem {
  kind: TitleKind;
  tmdbId: number | null;
  tvdbId: number | null;
  title: string;
  year: number;
  overview: string | null;
  poster: string | null;
  inLibrary: boolean;
  requested: boolean;
}

export type RequestStatus = "pending" | "denied" | "failed" | "requested" | "downloading" | "available";

export interface TitleRequest {
  id: number;
  username: string;
  kind: TitleKind;
  tmdbId: number | null;
  tvdbId: number | null;
  title: string;
  year: number;
  poster: string | null;
  created: string;
  error: string | null;
  status: RequestStatus;
  progress: number | null;
}

export interface Settings {
  // Set by the deployment's environment: shown read-only, the environment wins on save.
  deploymentFields: (keyof SettingsInput)[];
  modularrUrl: string | null;
  modularrKeySet: boolean;
  modularrPublicUrl: string | null;
  visularrUrl: string | null;
  visularrKeySet: boolean;
  visularrPublicUrl: string | null;
  linkarrPublicUrl: string | null;
  requestsNeedApproval: boolean;
  autoFixNotIndexed: boolean;
  requestsPerDay: number;
}

// An API key left null keeps the saved one, "" clears it.
export interface SettingsInput {
  modularrUrl: string | null;
  modularrApiKey: string | null;
  modularrPublicUrl: string | null;
  visularrUrl: string | null;
  visularrApiKey: string | null;
  visularrPublicUrl: string | null;
  linkarrPublicUrl: string | null;
  requestsNeedApproval: boolean;
  autoFixNotIndexed: boolean;
  requestsPerDay: number;
}

export interface Links {
  modularr: string | null;
  visularr: string | null;
}

export interface Dashboard {
  counts: Record<CatalogState, number>;
  downloads: { key: string; kind: TitleKind; title: string; year: number; tmdbId: number | null; tvdbId: number | null; progress: number; poster: string | null }[];
  users: { total: number; outOfSync: number; unmanaged: number } | null;
  pendingRequests: number;
  lastCatalogSync: string | null;
  catalogError: string | null;
  lastUserSync: string | null;
  userError: string | null;
  links: Links;
}

export type CatalogCounts = Record<CatalogState, number>;

export interface Snapshot {
  date: string;
  movies: number;
  series: number;
  available: number;
  downloading: number;
  notIndexed: number;
  untracked: number;
  missing: number;
  unmonitored: number;
}

export interface ServiceHealth {
  app: AppName;
  configured: boolean;
  reachable: boolean;
  version: string | null;
  latencyMs: number | null;
  error: string | null;
}

export interface PublicIp {
  ip: string;
  country: string | null;
  isp: string | null;
  checkedAt: string | null;
}

export interface Watching {
  user: string;
  accountId: number | null;
  device: string | null;
  client: string;
  ip: string | null;
  lastActivity: string | null;
  nowPlaying: { title: string | null; kind: string | null; year: number | null; paused: boolean; progress: number | null } | null;
}

export interface Stats {
  catalog: {
    counts: CatalogCounts;
    kinds: Record<TitleKind, number>;
    trend: Snapshot[];
    recentlyImported: { key: string; kind: TitleKind; title: string; year: number; tmdbId: number | null; tvdbId: number | null; poster: string | null; at: string }[];
    recentlyRequested: { id: number; kind: TitleKind; title: string; year: number; poster: string | null; username: string; created: string; status: RequestStatus }[];
    topRequesters: { username: string; count: number }[];
  };
  // Null when modularr is not configured or did not answer.
  downloads: { active: number; downloadBps: number; uploadBps: number; remainingBytes: number; etaSeconds: number | null } | null;
  storage: { path: string; freeBytes: number; totalBytes: number }[];
  publicIp: PublicIp | null;
  services: ServiceHealth[];
  sync: { lastCatalogSync: string | null; catalogError: string | null; lastUserSync: string | null; userError: string | null };
  users: { id: number; username: string; displayName: string | null; disabled: boolean; admin: boolean; lastSignIn: string | null; avatar: string | null; sessions: SessionInfo[] }[];
  watching: Watching[];
}

export const UNAUTHORIZED = "linkarr:unauthorized";

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, {
    ...init,
    // A Blob body (a picture) carries its own type.
    headers: typeof init?.body === "string" ? { "Content-Type": "application/json" } : undefined,
  });
  if (response.status === 401 && !path.startsWith("/auth/")) window.dispatchEvent(new Event(UNAUTHORIZED));
  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { error?: string } | null;
    throw new Error(body?.error ?? `${response.status} ${response.statusText}`);
  }
  return response.status === 204 || response.status === 202 ? (undefined as T) : ((await response.json()) as T);
}

const send = <T>(method: string, path: string, body?: unknown): Promise<T> =>
  request<T>(path, { method, body: body === undefined ? undefined : JSON.stringify(body) });

const query = (params: Record<string, string | undefined>): string =>
  new URLSearchParams(Object.entries(params).filter((e): e is [string, string] => !!e[1])).toString();

export const api = {
  authState: (): Promise<AuthState> => request("/auth/state"),
  setup: (username: string, password: string, setupToken?: string): Promise<Me> => send("POST", "/auth/setup", { username, password, setupToken }),
  login: (username: string, password: string): Promise<Me> => send("POST", "/auth/login", { username, password }),
  logout: (): Promise<void> => send("POST", "/auth/logout"),
  changePassword: (currentPassword: string, newPassword: string): Promise<Me> => send("POST", "/auth/password", { currentPassword, newPassword }),
  updateProfile: (displayName: string | null): Promise<Me> => send("PATCH", "/auth/profile", { displayName }),
  setAvatar: (image: Blob): Promise<Me> => request("/auth/avatar", { method: "PUT", body: image }),
  removeAvatar: (): Promise<Me> => send("DELETE", "/auth/avatar"),
  signOutEverywhere: (): Promise<void> => send("POST", "/auth/signout-all"),
  sessions: (): Promise<SessionInfo[]> => request("/auth/sessions"),
  endSession: (id: string): Promise<void> => send("POST", "/auth/sessions/signout", { id }),

  users: (): Promise<ManagedUser[]> => request("/users"),
  createUser: (input: { username: string; displayName: string | null; password: string; rights: Rights }): Promise<ManagedUser> => send("POST", "/users", input),
  updateUser: (id: number, input: { username?: string; displayName?: string | null; rights?: Rights; disabled?: boolean }): Promise<ManagedUser> => send("PATCH", `/users/${id}`, input),
  signOutUser: (id: number): Promise<void> => send("POST", `/users/${id}/signout`),
  resetPassword: (id: number, password: string): Promise<ManagedUser> => send("POST", `/users/${id}/password`, { password }),
  syncUser: (id: number, password: string | null): Promise<ManagedUser> => send("POST", `/users/${id}/sync`, { password }),
  deleteUser: (id: number): Promise<void> => send("DELETE", `/users/${id}`),
  userSessions: (id: number): Promise<SessionInfo[]> => request(`/users/${id}/sessions`),
  endUserSession: (userId: number, id: string): Promise<void> => send("POST", `/users/${userId}/sessions/signout`, { id }),
  syncUsers: (): Promise<{ users: ManagedUser[]; unmanaged: UnmanagedUser[] }> => send("POST", "/users/sync"),
  unmanaged: (): Promise<UnmanagedUser[]> => request("/users/unmanaged"),
  adopt: (app: AppName, remoteId: string): Promise<ManagedUser> => send("POST", "/users/unmanaged/adopt", { app, remoteId }),
  deleteUnmanaged: (app: AppName, remoteId: string): Promise<void> => send("POST", "/users/unmanaged/delete", { app, remoteId }),
  libraries: (): Promise<Library[]> => request("/libraries"),

  catalog: (filter: { state?: CatalogState; kind?: TitleKind; search?: string }): Promise<CatalogItem[]> => request(`/catalog?${query(filter)}`),
  syncCatalog: (): Promise<{ lastCatalogSync: string | null; catalogSyncError: string | null }> => send("POST", "/catalog/sync"),
  refreshTitle: (key: string): Promise<void> => send("POST", `/catalog/refresh?${query({ key })}`),
  monitorTitle: (key: string): Promise<void> => send("POST", `/catalog/monitor?${query({ key })}`),

  discover: (kind: TitleKind, term: string): Promise<DiscoverItem[]> => request(`/discover?${query({ kind, term })}`),
  requests: (): Promise<TitleRequest[]> => request("/requests"),
  requestTitle: (kind: TitleKind, id: number): Promise<{ id: number; status: RequestStatus }> => send("POST", "/requests", { kind, id }),
  approveRequest: (id: number): Promise<{ id: number; status: RequestStatus; error: string | null }> => send("POST", `/requests/${id}/approve`),
  denyRequest: (id: number): Promise<void> => send("POST", `/requests/${id}/deny`),
  deleteRequest: (id: number): Promise<void> => send("DELETE", `/requests/${id}`),

  settings: (): Promise<Settings> => request("/settings"),
  saveSettings: (input: SettingsInput): Promise<Settings> => send("PUT", "/settings", input),
  rotateWebhookToken: (): Promise<void> => send("POST", "/settings/webhook-token"),
  testApp: (app: AppName, url: string | null, apiKey: string | null): Promise<{ ok: boolean; message: string }> => send("POST", "/settings/test", { app, url, apiKey }),
  links: (): Promise<Links> => request("/links"),
  dashboard: (): Promise<Dashboard> => request("/dashboard"),
  stats: (): Promise<Stats> => request("/stats"),
};

export const errorMessage = (e: unknown): string => (e instanceof Error ? e.message : String(e));

// The title in visularr's web client (deep link handled by visularr).
export const watchHref = (visularr: string | null, item: { kind: TitleKind; tmdbId: number | null; tvdbId: number | null }): string | null => {
  if (!visularr) return null;
  if (item.kind === "Movie" && item.tmdbId) return `${visularr}/web/open/?tmdb=${item.tmdbId}`;
  if (item.kind === "Series" && item.tvdbId) return `${visularr}/web/open/?tvdb=${item.tvdbId}`;
  return null;
};

export const displayName = (user: { username: string; displayName: string | null }): string => user.displayName ?? user.username;

export const when = (iso: string | null): string => (iso ? new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" }) : "never");

const relative = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
const steps: readonly [Intl.RelativeTimeFormatUnit, number][] = [["year", 31_536_000], ["month", 2_592_000], ["week", 604_800], ["day", 86_400], ["hour", 3600], ["minute", 60]];

// "5 minutes ago", "yesterday"; "just now" under a minute. The exact time goes in a title next to it.
export const ago = (iso: string | null, never = "never"): string => {
  if (!iso) return never;
  const seconds = (new Date(iso).getTime() - Date.now()) / 1000;
  const step = steps.find(([, size]) => Math.abs(seconds) >= size);
  return step ? relative.format(Math.round(seconds / step[1]), step[0]) : "just now";
};

const number = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });
const units = ["B", "KB", "MB", "GB", "TB", "PB"] as const;

// Decimal units, as disks are sold: 1.2 TB, 830 KB.
export const bytes = (value: number): string => {
  const power = value > 0 ? Math.min(Math.floor(Math.log10(value) / 3), units.length - 1) : 0;
  return `${number.format(value / 1000 ** power)} ${units[power]}`;
};

export const rate = (bytesPerSecond: number): string => `${bytes(bytesPerSecond)}/s`;

// "13 min", "2 h 5 min", "3 d".
export const duration = (seconds: number): string => {
  const minutes = Math.max(1, Math.round(seconds / 60));
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return minutes % 60 ? `${hours} h ${minutes % 60} min` : `${hours} h`;
  return `${Math.round(hours / 24)} d`;
};

// "Firefox on Windows" from a user agent; enough to tell one's own devices apart, the full string goes in a title.
export const device = (userAgent: string | null): string => {
  if (!userAgent) return "Unknown device";
  const ua = userAgent;
  const browser = /Edg\//.test(ua) ? "Edge" : /OPR\//.test(ua) ? "Opera" : /Firefox\//.test(ua) ? "Firefox" : /Chrome\//.test(ua) ? "Chrome"
    : /Safari\//.test(ua) ? "Safari" : /curl|Wget|python|Go-http/i.test(ua) ? "Script" : null;
  const system = /iPhone/.test(ua) ? "iPhone" : /iPad/.test(ua) ? "iPad" : /Android/.test(ua) ? "Android" : /Windows/.test(ua) ? "Windows"
    : /Mac OS X|Macintosh/.test(ua) ? "macOS" : /CrOS/.test(ua) ? "ChromeOS" : /Linux/.test(ua) ? "Linux" : null;
  return browser && system ? `${browser} on ${system}` : browser ?? system ?? ua.slice(0, 40);
};
