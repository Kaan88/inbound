// What the web UI knows about the server, kept up to date from envelopes. A plain function of (model, message), so
// the tests feed it envelopes; the Svelte store holds one of these in reactive state.

import type { Envelope } from './protocol';
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
  /** Grows with each snapshot, so views can tell a fresh start from an update. */
  snapshots: number;
  exiting: boolean;
}

export function emptyModel(): Model {
  return {
    hello: null,
    jobs: {},
    settings: null,
    status: null,
    groups: [],
    remoteJobs: {},
    accounts: [],
    archives: {},
    snapshots: 0,
    exiting: false,
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
      const jobs: Record<string, Job> = {};
      for (const job of snapshot.jobs ?? []) jobs[job.id] = job;
      model.jobs = jobs;
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
      if (event.eventType === 'removed') {
        if (!(event.job.id in model.jobs)) return false;
        delete model.jobs[event.job.id];
      } else {
        model.jobs[event.job.id] = event.job;
      }
      return true;
    }

    case 'groups.changed':
      model.groups = (message.payload as DownloadGroup[]) ?? [];
      return true;

    case 'settings.result': {
      const result = message.payload as { ok: boolean; settings?: Settings };
      if (!result?.settings) return false;
      model.settings = result.settings;
      if (result.settings.downloadGroups) model.groups = result.settings.downloadGroups;
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

    case 'host.exiting':
      model.exiting = true;
      return true;

    default:
      return false;
  }
}
