// EXT.B12 (owner, 2026-11-23): a site that refuses the app's request is tried in the browser once. The app sends the
// extension's cookies, referrer and user agent, but a site can want more than the browser told the extension (a
// signed request, a one-time link, a script's own header): claude.ai answers a cookieless request with 403. Rather than
// leave the user with a failed download, the browser, which still has everything the page had, downloads it itself.
// Only the app's own answer says this: a 401 or 403 before any byte arrived. Pure rules, tested without a browser.

import type { JobSummary } from "./job-list";

/**
 * The HTTP status with which the site refused the app's request, or null when this failure is not that: 401 and 403
 * with no byte received (an error page may have arrived as "bytes", so a partial transfer is not a refusal).
 */
export function refusedBySite(job: Pick<JobSummary, "state" | "receivedBytes" | "failure">): number | null {
  if (job.state !== "failed") return null;
  const status = job.failure?.httpStatus;
  if (status !== 401 && status !== 403) return null;
  return (job.receivedBytes ?? 0) > 0 ? null : status;
}

/** A failure this new: the event is about the download just sent, not an old one. */
export const FRESH_FAILURE_MS = 2 * 60 * 1000;
/** A download added this long ago is not the one just sent. */
export const FRESH_JOB_MS = 15 * 60 * 1000;

/**
 * The event is this download failing now, not any later change to a download that failed long ago (the app sends
 * "updated" for a rename or a new description too, and "removed" when it is deleted): its failure happened in the last
 * two minutes and it was added in the last fifteen. Without both times (an older app) nothing is given back.
 */
export function freshFailure(job: Pick<JobSummary, "createdAt" | "failure">, eventType: string | undefined, now: number): boolean {
  if (eventType === "removed") return false;
  const failedAt = Date.parse(job.failure?.occurredAt ?? "");
  const addedAt = Date.parse(job.createdAt ?? "");
  if (!Number.isFinite(failedAt) || !Number.isFinite(addedAt)) return false;
  return now - failedAt <= FRESH_FAILURE_MS && now - addedAt <= FRESH_JOB_MS;
}

/** What the popup and the notification say: "The site refused Inbound (HTTP 403), so your browser downloads it." */
export function refusedNote(status: number): string {
  return `The site refused Inbound (HTTP ${status}), so your browser downloads it.`;
}

/** Why the browser could not start it again (the site answered, or the browser refused the file name). */
export const REFUSED_RESTART_FAILED = "The browser could not start it. Download it again from the page.";
