// The web UI's one store: the model, kept live from /api/ws, and the calls the screens make.

import { apply, emptyModel, hasCapability, type Model } from './model';
import { HttpStatusError, ProtocolClient, postEnvelope, type ConnectionState, type Envelope } from './protocol';
import { notifyEnabled } from './notify';
import { noticeFor } from './details';
import type { Job, Settings } from './types';

export type Theme = 'system' | 'dark' | 'light';

export interface Toast {
  id: number;
  text: string;
  tone: 'info' | 'error';
  /** A download the toast is about; the toast then offers "Show". */
  jobId?: string;
}

function readTheme(): Theme {
  try {
    const value = localStorage.getItem('inbound.theme');
    return value === 'dark' || value === 'light' ? value : 'system';
  } catch {
    return 'system';
  }
}

/** Calls that wait on the network go through POST /api/ipc, so they don't hold up the WebSocket's queue. */
const slowTypes = new Set([
  'remote.account.get', 'remote.account.validate', 'remote.classify', 'remote.hosters', 'remote.add', 'remote.item.get',
  'remote.files', 'remote.select', 'remote.deleteRemote', 'remote.startQueued', 'remote.container.preview',
  'remote.container.add', 'job.refreshUrl', 'file.metadata',
]);

export class Store {
  model = $state<Model>(emptyModel());
  connection = $state<ConnectionState>('connecting');
  connectionDetail = $state<string | undefined>(undefined);
  theme = $state<Theme>(readTheme());
  toasts = $state<Toast[]>([]);
  signInRequired = $state(true);
  /** A download the list should select and scroll to (set after an add or "Select existing"; the list clears it). */
  revealId = $state<string | null>(null);
  /** A download the list screen should open (from a toast's Show). */
  showJob = $state<string | null>(null);
  /** Counts every saved or cleared provider key, so screens drop what they read with the old key. */
  keyEpoch = $state(0);
  /** The Settings page to open next (a provider's account page, from My provider). */
  settingsPage = $state<string | null>(null);
  private client: ProtocolClient | null = null;
  private toastId = 0;

  start(): void {
    this.applyTheme();
    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
    this.client = new ProtocolClient({
      url: `${scheme}//${location.host}/api/ws`,
      onMessage: (message) => this.receive(message),
      onState: (state, detail) => {
        this.connection = state;
        this.connectionDetail = detail;
        if (state === 'signedOut') location.assign('/signin/');
      },
      isSignedOut: async () => {
        const response = await fetch('/api/auth/status', { credentials: 'same-origin' });
        const status = (await response.json()) as { signInRequired: boolean; signedIn: boolean };
        this.signInRequired = status.signInRequired;
        return status.signInRequired && !status.signedIn;
      },
    });
    this.client.connect();
    void fetch('/api/auth/status', { credentials: 'same-origin' })
      .then((response) => response.json())
      .then((status: { signInRequired: boolean }) => (this.signInRequired = status.signInRequired))
      .catch(() => undefined);
  }

  private receive(message: Envelope): void {
    const event = message.type === 'job.event' ? (message.payload as { job?: Job; eventType?: string } | undefined) : undefined;
    const before = event?.job?.id ? this.model.jobs[event.job.id]?.state : undefined;
    apply(this.model, message);
    if (event?.job && event.eventType !== 'removed') this.announce(before, event.job);
    if (message.type === 'jobs.snapshot') void this.afterSnapshot();
  }

  /** D10: a download that finished or failed gets a toast, and a system notice while this tab is hidden. */
  private announce(before: string | undefined, job: Job): void {
    const notice = noticeFor(before, job, this.model.settings?.notifyOnCompletion !== false);
    if (!notice) return;
    this.toast(notice.text, notice.tone, notice.jobId);
    try {
      // The browser's own notice only when this browser was asked for it in Options (notify.ts).
      if (document.visibilityState === 'hidden' && notifyEnabled()) {
        new Notification(notice.tone === 'error' ? 'Download failed' : 'Download complete', { body: notice.text, tag: `inbound-${job.id}` });
      }
    } catch {
      // Notifications can be blocked; the toast was shown.
    }
  }

  /** Asks once for the right to show system notices; called from a click, which browsers require. */
  askForNotices(): void {
    try {
      if (typeof Notification !== 'undefined' && Notification.permission === 'default' && this.model.settings?.notifyOnCompletion !== false) void Notification.requestPermission();
    } catch {
      // Not offered here.
    }
  }

  /** What isn't in the snapshot: the provider's work and account, and the archive sets. */
  private async afterSnapshot(): Promise<void> {
    if (hasCapability(this.model, 'remote')) {
      await this.quiet('remote.list', {});
      await this.quiet('remote.account.get', {});
    }
    await this.quiet('archive.list', {});
  }

  private async quiet(type: string, payload: unknown): Promise<void> {
    try {
      apply(this.model, await this.raw(type, payload));
    } catch {
      // Not offered here, or the connection dropped; the next snapshot tries again.
    }
  }

  /** One request; the reply envelope. Replies that carry state also update the model. */
  async raw<R = unknown>(type: string, payload: unknown = {}): Promise<Envelope<R>> {
    let reply: Envelope<R>;
    if (slowTypes.has(type) || !this.client?.connected) {
      try {
        reply = await postEnvelope<unknown, R>(type, payload);
      } catch (error) {
        if (error instanceof HttpStatusError && error.status === 401) location.assign('/signin/');
        throw error;
      }
    } else {
      reply = await this.client.request<unknown, R>(type, payload);
    }
    apply(this.model, reply);
    return reply;
  }

  /** One request; its payload. A reply that says it failed becomes an error with the server's words. */
  async ask<R = Record<string, unknown>>(type: string, payload: unknown = {}): Promise<R> {
    const reply = await this.raw<R>(type, payload);
    const body = reply.payload as Record<string, unknown> | undefined;
    if (reply.type === 'host.actionResult' && body && body.ok === false) throw new Error(String(body.message ?? 'The server refused this.'));
    if (body && body.ok === false && typeof body.error === 'string' && body.error) throw new Error(body.error);
    if (body && typeof body.error === 'string' && body.error && body.ok === undefined) throw new Error(body.error);
    return reply.payload as R;
  }

  /** Like ask, but shows a failure as a toast and returns null. */
  async attempt<R = Record<string, unknown>>(type: string, payload: unknown = {}, done?: string): Promise<R | null> {
    try {
      const result = await this.ask<R>(type, payload);
      if (done) this.toast(done);
      return result;
    } catch (error) {
      this.toast(error instanceof Error ? error.message : String(error), 'error');
      return null;
    }
  }

  /** Saves the whole settings object with the given changes; the server answers with what it kept. */
  async saveSettings(change: (settings: Settings) => void, done = 'Settings saved.'): Promise<boolean> {
    if (!this.model.settings) return false;
    const copy = JSON.parse(JSON.stringify(this.model.settings)) as Settings;
    change(copy);
    const result = await this.attempt<{ ok: boolean; error?: string }>('settings.update', { settings: copy }, done);
    return !!result?.ok;
  }

  has(capability: string): boolean {
    return hasCapability(this.model, capability);
  }

  toast(text: string, tone: Toast['tone'] = 'info', jobId?: string): void {
    const id = ++this.toastId;
    this.toasts.push({ id, text, tone, jobId });
    setTimeout(() => this.dismiss(id), tone === 'error' ? 8000 : 4000);
  }

  dismiss(id: number): void {
    this.toasts = this.toasts.filter((toast) => toast.id !== id);
  }

  setTheme(theme: Theme): void {
    this.theme = theme;
    try {
      if (theme === 'system') localStorage.removeItem('inbound.theme');
      else localStorage.setItem('inbound.theme', theme);
    } catch {
      // A private window: the choice lasts until the page closes.
    }
    this.applyTheme();
  }

  private applyTheme(): void {
    if (this.theme === 'system') delete document.documentElement.dataset.theme;
    else document.documentElement.dataset.theme = this.theme;
  }

  async signOut(): Promise<void> {
    try {
      await fetch('/api/auth/signout', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: '{}' });
    } finally {
      this.client?.close();
      location.assign('/signin/');
    }
  }
}

export const store = new Store();
