// What the web UI knows about the server, kept up to date from envelopes. A plain function of (model, message), so
// the tests feed it envelopes; the Svelte store holds one of these in reactive state.

import type { Envelope } from './protocol';
import type { Site, SitesStatus, SiteTransfer } from './sites';
import type { ArchiveSet, DownloadGroup, Hello, HostStatus, Job, RemoteAccount, RemoteJob, Settings, Snapshot } from './types';

export interface Model {
  hello: Hello | null;
  jobs: Record<string, Job>;
  settings: Settings | null;
  status: HostStatus | null;
  groups: DownloadGroup[];
  remoteJobs: Record<string, RemoteJob>;
  accounts: RemoteAccount[];
  archives: Record<string, ArchiveSet>;
  /** True once the server has answered archive.list, even with no sets. */
  archivesAnswered: boolean;
  /** Grows with each snapshot, so views can tell a fresh start from an update. */
  snapshots: number;
  exiting: boolean;
  /** Sites (SITES.7): null until the server has said; the list and every transfer batch, as it last sent them. */
  sitesStatus: SitesStatus | null;
  sites: Site[];
  siteTransfers: SiteTransfer[];
}

/**
 * PERF.19: the downloads by id, in an object with no prototype. Svelte's `$state` leaves such an object (and the jobs in
 * it) as it is instead of wrapping every job in a reactive proxy, so going through 5,000 jobs costs no proxy traps (about
 * 130 ms a second at 5,000 jobs before). The table is replaced, never edited, so whatever reads `model.jobs` still
 * follows each change; a job itself is replaced too (a job event carries the whole job).
 */
export function jobTable(source: Iterable<Job> | Record<string, Job> = []): Record<string, Job> {
  const table = Object.create(null) as Record<string, Job>;
  if (Symbol.iterator in source) for (const job of source as Iterable<Job>) table[job.id] = job;
  else Object.assign(table, source);
  return table;
}

export function emptyModel(): Model {
  return {
    hello: null,
    jobs: jobTable(),
    settings: null,
    status: null,
    groups: [],
    remoteJobs: {},
    accounts: [],
    archives: {},
    archivesAnswered: false,
    snapshots: 0,
    exiting: false,
    sitesStatus: null,
    sites: [],
    siteTransfers: [],
  };
}

export function hasCapability(model: Model, name: string): boolean {
  return model.hello?.capabilities.includes(name) ?? false;
}

/** Applies one envelope; returns whether anything changed. Types it doesn't know are ignored, as the protocol asks. */
export function apply(model: Model, message: Envelope): boolean {
  switch (message.type) {
    case 'server.hello':
      model.hello = message.payload as Hello;
      model.exiting = false;
      return true;

    case 'jobs.snapshot': {
      const snapshot = message.payload as Snapshot;
      model.jobs = jobTable(snapshot.jobs ?? []);
      if (snapshot.settings) {
        model.settings = snapshot.settings;
        model.groups = snapshot.settings.downloadGroups ?? [];
      }
      model.status = snapshot.status ?? null;
      model.snapshots++;
      return true;
    }

    case 'job.event': {
      const event = message.payload as { job: Job; eventType: string };
      if (!event?.job?.id) return false;
      // A new table each time (see jobTable): editing the old one would change nothing anyone follows.
      const jobs = jobTable(model.jobs);
      if (event.eventType === 'removed') {
        if (!(event.job.id in jobs)) return false;
        delete jobs[event.job.id];
      } else {
        jobs[event.job.id] = event.job;
      }
      model.jobs = jobs;
      return true;
    }

    case 'groups.changed':
      model.groups = (message.payload as DownloadGroup[]) ?? [];
      return true;

    // UI.R37: the server's update check found another latest version. Before the snapshot there is nothing to put it
    // on; the snapshot carries it.
    case 'update.latest': {
      const latest = (message.payload as { latestVersion?: string | null } | undefined)?.latestVersion ?? null;
      if (!model.status || model.status.latestVersion === latest) return false;
      model.status = { ...model.status, latestVersion: latest };
      return true;
    }

    // UPD.D1: the server update notice's dismiss state changed on the server (someone dismissed or skipped it).
    case 'update.notice': {
      const state = message.payload as { hiddenUntil?: string | null; skippedVersion?: string | null } | undefined;
      if (!model.status) return false;
      const hiddenUntil = state?.hiddenUntil ?? null;
      const skippedVersion = state?.skippedVersion ?? null;
      if (model.status.updateNoticeHiddenUntil === hiddenUntil && model.status.updateNoticeSkippedVersion === skippedVersion) return false;
      model.status = { ...model.status, updateNoticeHiddenUntil: hiddenUntil, updateNoticeSkippedVersion: skippedVersion };
      return true;
    }

    case 'settings.result': {
      const result = message.payload as { ok: boolean; settings?: Settings };
      if (!result?.settings) return false;
      model.settings = result.settings;
      if (result.settings.downloadGroups) model.groups = result.settings.downloadGroups;
      return true;
    }

    // Sent by a server that says when its settings change (audit 4 H1); an older one doesn't, and the page then reads
    // them before each save (Store.saveSettings). The settings come alone or as { settings }.
    case 'settings.changed': {
      const payload = message.payload as (Settings & { settings?: Settings }) | undefined;
      const settings = payload?.settings ?? (payload && 'queues' in payload ? payload : undefined);
      if (!settings) return false;
      model.settings = settings;
      if (settings.downloadGroups) model.groups = settings.downloadGroups;
      return true;
    }

    case 'remote.listResult': {
      const result = message.payload as { ok: boolean; jobs?: RemoteJob[] };
      if (!result?.ok) return false;
      const jobs: Record<string, RemoteJob> = {};
      for (const job of result.jobs ?? []) jobs[job.id] = job;
      model.remoteJobs = jobs;
      return true;
    }

    case 'remote.account.result': {
      const result = message.payload as { accounts?: RemoteAccount[] };
      if (!result?.accounts) return false;
      model.accounts = result.accounts;
      return true;
    }

    case 'remote.changed': {
      const change = message.payload as { change: string; job?: RemoteJob; jobId?: string; account?: RemoteAccount };
      if (change.change === 'job' && change.job) {
        model.remoteJobs[change.job.id] = change.job;
        return true;
      }
      if (change.change === 'removed' && change.jobId) {
        delete model.remoteJobs[change.jobId];
        return true;
      }
      if (change.change === 'account' && change.account) {
        const index = model.accounts.findIndex((account) => account.provider === change.account!.provider);
        if (index >= 0) model.accounts[index] = change.account;
        else model.accounts.push(change.account);
        return true;
      }
      return false;
    }

    case 'archive.listResult': {
      const result = message.payload as { sets?: ArchiveSet[] };
      const sets: Record<string, ArchiveSet> = {};
      for (const set of result?.sets ?? []) sets[set.id] = set;
      model.archives = sets;
      model.archivesAnswered = true;
      return true;
    }

    case 'archive.changed': {
      const change = message.payload as { set?: ArchiveSet; removedId?: string };
      if (change.set) {
        model.archives[change.set.id] = change.set;
        return true;
      }
      if (change.removedId) {
        delete model.archives[change.removedId];
        return true;
      }
      return false;
    }

    case 'sites.changed': {
      const change = message.payload as { status?: SitesStatus; sites?: Site[] };
      if (!change?.status) return false;
      model.sitesStatus = change.status;
      model.sites = change.sites ?? [];
      return true;
    }

    case 'sites.status':
      if (!message.payload) return false;
      model.sitesStatus = message.payload as SitesStatus;
      return true;

    case 'sites.listResult':
      model.sites = (message.payload as { sites?: Site[] })?.sites ?? [];
      return true;

    case 'sites.transfer.changed':
    case 'sites.transfer.listResult':
      model.siteTransfers = (message.payload as { transfers?: SiteTransfer[] })?.transfers ?? [];
      return true;

    case 'host.exiting':
      model.exiting = true;
      return true;

    default:
      return false;
  }
}
