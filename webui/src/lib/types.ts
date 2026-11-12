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
  /** When it first started; absent before then (and from a client that leaves it out). */
  startedAt?: string;
  /** Review 5:T3: when the latest run started (startedAt is the first start ever). An older server leaves it out. */
  runStartedAt?: string;
  completedAt?: string;
  fileRemovedAfterExtraction?: boolean;
  /** T10: a finished .torrent file the server can add as a torrent (torrent.add with fromJobId). An older server leaves it out. */
  torrentFileReady?: boolean;
  statusNote?: string;
  requestedConnections?: number;
  activeConnections?: number;
  waitingForHost?: string;
  bytesPerSecond?: number;
  estimatedRemaining?: string;
  source?: string;
  /** Added from a browser on another device: the Host opens no window for it. */
  addedFromAnotherDevice?: boolean;
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
  /** The user paused it (its own Pause, or Stop All): a queue start leaves it paused. Older servers leave it out. */
  pausedByUser?: boolean;
  /** False when the download was taken out of its queue (the Q column is then empty). Older servers leave it out (then it is true). */
  isQueued?: boolean;
  pageUrl?: string;
  canCancel?: boolean;
  /** Set on a file a package's Cancel stopped; Resume on the package brings all of them back only if every file has it. */
  cancelledByPackageId?: string;
  startMode?: string;
  conflictPolicy?: string;
  expectedSha256?: string;
  /** The content type the browser hand-off gave; a saved list keeps it (JobDto.HandoffContentType). */
  handoffContentType?: string;
  // D-items: the details view.
  resumeSupported?: boolean | null;
  speedLimitBytesPerSecond?: number | null;
  canChangeSpeedLimit?: boolean;
  categoryIsAutomatic?: boolean;
  showCompletionDialog?: boolean | null;
  completionOptions?: JobCompletionOptions;
  /** T1: set on a torrent job only (TorrentDto). Its url is the magnet, which may carry trackers: never show it. */
  torrent?: Torrent;
}

/** What every client is told about a torrent job (TorrentDto). No file list: that is asked for (torrent.files). */
export interface Torrent {
  infoHash: string;
  name: string;
  hasMetadata: boolean;
  /** The file list is in and the download waits for a choice of files. */
  awaitingFileChoice: boolean;
  fileCount: number;
  wantedCount: number;
  totalBytes: number;
  wantedBytes: number;
  peers: number;
  seeds: number;
  /** T2: the download is complete and the torrent is shared with other peers now. Older servers leave these out. */
  seeding?: boolean;
  seedingSince?: string | null;
  /** Bytes sent to other peers over the torrent's life. */
  uploaded?: number;
  /** Uploaded against the downloaded (wanted) size. */
  ratio?: number;
  /** Bytes per second sent now, while it downloads or seeds. */
  uploadRate?: number;
  /** T3: `magnet` or `file`. Older servers leave the T3 fields out. */
  source?: string;
  /** T3: the files on disk are being checked now. */
  checking?: boolean;
  /** T3: a check of the files runs when the torrent next starts. */
  recheckPending?: boolean;
  /** T3: the torrent's own caps in bytes/s; null is none. */
  downloadLimit?: number | null;
  uploadLimit?: number | null;
  /** T3: `global`, `ratio`, `hours` or `never`, with the ratio or hours. */
  seedGoal?: string;
  seedGoalValue?: number | null;
  /** T3: downloads its pieces in order. */
  sequential?: boolean;
}

/** One file of a torrent (TorrentFileDto). The path is relative, '/' separated. */
export interface TorrentFile {
  index: number;
  path: string;
  size: number;
  wanted: boolean;
  /** T3: `skip`, `low`, `normal` or `high`; an older server leaves it out (normal when wanted). */
  priority?: string | null;
  /** Bytes on disk so far; absent when not known (the torrent isn't running). */
  done?: number | null;
}

export interface TorrentInspectResult {
  ok: boolean;
  error?: string;
  /** `magnet` or `torrentFile`. */
  kind?: string;
  infoHash?: string;
  name?: string;
  /** Absent for a magnet: its files come from the peers. */
  totalBytes?: number | null;
  files?: TorrentFile[] | null;
  /** A job with the same info hash is already in the list. */
  existingJobId?: string | null;
  /** T3: the folder the free space was measured for, the free bytes there (null: not measured), and, when every file
   * would not fit, why in words. */
  destination?: string | null;
  freeBytes?: number | null;
  spaceProblem?: string | null;
}

export interface TorrentAddResult {
  ok: boolean;
  error?: string;
  existing?: boolean;
  job?: Job;
}

export interface TorrentFilesResult {
  ok: boolean;
  error?: string;
  files?: TorrentFile[];
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

export interface CategoryDefinition {
  id: string;
  name: string;
  destinationDirectory?: string;
  extensions: string[];
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
  postDownloadCommand?: string;
  /** Set by the server: why the saved program won't run (review 2:L5). */
  postDownloadCommandProblem?: string;
  updateManifestUrl?: string;
  updatePublicKey?: string;
  allowSystemCompletionActions: boolean;
  favoriteFolders: FavoriteFolder[];
  recentDestinationDirectories: string[];
  downloadGroups: DownloadGroup[];
  /** Torrents are on in the Host's Options. The Windows app's web UI can't change it (WebSettingsRules.PipeOnly). */
  torrentsEnabled?: boolean;
  /** `ask`, `thisPc` or `provider`. */
  torrentRoute?: string;
  torrentChooseFiles?: boolean;
  /** T14: torrents go to a folder for their category, apart from `useCategoryFolders`. Missing from a server before T14. */
  useTorrentCategoryFolders?: boolean;
  /** T14: the torrent folder; empty for the same folders as other downloads. Changed only on that PC. */
  torrentCategoryFoldersRoot?: string;
  torrentHandler?: boolean;
  /** T2: the torrent listen port (0 before one is chosen) and the router request; changed only on that PC. */
  torrentListenPort?: number;
  torrentUpnp?: boolean;
  /** T2 seeding: keep sharing a finished torrent until the ratio or the hours (0 is no limit; never both 0). */
  torrentSeedAfterDownload?: boolean;
  torrentSeedRatio?: number;
  torrentSeedHours?: number;
  /** Bytes per second; 0 is the total speed limit, or none. */
  torrentUploadLimit?: number;
  /** T3: how peers are found and met. Changed only on that PC from the Windows app's web UI (WebSettingsRules.PipeOnly). */
  torrentDht?: boolean;
  torrentPex?: boolean;
  torrentLsd?: boolean;
  /** `prefer`, `require` or `off`. */
  torrentEncryption?: string;
  torrentUseProxy?: boolean;
  /** T3 limits: 0 is all of them / no limit of its own / the torrent program's default. */
  torrentSeedsAtOnce?: number;
  torrentConnectionsPerTorrent?: number;
  torrentMaxConnections?: number;
  excludedFileExtensions: string[];
  excludedHosts: string[];
  /** Built-in category name to its folder; a category left out uses the default folder. */
  categoryFolders?: Record<string, string>;
  customCategories?: CategoryDefinition[];
  deleteCategoryIds?: string[];
  notifyOnCompletion?: boolean;
  showCompletionDialog?: boolean;
  [key: string]: unknown;
}

export interface HostStatus {
  /** Part files that belong to no download, as of the last scan. */
  orphanPartFiles?: string[];
  hostVersion: string;
  activeDownloads: number;
  queuedDownloads: number;
  totalJobs: number;
  lastError?: string;
  /** UI.R37: the newest version the server's own update check found; absent when it hasn't one (or is older). */
  latestVersion?: string | null;
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
  /** `windows` for the desktop app's web bridge, `server` for the headless server; absent from an older server. */
  hostKind?: string;
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
  /** The provider's own word for the state, as it sent it. */
  rawState?: string;
  /** The provider's item this job covers; empty while the provider has not queued it. */
  itemId?: string;
  startMode?: string;
  selectedFileIds?: string[];
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

export interface RemoteAddResult {
  ok: boolean;
  error?: string;
  /** Something asked for that didn't happen, because an active job already covers this input. */
  note?: string;
  existing?: boolean;
  inputKind?: string;
  job?: RemoteJob;
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
  updatedAt?: string;
  expiresAt?: string;
  rawState?: string;
  etaSeconds?: number;
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
  /** The archives were deleted after extracting, so extracting again could only fail. */
  archivesDeleted?: boolean;
  note?: string;
  packageId?: string;
  jobIds: string[];
  attempts?: number;
  createdAt?: string;
  completedAt?: string;
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
  /** Only with `files: 'archives'` in the request: the archive files in the folder. */
  files?: { name: string; path: string; size: number }[];
  truncated: boolean;
  freeBytes?: number;
  totalBytes?: number;
}

/** The server's last hour of total download speed (speed.history), one figure a second, oldest first. */
export interface SpeedHistoryResult {
  start: string;
  intervalSeconds: number;
  bytesPerSecond: number[];
  /** T8: the torrents' upload for the same seconds. Older servers leave it out, as does one whose torrents sent nothing yet. */
  uploadBytesPerSecond?: number[] | null;
}

/** remote.container.preview: what a container holds, without its links. */
export interface ContainerPreview {
  ok?: boolean;
  error?: string;
  format: string;
  notSupported?: boolean;
  previewId?: string;
  expiresAt?: string;
  accountProblem?: string;
  packages: { index: number; name?: string; comment?: string; hasArchivePassword: boolean; entryCount: number }[];
  entries: { entryId: number; packageIndex?: number; displayHost?: string; displayName?: string; size?: number; kind: string; reason?: string; canAdd: boolean }[];
  problems: string[];
}

/** remote.container.add: how each chosen entry went. */
export interface ContainerAddResult {
  ok: boolean;
  error?: string;
  outcomes: { entryId: number; ok: boolean; error?: string; note?: string; existing?: boolean }[];
  jobsLeftOut?: boolean;
}

export interface FolderSpace {
  path: string;
  freeBytes?: number;
  totalBytes?: number;
  insideContainer?: boolean;
}
