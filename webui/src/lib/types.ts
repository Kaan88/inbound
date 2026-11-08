// The payloads the web UI reads, as the server sends them (camelCase, nulls left out). The full shapes are the C#
// classes in src/DownloadManager.Contracts (Dtos.cs, RemoteDtos.cs, ArchiveDtos.cs, FolderBrowseDtos.cs).

export interface Failure {
  kind: string;
  message: string;
  isRetryable?: boolean;
  httpStatus?: number;
}

export interface Job {
  id: string;
  url: string;
  addressHidden?: boolean;
  fileName: string;
  categoryId?: string;
  destinationDirectory: string;
  finalFilePath: string;
  totalBytes?: number;
  receivedBytes: number;
  progress?: number;
  state: string;
  createdAt: string;
  updatedAt: string;
  completedAt?: string;
  fileRemovedAfterExtraction?: boolean;
  statusNote?: string;
  requestedConnections?: number;
  activeConnections?: number;
  waitingForHost?: string;
  bytesPerSecond?: number;
  estimatedRemaining?: string;
  source?: string;
  queueId: number;
  queueName?: string;
  priority?: number;
  description?: string;
  canPause?: boolean;
  canResume?: boolean;
  isTerminal?: boolean;
  failure?: Failure;
  isRemote?: boolean;
  remoteProvider?: string;
  queueRetryAt?: string;
  // D-items: the details view.
  resumeSupported?: boolean | null;
  speedLimitBytesPerSecond?: number | null;
  canChangeSpeedLimit?: boolean;
  canCancel?: boolean;
  categoryIsAutomatic?: boolean;
  showCompletionDialog?: boolean | null;
  completionOptions?: JobCompletionOptions;
  cancelledByPackageId?: string;
}

/** What happens when one download completes: a program (null = the global one, '' = none), opening it, a system action. */
export interface JobCompletionOptions {
  programPath?: string | null;
  openFile: boolean;
  systemAction: string;
  forceSystemAction: boolean;
}

export interface Segment {
  start: number;
  end: number;
  completed: number;
  isComplete: boolean;
  fraction: number;
}

export interface ConnectionInfo {
  id: string;
  host: string;
  state: string;
  rangeStart?: number | null;
  rangeEnd?: number | null;
  receivedBytes: number;
  bytesPerSecond: number;
  statusCode?: number | null;
  httpVersion?: string | null;
  startedAt: string;
}

export interface Statistics {
  capturedAt: string;
  firstRecordedAt?: string | null;
  recordedBytes: number;
  completedBytes: number;
  completedTodayBytes: number;
  completedCount: number;
  completedTodayCount: number;
  failedCount: number;
  cancelledCount: number;
  retainedCount: number;
  archivedCount: number;
  activeCount: number;
  queuedCount: number;
  pausedCount: number;
  bytesPerSecond: number;
}

export interface DownloadGroup {
  id: string;
  name: string;
  key?: string;
  jobIds: string[];
}

export interface Queue {
  id: number;
  name: string;
  maxConcurrentDownloads: number;
  isRunning: boolean;
  order: number;
  jobOrder: string[];
  retryLimit?: number;
  quotaEnabled: boolean;
  quotaBytes: number;
  quotaWindowHours: number;
  quotaBytesUsed: number;
  quotaWindowStart?: string;
}

export interface Schedule {
  id: string;
  name: string;
  queueId: number;
  enabled: boolean;
  kind: string;
  startDate?: string;
  days: string;
  startTime: string;
  stopTime?: string;
  timedStartEnabled: boolean;
  repeatEveryMinutes?: number;
  startOnHostStartup: boolean;
  lastRunAt?: string;
  lastRunError?: string;
  openFileWhenDone: boolean;
  exitWhenDone: boolean;
  shutdownWhenDone: boolean;
  shutdownMode: string;
  forceShutdown: boolean;
}

export interface Proxy {
  mode: string;
  address?: string;
  username?: string;
  password?: string;
  hasPassword: boolean;
  clearPassword?: boolean;
}

export interface SiteLogin {
  id: string;
  site: string;
  username: string;
  password?: string;
  hasPassword: boolean;
}

export interface FavoriteFolder {
  name: string;
  path: string;
}

export interface ConnectionProfile {
  pattern: string;
  maxConnections: number;
}

export interface Settings {
  defaultDownloadDirectory: string;
  maxConcurrentDownloads: number;
  defaultQueueId: number;
  queues: Queue[];
  schedules: Schedule[];
  defaultConnections: number;
  useCategoryFolders: boolean;
  maxConnectionsPerHost: number;
  connectionProfiles: ConnectionProfile[];
  globalSpeedLimitBytesPerSecond?: number;
  proxy: Proxy;
  siteLogins: SiteLogin[];
  allowLocalNetwork: boolean;
  autoResumeUnfinishedOnStartup: boolean;
  browserIntegrationEnabled: boolean;
  ffmpegPath?: string;
  postDownloadCommand?: string;
  updateManifestUrl?: string;
  updatePublicKey?: string;
  allowSystemCompletionActions: boolean;
  favoriteFolders: FavoriteFolder[];
  recentDestinationDirectories: string[];
  downloadGroups: DownloadGroup[];
  excludedFileExtensions: string[];
  excludedHosts: string[];
  notifyOnCompletion?: boolean;
  showCompletionDialog?: boolean;
  customCategories?: { id: string; name: string }[];
  [key: string]: unknown;
}

export interface HostStatus {
  hostVersion: string;
  activeDownloads: number;
  queuedDownloads: number;
  totalJobs: number;
  lastError?: string;
}

export interface Snapshot {
  jobs: Job[];
  settings: Settings;
  status: HostStatus;
}

export interface Hello {
  hostVersion: string;
  protocolVersion: number;
  startedAt: string;
  capabilities: string[];
}

export interface RemoteJob {
  id: string;
  provider: string;
  providerName: string;
  kind: string;
  state: string;
  status: string;
  stage?: string;
  progress: number;
  speedBytesPerSecond: number;
  etaSeconds?: number;
  name?: string;
  displayHost?: string;
  cached: boolean;
  approvalNeeded: boolean;
  heldForApproval: boolean;
  hasLinkPassword: boolean;
  hasArchivePassword: boolean;
  failureKind?: string;
  failureMessage?: string;
  nextCheckAt?: string;
  destination?: string;
  queueId: number;
  selectionMode: string;
  packageId?: string;
  notes: string[];
  createdAt: string;
  updatedAt: string;
}

export interface RemoteAccount {
  provider: string;
  providerName: string;
  capabilities: string[];
  itemKinds: string[];
  hasKey: boolean;
  keyUnreadable: boolean;
  plan?: string;
  premium: boolean;
  premiumUntil?: string;
  cooldownUntil?: string;
  checkedAt?: string;
}

export interface RemoteFile {
  fileId: string;
  relativePath?: string;
  size: number;
  infected: boolean;
  selectable: boolean;
  note?: string;
}

export interface ClassifyResult {
  ok: boolean;
  error?: string;
  kind: string;
  provider?: string;
  providerName?: string;
  itemKind?: string;
  itemId?: string;
  name?: string;
  displayHost?: string;
  hosterName?: string;
  hosterDailyLinksLeft?: number;
  hosterDailyBytesLeft?: number;
  hosterLargestFile?: number;
  hosterAvailable?: boolean;
  hosterNote?: string;
  reason?: string;
  note?: string;
  checkIncomplete: boolean;
  cached?: boolean;
  files?: RemoteFile[];
}

export interface RemoteItem {
  provider: string;
  kind: string;
  itemId: string;
  name?: string;
  size: number;
  stage?: string;
  progress: number;
  speedBytesPerSecond: number;
  cached: boolean;
  error?: string;
  createdAt?: string;
  expiresAt?: string;
  fileCount: number;
  files?: RemoteFile[];
}

export interface Hoster {
  name: string;
  domains: string[];
  available: boolean;
  type?: string;
  note?: string;
  dailyLinkLimit?: number;
  dailyLinksUsed?: number;
  dailyBandwidthLimit?: number;
  dailyBandwidthUsed?: number;
  perLinkSizeLimit?: number;
}

export interface ArchiveSet {
  id: string;
  name: string;
  directory: string;
  firstVolume: string;
  state: string;
  stateText: string;
  failureKind?: string;
  failureMessage?: string;
  needsPassword: boolean;
  hasPassword: boolean;
  volumes: string[];
  missingVolumes: string[];
  downloadingVolumes: string[];
  /** Parts of downloadingVolumes: all paused; won't start by themselves (start later, stopped queue); waiting in a running queue. Older servers leave them out. */
  pausedVolumes?: string[];
  heldVolumes?: string[];
  queuedVolumes?: string[];
  failedVolumes: string[];
  phase?: string;
  percent?: number;
  destination?: string;
  note?: string;
  packageId?: string;
  jobIds: string[];
  updatedAt: string;
}

export interface ArchiveSettings {
  autoExtract: boolean;
  destination: string;
  folder?: string;
  preserveFolders: boolean;
  conflictPolicy: string;
  deleteArchives: boolean;
}

export interface FolderBrowseResult {
  ok: boolean;
  error?: string;
  path?: string;
  parent?: string;
  roots: string[];
  folders: { name: string; path: string }[];
  truncated: boolean;
  freeBytes?: number;
  totalBytes?: number;
}

export interface FolderSpace {
  path: string;
  freeBytes?: number;
  totalBytes?: number;
  insideContainer?: boolean;
}
