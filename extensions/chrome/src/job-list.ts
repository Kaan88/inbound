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
  /** How the download was added: "startNow", "queue" or "later" (the Host's StartMode). */
  startMode?: string;
}

export type JobAction = "start" | "pause" | "resume" | "retry" | "remove";

/** The commands the popup may send, as the native host's allow-list names them. */
export const JOB_ACTION_MESSAGES: Record<JobAction, string> = {
  // U-EX-8: starting a "Download later" item is the Host's resume, which sets it to start now.
  start: "job.resume",
  pause: "job.pause",
  resume: "job.resume",
  retry: "job.retry",
  remove: "job.remove",
};

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
 * (U-EX-8), else Stop while it can stop, else Resume (Retry for a failed or cancelled one), then Delete.
 */
export function actionsFor(job: JobSummary): JobAction[] {
  const actions: JobAction[] = [];
  if (waitsToBeStarted(job)) actions.push("start");
  else if (job.canPause) actions.push("pause");
  else if (job.canResume && job.state !== "completed") actions.push(job.state === "failed" || job.state === "cancelled" ? "retry" : "resume");
  actions.push("remove");
  return actions;
}

/** The popup's group heading for a place: "This PC", or the server's name with "(server)". */
export function groupLabel(target: "local" | "server", serverName?: string): string {
  return target === "server" ? `${serverName?.trim() || "Server"} (server)` : "This PC";
}
