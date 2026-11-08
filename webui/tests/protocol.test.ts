import { describe, expect, it } from 'vitest';
import { ConnectionLostError, HttpStatusError, ProtocolClient, backoffDelay, envelope, postEnvelope, type ConnectionState, type Envelope } from '../src/lib/protocol';
import { FakeSocket, FakeTimers, settle } from './fakes';

function setup(signedOut: () => Promise<boolean> = async () => false) {
  const sockets: FakeSocket[] = [];
  const timers = new FakeTimers();
  const states: ConnectionState[] = [];
  const messages: Envelope[] = [];
  const client = new ProtocolClient({
    url: 'ws://server/api/ws',
    createSocket: (url) => {
      const socket = new FakeSocket(url);
      sockets.push(socket);
      return socket;
    },
    onMessage: (message) => messages.push(message),
    onState: (state) => states.push(state),
    isSignedOut: signedOut,
    timers,
    requestTimeoutMs: 1000,
  });
  return { client, sockets, timers, states, messages };
}

describe('backoffDelay', () => {
  it('doubles from half a second up to fifteen', () => {
    expect([0, 1, 2, 3, 4, 5, 6, 10].map((n) => backoffDelay(n, () => 0))).toEqual([500, 1000, 2000, 4000, 8000, 15000, 15000, 15000]);
  });

  it('adds at most a fifth at random', () => {
    expect(backoffDelay(0, () => 0.999)).toBeLessThanOrEqual(600);
    expect(backoffDelay(5, () => 1)).toBe(18000);
  });
});

describe('envelope', () => {
  it('carries the protocol version and a fresh id', () => {
    const a = envelope('jobs.list', {});
    const b = envelope('jobs.list', {});
    expect(a.v).toBe(1);
    expect(a.id).not.toBe(b.id);
    expect(a.id.length).toBeGreaterThanOrEqual(16);
  });
});

describe('ProtocolClient', () => {
  it('is open only after the hello, and passes the hello and snapshot on', () => {
    const { client, sockets, states, messages } = setup();
    client.connect();
    expect(sockets).toHaveLength(1);
    expect(sockets[0].url).toBe('ws://server/api/ws');
    expect(client.connected).toBe(false);
    sockets[0].receive({ type: 'server.hello', payload: { protocolVersion: 1, capabilities: [] } });
    sockets[0].receive({ type: 'jobs.snapshot', payload: { jobs: [] } });
    expect(client.connected).toBe(true);
    expect(states).toEqual(['connecting', 'open']);
    expect(messages.map((m) => m.type)).toEqual(['server.hello', 'jobs.snapshot']);
  });

  it('matches a reply to its request by replyTo, and keeps events apart', async () => {
    const { client, sockets, messages } = setup();
    client.connect();
    sockets[0].receive({ type: 'server.hello', payload: {} });
    const reply = client.request('settings.get', {});
    const sent = sockets[0].sent[0];
    expect(sent.type).toBe('settings.get');
    expect(sent.v).toBe(1);
    sockets[0].receive({ type: 'job.event', payload: { eventType: 'progress' } });
    sockets[0].receive({ type: 'settings.result', replyTo: sent.id, payload: { ok: true } });
    await expect(reply).resolves.toMatchObject({ type: 'settings.result', payload: { ok: true } });
    expect(messages.map((m) => m.type)).toEqual(['server.hello', 'job.event']);
  });

  it('refuses a request while not connected', async () => {
    const { client } = setup();
    client.connect();
    await expect(client.request('jobs.list')).rejects.toBeInstanceOf(ConnectionLostError);
  });

  it('times a request out', async () => {
    const { client, sockets, timers } = setup();
    client.connect();
    sockets[0].receive({ type: 'server.hello', payload: {} });
    const reply = client.request('jobs.list');
    timers.advance(1001);
    await expect(reply).rejects.toThrow(/didn't answer jobs.list/);
  });

  it('fails waiting requests when the socket drops, then reconnects with growing waits', async () => {
    const { client, sockets, timers, states } = setup();
    client.connect();
    sockets[0].receive({ type: 'server.hello', payload: {} });
    const reply = client.request('jobs.list');
    sockets[0].drop();
    await expect(reply).rejects.toBeInstanceOf(ConnectionLostError);
    await settle();
    expect(states.at(-1)).toBe('reconnecting');

    timers.advance(499);
    expect(sockets).toHaveLength(1);
    timers.advance(1);
    expect(sockets).toHaveLength(2);

    // Refused again before a hello: the next wait doubles.
    sockets[1].drop();
    await settle();
    timers.advance(999);
    expect(sockets).toHaveLength(2);
    timers.advance(1);
    expect(sockets).toHaveLength(3);

    // A hello starts the waits over.
    sockets[2].receive({ type: 'server.hello', payload: {} });
    sockets[2].drop();
    await settle();
    timers.advance(500);
    expect(sockets).toHaveLength(4);
  });

  it('stops trying and says so when the session is gone', async () => {
    const { client, sockets, timers, states } = setup(async () => true);
    client.connect();
    sockets[0].drop(1008);
    await settle();
    expect(states.at(-1)).toBe('signedOut');
    timers.advance(60_000);
    expect(sockets).toHaveLength(1);
    expect(client.state).toBe('signedOut');
  });

  it('keeps trying when the sign-in check itself fails', async () => {
    const { client, sockets, timers } = setup(async () => {
      throw new Error('offline');
    });
    client.connect();
    sockets[0].drop();
    await settle();
    timers.advance(500);
    expect(sockets).toHaveLength(2);
  });

  it('stops for good on server.incompatible', async () => {
    const { client, sockets, timers, states } = setup();
    client.connect();
    sockets[0].receive({ type: 'server.incompatible', payload: { message: 'Too old.' } });
    expect(states.at(-1)).toBe('incompatible');
    expect(sockets[0].closed).toBe(true);
    sockets[0].drop(1000);
    await settle();
    timers.advance(60_000);
    expect(sockets).toHaveLength(1);
  });

  it('ignores what is not an envelope', () => {
    const { client, sockets, messages } = setup();
    client.connect();
    sockets[0].onmessage?.({ data: 'not json' });
    sockets[0].onmessage?.({ data: JSON.stringify({ nothing: true }) });
    sockets[0].onmessage?.({ data: new ArrayBuffer(4) });
    expect(messages).toHaveLength(0);
  });

  it('close() stops retries and fails what waits', async () => {
    const { client, sockets, timers, states } = setup();
    client.connect();
    sockets[0].receive({ type: 'server.hello', payload: {} });
    const reply = client.request('jobs.list');
    client.close();
    await expect(reply).rejects.toBeInstanceOf(ConnectionLostError);
    expect(states.at(-1)).toBe('closed');
    timers.advance(60_000);
    expect(sockets).toHaveLength(1);
  });
});

describe('postEnvelope', () => {
  it('posts one envelope and returns the reply', async () => {
    const seen: { input?: string; body?: string; method?: string } = {};
    const reply = await postEnvelope('remote.classify', { text: 'magnet:?xt=' }, async (input, init) => {
      seen.input = input;
      seen.body = init?.body;
      seen.method = init?.method;
      return { status: 200, ok: true, json: async () => ({ v: 1, type: 'remote.classifyResult', id: 'x', replyTo: 'y', payload: { ok: true } }) };
    });
    expect(seen.input).toBe('/api/ipc');
    expect(seen.method).toBe('POST');
    expect(JSON.parse(seen.body!)).toMatchObject({ v: 1, type: 'remote.classify', payload: { text: 'magnet:?xt=' } });
    expect(reply.type).toBe('remote.classifyResult');
  });

  it('turns 401 into a signed-out error and other failures into the server words', async () => {
    await expect(postEnvelope('x', {}, async () => ({ status: 401, ok: false, json: async () => ({ ok: false, error: 'Sign in first.' }) })))
      .rejects.toMatchObject({ status: 401 });
    const error = await postEnvelope('x', {}, async () => ({ status: 403, ok: false, json: async () => ({ error: 'Not for this browser.' }) })).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(HttpStatusError);
    expect((error as Error).message).toBe('Not for this browser.');
  });
});
