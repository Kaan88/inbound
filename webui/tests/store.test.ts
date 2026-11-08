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

  it('saves the whole settings object with the change, and keeps what the server answers', async () => {
    const requests = serve((request) => reply(request, 'settings.result', { ok: true, settings: { ...(request.payload as { settings: object }).settings, maxConcurrentDownloads: 3 } }));
    const store = new Store();
    store.model.settings = { defaultDownloadDirectory: '/data', maxConcurrentDownloads: 2, downloadGroups: [] } as unknown as Settings;
    expect(await store.saveSettings((s) => (s.defaultDownloadDirectory = '/other'))).toBe(true);
    expect(requests[0].type).toBe('settings.update');
    expect(requests[0].payload).toEqual({ settings: { defaultDownloadDirectory: '/other', maxConcurrentDownloads: 2, downloadGroups: [] } });
    expect(store.model.settings?.maxConcurrentDownloads).toBe(3);
  });

  it('goes to the sign-in page when the server answers 401', async () => {
    serve(() => ({ status: 401, body: { ok: false, error: 'Sign in first.' } }));
    const store = new Store();
    await expect(store.raw('jobs.list', {})).rejects.toMatchObject({ status: 401 });
    expect((globalThis.location as unknown as { assign: ReturnType<typeof vi.fn> }).assign).toHaveBeenCalledWith('/signin/');
  });
});
