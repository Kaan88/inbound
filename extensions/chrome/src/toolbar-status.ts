// EXT.R12: the toolbar icon shows the downloads sent from this browser to the app on this PC, as the browser's own
// download icon shows its downloads: a ring that fills green while they download, orange while one is paused, red when
// one failed, and the plain icon when nothing is going on. Pure rules, so they can be tested without a browser.

import type { JobSummary } from "./job-list";

export type IconKind = "idle" | "downloading" | "paused" | "error";

export interface IconState {
  kind: IconKind;
  /** 0 to 1 while downloading or paused, when the sizes are known; null when they are not. */
  progress: number | null;
  /** How many downloads the state is about. */
  count: number;
}

export const IDLE: IconState = { kind: "idle", progress: null, count: 0 };

/** The Host's sources for a download the extension sent: caught automatically, or from a menu, popup or window. */
export const BROWSER_SOURCES: ReadonlySet<string> = new Set(["chromeAuto", "chromeContextMenu"]);

/** The Host's mark on the owner of a download a web-UI or server caller added. */
const WEB_OWNER_PREFIX = "web:";

/**
 * Sent from this browser: the icon follows only these, as the browser's icon follows only its own downloads. With
 * this extension's id, one another browser's extension sent to the same app is not followed (review 3 N7); a
 * download whose owner the app does not say (an older app) still is.
 */
export function sentFromBrowser(job: Pick<JobSummary, "source" | "ownerExtensionId"> | undefined | null,
  extensionId?: string): boolean {
  if (typeof job?.source !== "string" || !BROWSER_SOURCES.has(job.source)) return false;
  const owner = job.ownerExtensionId;
  if (!extensionId || typeof owner !== "string" || owner.length === 0) return true;
  return owner === extensionId || owner === WEB_OWNER_PREFIX + extensionId;
}

const RUNNING_STATES: ReadonlySet<string> = new Set(["connecting", "downloading", "retryWaiting", "verifying"]);

/**
 * Under way: transferring, about to, waiting to try again or checking the file. A queued one counts only when it was
 * added to start now (it waits for a free slot); one added for later or to a stopped queue waits for the user.
 */
export function isRunning(job: Pick<JobSummary, "state" | "startMode">): boolean {
  return RUNNING_STATES.has(job.state) || (job.state === "queued" && (job.startMode ?? "startNow") === "startNow");
}

/** The share of the bytes that arrived, over all of them, or null when any size is unknown. */
export function combinedProgress(jobs: readonly JobSummary[]): number | null {
  let received = 0;
  let total = 0;
  for (const job of jobs) {
    const size = job.totalBytes;
    if (typeof size !== "number" || !(size > 0)) return null;
    total += size;
    received += Math.min(Math.max(job.receivedBytes || 0, 0), size);
  }
  return total > 0 ? received / total : null;
}

/** What the icon shows for these downloads: downloading first, then a failure, then a pause. */
export function iconState(jobs: Iterable<JobSummary>): IconState {
  const all = [...jobs];
  const running = all.filter(isRunning);
  if (running.length > 0) return { kind: "downloading", progress: combinedProgress(running), count: running.length };
  const failed = all.filter((job) => job.state === "failed");
  if (failed.length > 0) return { kind: "error", progress: null, count: failed.length };
  const paused = all.filter((job) => job.state === "paused");
  if (paused.length > 0) return { kind: "paused", progress: combinedProgress(paused), count: paused.length };
  return IDLE;
}

/** Steps of the ring: the icon is drawn again only when the state or the step changes, never per event. */
export const RING_STEPS = 24;

/** Equal for two states that draw the same icon; finished is how many finished downloads have not been looked at (EXT.R14). */
export function drawKey(state: IconState, finished = 0): string {
  const step = state.progress === null ? "-" : String(Math.floor(Math.min(Math.max(state.progress, 0), 1) * RING_STEPS));
  return `${state.kind}:${step}:${Math.min(finished, FINISHED_SHOWN_MAX)}`;
}

/** EXT.R14: finished downloads the icon counts (and the session storage keeps), newest first. */
export const FINISHED_KEEP = 20;
/** The badge shows at most this many. */
export const FINISHED_SHOWN_MAX = 99;

/** The finished list with one more at the front (a download finishing again moves to the front). */
export function addFinished(ids: readonly string[], id: string): string[] {
  return [id, ...ids.filter((known) => known !== id)].slice(0, FINISHED_KEEP);
}

/** Reads the stored finished list: only non-empty strings. */
export function parseFinished(value: unknown): string[] {
  return Array.isArray(value)
    ? value.filter((id): id is string => typeof id === "string" && id.length > 0).slice(0, FINISHED_KEEP)
    : [];
}

/** The badge text for the finished count, or "" for none. */
export function finishedBadge(count: number): string {
  return count > 0 ? String(Math.min(count, FINISHED_SHOWN_MAX)) : "";
}

/** The tooltip for finished downloads, or null for none. */
export function finishedTitle(count: number): string | null {
  if (count <= 0) return null;
  const what = count === 1 ? "a download has" : `${count} downloads have`;
  return `Inbound Download Manager: ${what} finished. Open the popup to see ${count === 1 ? "it" : "them"}.`;
}

/**
 * The tooltip while automatic sending is off or paused (the badge says OFF then), with the finished downloads after it;
 * null when sending is on.
 */
export function offTitle(off: boolean, paused: boolean, finished: number): string | null {
  const base = off
    ? "Inbound Download Manager: automatic sending is off"
    : paused
      ? "Inbound Download Manager: sending is paused"
      : null;
  if (!base) return null;
  if (finished <= 0) return base;
  const what = finished === 1 ? "A download has" : `${finished} downloads have`;
  return `${base}. ${what} finished. Open the popup to see ${finished === 1 ? "it" : "them"}.`;
}

/** The icon's tooltip for a state, or null when idle (the catching status shows then). */
export function iconTitle(state: IconState): string | null {
  const name = "Inbound Download Manager";
  const files = (count: number) => (count === 1 ? "1 download" : `${count} downloads`);
  switch (state.kind) {
    case "downloading": {
      const percent = state.progress === null ? "" : `, ${Math.floor(state.progress * 100)}%`;
      return `${name}: ${files(state.count)} from the browser${percent}`;
    }
    case "paused":
      return `${name}: ${state.count === 1 ? "a download is" : `${state.count} downloads are`} stopped`;
    case "error":
      return `${name}: ${state.count === 1 ? "a download" : `${state.count} downloads`} failed. Open the popup to see why.`;
    default:
      return null;
  }
}

export interface WatchChange {
  /** A followed download just finished. */
  finished: boolean;
}

/**
 * The downloads sent from the browser that the icon follows, kept up to date from the app's job events (and the
 * popup's list). A finished, stopped or removed download leaves; a failed or paused one stays until the popup has
 * been opened (it was seen there) or it runs again.
 */
export class DownloadWatch {
  private readonly jobs = new Map<string, JobSummary>();

  /** extensionId: this extension's, so only its own downloads are followed (see sentFromBrowser). */
  constructor(private readonly extensionId?: string) {}

  /** One download as an event brought it. */
  update(job: JobSummary | undefined | null, eventType?: string): WatchChange {
    if (!job || typeof job.id !== "string" || job.id.length === 0) return { finished: false };
    const known = this.jobs.get(job.id);
    if (!sentFromBrowser(job, this.extensionId) ||eventType === "removed" || job.state === "cancelled") {
      this.jobs.delete(job.id);
      return { finished: false };
    }
    if (job.state === "completed") {
      this.jobs.delete(job.id);
      return { finished: known !== undefined };
    }
    this.jobs.set(job.id, job);
    return { finished: false };
  }

  /** The popup's list: downloads under way join (after a browser restart, say); followed ones are brought up to date. */
  take(jobs: readonly JobSummary[]): void {
    for (const job of jobs) {
      if (!job?.id) continue;
      if (this.jobs.has(job.id)) this.update(job);
      else if (sentFromBrowser(job, this.extensionId) && isRunning(job)) this.jobs.set(job.id, job);
    }
  }

  /** The popup was opened: failures and pauses were seen there, and the icon stops showing them. */
  acknowledge(): void {
    for (const [id, job] of this.jobs) {
      if (!isRunning(job)) this.jobs.delete(id);
    }
  }

  /**
   * The app's events stopped (its connection closed, or it said it is exiting): nothing will tell how the downloads
   * under way end, so the icon stops showing them. Failures and pauses already known stay until the popup is opened.
   */
  forgetRunning(): void {
    for (const [id, job] of this.jobs) {
      if (isRunning(job)) this.jobs.delete(id);
    }
  }

  state(): IconState {
    return iconState(this.jobs.values());
  }

  get size(): number {
    return this.jobs.size;
  }
}
