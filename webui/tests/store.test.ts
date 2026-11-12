import { afterEach, describe, expect, it, vi } from 'vitest';
import { Store } from '../src/lib/store.svelte';
import type { Envelope } from '../src/lib/protocol';
import type { Settings } from '../src/lib/types';

// The store before start(): no socket, so every request goes through POST /api/ipc, which these tests stand in for.
function serve(answer: (request: Envelope) => { status?: number; body: unknown }) {
  const requests: Envelope[] = [];
  vi.stubGlobal('fetch', async (_input: string, init: { body: string }) => {
    const request = JSON.parse(init.body) as Envelope;
    requests.push(request);
    const { status = 200, body } = answer(request);
    return { status, ok: status < 400, json: async () => body };
  });
  vi.stubGlobal('location', { assign: vi.fn(), protocol: 'http:', host: 'server' });
  return requests;
}

function reply(request: Envelope, type: string, payload: unknown): { body: Envelope } {
  return { body: { v: 1, type, id: 'r', replyTo: request.id, payload } };
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('Store', () => {
  it('turns a refusal into an error with the server words', async () => {
    serve((request) => reply(request, 'host.actionResult', { ok: false, message: 'That queue is running.' }));
    const store = new Store();
    await expect(store.ask('queue.action', {})).rejects.toThrow('That queue is running.');
    serve((request) => reply(request, 'remote.addResult', { ok: false, error: 'No key.' }));
    await expect(store.ask('remote.add', {})).rejects.toThrow('No key.');
  });

  it('shows a failure as a toast through attempt()', async () => {
    vi.useFakeTimers();
    serve((request) => reply(request, 'remote.addResult', { ok: false, error: 'No key.' }));
    const store = new Store();
    expect(await store.attempt('remote.add', {})).toBeNull();
    expect(store.toasts.map((t) => [t.text, t.tone])).toEqual([['No key.', 'error']]);
    vi.advanceTimersByTime(8000);
    expect(store.toasts).toHaveLength(0);
  });

  // A server that holds its own settings: settings.get answers with them, settings.update keeps what it was sent and
  // answers with what it kept (the limit at most 4). Never an echo of the request, which would hide a stale copy.
  function settingsServer(held: Record<string, unknown>) {
    const server = { held };
    const requests = serve((request) => {
      if (request.type === 'settings.update') {
        const sent = (request.payload as { settings: Record<string, unknown> }).settings;
        server.held = { ...sent, maxConcurrentDownloads: Math.min(Number(sent.maxConcurrentDownloads), 4) };
      }
      return reply(request, 'settings.result', { ok: true, settings: server.held });
    });
    return { server, requests };
  }

  it('saves the change onto the settings the server holds now, and keeps what the server answers', async () => {
    // Since this page read them: the server stopped the queue at its quota, and the desktop added a schedule.
    const { server, requests } = settingsServer({
      defaultDownloadDirectory: '/data', maxConcurrentDownloads: 2, downloadGroups: [],
      queues: [{ id: 0, name: 'Main', isRunning: false, quotaBytesUsed: 500 }],
      schedules: [{ id: 'added-elsewhere', queueId: 0 }],
    });
    const store = new Store();
    store.model.settings = {
      defaultDownloadDirectory: '/data', maxConcurrentDownloads: 2, downloadGroups: [],
      queues: [{ id: 0, name: 'Main', isRunning: true, quotaBytesUsed: 0 }],
      schedules: [],
    } as unknown as Settings;

    expect(await store.saveSettings((s) => { s.defaultDownloadDirectory = '/other'; s.maxConcurrentDownloads = 9; })).toBe(true);

    expect(requests.map((r) => r.type)).toEqual(['settings.get', 'settings.update']);
    const sent = (requests[1].payload as { settings: Record<string, unknown> }).settings;
    expect(sent.defaultDownloadDirectory).toBe('/other');
    expect(sent.queues).toEqual([{ id: 0, name: 'Main', isRunning: false, quotaBytesUsed: 500 }]);
    expect(sent.schedules).toEqual([{ id: 'added-elsewhere', queueId: 0 }]);
    // What the server kept, not what was sent.
    expect(store.model.settings?.maxConcurrentDownloads).toBe(4);
    expect(server.held.schedules).toEqual([{ id: 'added-elsewhere', queueId: 0 }]);
  });

  it('sends nothing when the current settings cannot be read', async () => {
    vi.useFakeTimers();
    const requests = serve((request) => reply(request, 'host.actionResult', { ok: false, message: 'Not now.' }));
    const store = new Store();
    store.model.settings = { defaultDownloadDirectory: '/data', downloadGroups: [] } as unknown as Settings;
    expect(await store.saveSettings((s) => (s.defaultDownloadDirectory = '/other'))).toBe(false);
    expect(requests.map((r) => r.type)).toEqual(['settings.get']);
    expect(store.toasts.map((t) => t.text)).toEqual(['Not now.']);
  });

  it('goes to the sign-in page when the server answers 401', async () => {
    serve(() => ({ status: 401, body: { ok: false, error: 'Sign in first.' } }));
    const store = new Store();
    await expect(store.raw('jobs.list', {})).rejects.toMatchObject({ status: 401 });
    expect((globalThis.location as unknown as { assign: ReturnType<typeof vi.fn> }).assign).toHaveBeenCalledWith('/signin/');
  });
});
