// UPD.D1: the dismissible "update available" notice in the web UI. The server's own update check (a Docker install, or a
// paired Windows app's web bridge) finds the newest version; the page shows a notice when one is available and it is
// neither hidden for a day ("Not now") nor skipped ("Skip this version"), both kept on the server. The status bar's
// version note stays while the server is out of date (versionStatus.ts), so an update is never lost.

import { compareVersions } from './versionStatus';
import { elsewhereText, filterNotes, type ChangelogItem, type Features } from './whatsNew';

/** The docker commands the dialog shows, exactly as the mockup draws them. */
export const composeCommand = 'docker compose pull inbound && docker compose up -d inbound';
export const runCommand = 'docker pull ghcr.io/kaan88/inbound:latest';

export type UpdateKind = 'windows' | 'server';

/** Whether the notice should show now, given the server's version, the newest known, and its dismiss state. */
export function noticeShown(
  hostVersion: string | null | undefined,
  latestVersion: string | null | undefined,
  hiddenUntil: string | null | undefined,
  skippedVersion: string | null | undefined,
  now: number,
): boolean {
  if (!hostVersion || !latestVersion) return false;
  if ((compareVersions(latestVersion, hostVersion) ?? 0) <= 0) return false;
  if (hiddenUntil) {
    const until = Date.parse(hiddenUntil);
    if (!Number.isNaN(until) && now < until) return false;
  }
  if (skippedVersion && (compareVersions(latestVersion, skippedVersion) ?? 0) <= 0) return false;
  return true;
}

/** The notice bar's words: a bold lead and the sentence after it, fitted to the kind of install. */
export function noticeWords(kind: UpdateKind, current: string, latest: string): { title: string; body: string } {
  if (kind === 'windows') {
    return {
      title: `Inbound ${latest} is out.`,
      body: `This PC runs ${current}. Open Inbound on this PC and choose Check for Updates to install it.`,
    };
  }
  return {
    title: `Inbound ${latest} is out for Docker.`,
    body: `This server runs ${current}. Pull the new image and restart the container to update.`,
  };
}

/** "This server runs 0.7.2 in Docker" / "This PC runs 0.7.2", the dialog's subtitle. */
export function dialogSubtitle(kind: UpdateKind, current: string): string {
  return kind === 'windows' ? `This PC runs ${current}` : `This server runs ${current} in Docker`;
}

/** The dialog's "how to update" body line for a normal install (no docker commands). */
export function windowsHowTo(): string {
  return 'Open Inbound on this PC and choose Check for Updates (Help › Check for Updates). It downloads and installs the update.';
}

/**
 * The available version's flat manifest notes as this web UI shows them: the lines tagged server, docker, webui or all,
 * and the rest folded under one "Also N changes in …" line. The same rule as what's-new (whatsNew.ts).
 */
export function filterUpdateNotes(
  notes: readonly ChangelogItem[] | null | undefined,
  features: Features,
): { here: string[]; elsewhere: string[] } {
  const view = filterNotes(notes, features);
  const tail = elsewhereText(view.elsewhere);
  return { here: view.here, elsewhere: tail ? [tail] : [] };
}

/**
 * The docker tag line of the update dialog, from the running server's version: an image pinned to :0.9 gets 0.9.x
 * updates, a new 0.10 needs the tag changed. Empty when the version can't be read as major.minor.
 */
export function tagNote(version: string | null | undefined): string {
  const match = /^v?(\d+)\.(\d+)(?:\.\d+)?/.exec((version ?? '').trim());
  if (!match) return '';
  const minor = `${match[1]}.${match[2]}`;
  return `An image pinned to :${minor} gets ${minor}.x updates; a new ${match[1]}.${Number(match[2]) + 1} needs the tag changed.`;
}

/** What to tell the user when the server answered a dismiss with ok: false (null when it took it). */
export function dismissFailure(answer: { ok?: boolean; message?: string | null } | null | undefined): string | null {
  if (!answer || answer.ok !== false) return null;
  return answer.message?.trim() || "The server couldn't hide the update notice.";
}
