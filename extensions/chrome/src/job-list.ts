// The popup's view of the app's download list: which downloads to show, and what can be done to each.

export interface JobSummary {
  id: string;
  /** Where the download is, when a server is paired too ("local" or "server"); absent means this PC. */
  target?: "local" | "server";
  fileName: string;
  state: string;
  receivedBytes: number;
  totalBytes?: number;
  progress?: number;
  bytesPerSecond?: number;
  estimatedRemaining?: string | number | null;
  failure?: { message?: string };
  createdAt?: string;
  canPause?: boolean;
  canResume?: boolean;
  isTerminal?: boolean;
  /** Where it came from, as the app names it ("chromeAuto" and "chromeContextMenu" are this browser's). */
  source?: string;
  /** The extension that sent it, as the Host recorded it (a web caller's carries "web:"); absent from an older app. */
  ownerExtensionId?: string | null;
  /** How the download was added: "startNow", "queue" or "later" (the Host's StartMode). */
  startMode?: string;
  /** The address it was added from. A provider's file has a remote:// one, which is no link. */
  url?: string;
  /** The real address held a key and the app keeps it; url is a masked copy that downloads nothing. */
  addressHidden?: boolean;
  /** A finished download whose file was deleted on purpose after it was extracted. */
  fileRemovedAfterExtraction?: boolean;
  /**
   * Its files are the app's to manage (the native host and a paired server set this): the popup offers no rename or
   * redownload for it, and one that waits for the user in the app is opened there rather than resumed.
   */
  appManaged?: boolean;
}

export type JobAction = "start" | "pause" | "resume" | "retry" | "remove";

/** A row's buttons: the job actions, and "open" for a download that waits for the user in the app. */
export type RowAction = JobAction | "open";

/** The commands the popup may send, as the native host's allow-list names them. */
export const JOB_ACTION_MESSAGES: Record<JobAction, string> = {
  // U-EX-8: starting a "Download later" item is the Host's resume, which sets it to start now.
  start: "job.resume",
  pause: "job.pause",
  resume: "job.resume",
  retry: "job.retry",
  remove: "job.remove",
};

/**
 * EXT.R11: the right-click menu's commands, each on one download, as the native host's allow-list names them. Rename
 * is job.update with a new file name only; reveal goes to this PC only. EXT.R13: open is a row's double-click on a
 * finished download of this PC; the app shows a program in its folder instead of starting it.
 */
export type JobCommand = "reveal" | "rename" | "redownload" | "show" | "open";

export const JOB_COMMAND_MESSAGES: Record<JobCommand, string> = {
  // EXT.R12: the app's main window at this download (host.openUi with its id; an older app opens the window only).
  show: "host.openUi",
  reveal: "job.reveal",
  open: "job.open",
  rename: "job.update",
  redownload: "job.redownload",
};

/** Said when the app or server answering is older than these commands. */
export const UPDATE_APP_FOR_COMMAND = "Update Inbound to use this.";
export const UPDATE_SERVER_FOR_COMMAND = "Update the Inbound server to use this.";

/** An older native host, Host or server refuses a message it doesn't know, in one of these words. */
function refusedAsUnknown(text: string): boolean {
  return text === "The browser extension cannot send this message." ||
    text.startsWith("Unknown message type") ||
    text.startsWith("A paired browser can't send");
}

export interface JobCommandOutcome {
  ok: boolean;
  error?: string;
  job?: JobSummary;
  message?: string;
}

/**
 * EXT.R11: the app's answer to a row-menu command, as the popup shows it. An app older than the command refuses it
 * with words of its own, which become "Update Inbound". A Host older than Rename ignores the new name and still says
 * ok, so a rename counts as done only when the download it returns has the new name.
 */
export function jobCommandOutcome(command: JobCommand, result: any, options: { newFileName?: string; server?: boolean } = {}): JobCommandOutcome {
  const update = options.server ? UPDATE_SERVER_FOR_COMMAND : UPDATE_APP_FOR_COMMAND;
  if (result?.ok !== true) {
    const text = String(result?.error || result?.message || "");
    if (refusedAsUnknown(text)) return { ok: false, error: update };
    return { ok: false, error: text || "The app did not do that." };
  }
  const job = result.job as JobSummary | undefined;
  if (command === "rename" && options.newFileName !== undefined && job?.fileName !== options.newFileName) {
    return { ok: false, error: `The file was not renamed. ${update}`, job };
  }
  return { ok: true, job, message: typeof result.message === "string" ? result.message : undefined };
}

/** What the row menu offers (GitHub #4). Open file is the row's double-click (EXT.R13), not a menu entry. */
export type RowMenuChoice = "show" | "reveal" | "copyLink" | "redownload" | "rename" | "remove";

export interface RowMenuEntry {
  id: RowMenuChoice;
  label: string;
  enabled: boolean;
  title?: string;
}

/** Said wherever Redownload is offered or asked about. */
export const REDOWNLOAD_NOTE = "Saves as a new copy. The existing file is kept.";

/** The download is on this PC, not on the paired server. */
export function onThisPc(job: JobSummary): boolean {
  return (job.target ?? "local") === "local";
}

/** Its address can be copied or used again: a real web link, not a provider's file or a hidden key (FTP was cut in 0.7.2). */
export function hasUsableLink(job: JobSummary): boolean {
  const url = (job.url ?? "").trim();
  return /^https?:\/\//i.test(url) && job.addressHidden !== true;
}

/** EXT.R13: a double-click (or Enter) opens the file itself: a finished download of this PC whose file is still there. */
export function opensFile(job: JobSummary): boolean {
  return onThisPc(job) && job.state === "completed" && job.fileRemovedAfterExtraction !== true;
}

/** Redownload as the desktop allows it: a finished, failed or stopped download with a usable link. */
export function canRedownload(job: JobSummary): boolean {
  return (job.state === "completed" || job.state === "failed" || job.state === "cancelled") && hasUsableLink(job) &&
    job.appManaged !== true;
}

/** Rename is for a finished download's file. Whether the file is still there only the app can say. */
export function canRename(job: JobSummary): boolean {
  return job.state === "completed" && job.fileRemovedAfterExtraction !== true && job.appManaged !== true;
}

/**
 * A stopped download the app can't simply resume: it waits for the user's choice in the app. The app says so with
 * canResume false on a paused download (an older app always says true, and the row keeps Resume).
 */
export function waitsInApp(job: JobSummary): boolean {
  return job.state === "paused" && job.canResume === false && job.isTerminal !== true;
}

/** The folder button beside Delete: a finished download on this PC. */
export function showsFolderButton(job: JobSummary): boolean {
  return onThisPc(job) && job.state === "completed";
}

/**
 * The right-click menu of a row, in order. "Show in app" and the folder only for this PC: a server's downloads have no
 * app window on this screen (its web page is the row's own click).
 */
export function rowMenuEntries(job: JobSummary): RowMenuEntry[] {
  const entries: RowMenuEntry[] = [];
  if (onThisPc(job)) entries.push({ id: "show", label: "Show in app", enabled: true });
  if (onThisPc(job)) entries.push({ id: "reveal", label: "Open containing folder", enabled: true });
  entries.push({ id: "copyLink", label: "Copy download link", enabled: hasUsableLink(job),
    title: hasUsableLink(job) ? undefined : "This download has no link that can be copied." });
  entries.push({ id: "redownload", label: "Redownload", enabled: canRedownload(job), title: REDOWNLOAD_NOTE });
  entries.push({ id: "rename", label: "Rename…", enabled: canRename(job),
    title: canRename(job) ? undefined : job.appManaged === true ? "This download is managed in the app." : "Only a completed download can be renamed." });
  entries.push({ id: "remove", label: "Delete…", enabled: true });
  return entries;
}

/** Why a new name can't be used, before asking the app (which checks again), or null. */
export function badFileName(name: string): string | null {
  const trimmed = name.trim();
  if (trimmed.length === 0) return "Type a file name.";
  if (/[\\/:*?"<>|]/.test(trimmed) || trimmed === "." || trimmed === "..") return "A file name can't contain \\ / : * ? \" < > |.";
  return null;
}

export const POPUP_JOB_LIMIT = 20;

function createdTime(job: JobSummary): number {
  const time = job.createdAt ? Date.parse(job.createdAt) : NaN;
  return Number.isFinite(time) ? time : 0;
}

/** The newest downloads first, whatever their source or state. */
export function latestJobs(jobs: readonly JobSummary[], limit = POPUP_JOB_LIMIT): JobSummary[] {
  return jobs
    .filter((job) => typeof job?.id === "string" && job.id.length > 0)
    .map((job, index) => ({ job, index }))
    // Newest first; the app's own order breaks ties, so the list does not jump about.
    .sort((left, right) => createdTime(right.job) - createdTime(left.job) || left.index - right.index)
    .slice(0, limit)
    .map((entry) => entry.job);
}

/**
 * The popup's list when a server is paired too: the newest of each place, this PC first, so one busy place
 * can't push the other out of view. Without targets it is latestJobs.
 */
export function latestJobsByTarget(jobs: readonly JobSummary[], limit = POPUP_JOB_LIMIT): JobSummary[] {
  if (!jobs.some((job) => job?.target === "server")) return latestJobs(jobs, limit);
  return [
    ...latestJobs(jobs.filter((job) => job?.target !== "server"), limit),
    ...latestJobs(jobs.filter((job) => job?.target === "server"), limit),
  ];
}

/**
 * Whether Delete asks about the file. Only a finished download has one to keep; an unfinished one has only
 * partial data, which nothing can use once the download is off the list, so Delete always removes it, as the
 * app's own window and the web UI do (UI.B19). Without this a kept partial file, and the folder holding it, stayed.
 */
export function deleteAsksAboutFile(job: JobSummary): boolean {
  return job.state === "completed";
}

/** What Delete sends as deleteFile: the box's answer for a finished download, always yes otherwise. */
export function deleteFileFor(job: JobSummary, boxTicked: boolean): boolean {
  return deleteAsksAboutFile(job) ? boxTicked : true;
}

/** A download that waits for the user to start it: added with "Download later", or to a queue not running. */
export function waitsToBeStarted(job: JobSummary): boolean {
  return job.state === "queued" && (job.startMode === "later" || job.startMode === "queue");
}

/**
 * The row's buttons, two at most, from what the app says the job allows: Start for one that waits to be started
 * (U-EX-8), else Stop while it can stop, else Resume (Retry for a failed or cancelled one), else Open in app for one
 * that waits for the user in the app on this PC (a server's has none: its window is not on this screen), then Delete.
 * A finished download offers Delete only, whatever the app still does with it.
 */
export function actionsFor(job: JobSummary): RowAction[] {
  const actions: RowAction[] = [];
  if (job.state === "completed") {
    // Nothing to stop or resume once it is done.
  } else if (waitsToBeStarted(job)) actions.push("start");
  else if (job.canPause) actions.push("pause");
  else if (waitsInApp(job)) {
    if (onThisPc(job)) actions.push("open");
  } else if (job.canResume) actions.push(job.state === "failed" || job.state === "cancelled" ? "retry" : "resume");
  actions.push("remove");
  return actions;
}

/** The popup's group heading for a place: "This PC", or the server's name with "(server)". */
export function groupLabel(target: "local" | "server", serverName?: string): string {
  return target === "server" ? `${serverName?.trim() || "Server"} (server)` : "This PC";
}
