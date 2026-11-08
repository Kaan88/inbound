// The server's protocol (docs/ipc-protocol.md): JSON envelopes with a protocol version, one per WebSocket text
// message on /api/ws (hello and snapshot first, then replies and events), or one request and one reply on
// POST /api/ipc. Nothing here touches the DOM, so the tests run it with a fake socket.

export const PROTOCOL_VERSION = 1;

export interface Envelope<T = unknown> {
  v: number;
  type: string;
  id: string;
  replyTo?: string;
  payload?: T;
}

export type ConnectionState = 'connecting' | 'open' | 'reconnecting' | 'signedOut' | 'incompatible' | 'closed';

/** The part of a browser WebSocket this client uses, so a test can stand in for it. */
export interface SocketLike {
  readonly readyState: number;
  onopen: ((event: unknown) => void) | null;
  onmessage: ((event: { data: unknown }) => void) | null;
  onclose: ((event: { code: number; reason?: string }) => void) | null;
  onerror: ((event: unknown) => void) | null;
  send(data: string): void;
  close(code?: number, reason?: string): void;
}

export interface Timers {
  setTimeout(handler: () => void, ms: number): unknown;
  clearTimeout(handle: unknown): void;
  random(): number;
}

const defaultTimers: Timers = {
  setTimeout: (handler, ms) => globalThis.setTimeout(handler, ms),
  clearTimeout: (handle) => globalThis.clearTimeout(handle as ReturnType<typeof setTimeout>),
  random: () => Math.random(),
};

export interface ClientOptions {
  url: string;
  createSocket?: (url: string) => SocketLike;
  /** Every envelope that isn't the reply to a request made here (hello, snapshot, events). */
  onMessage: (message: Envelope) => void;
  onState?: (state: ConnectionState, detail?: string) => void;
  /**
   * Asked after the socket closes before a hello came, or after it dropped: true when the session is gone, so the
   * client stops instead of retrying (the page then goes to sign-in).
   */
  isSignedOut?: () => Promise<boolean>;
  timers?: Timers;
  requestTimeoutMs?: number;
}

/** The waits between tries: 0.5 s, 1, 2, 4, 8, then 15 s at most, each with up to a fifth more at random. */
export function backoffDelay(attempt: number, random: () => number = Math.random): number {
  const base = Math.min(15_000, 500 * 2 ** Math.max(0, attempt));
  return Math.round(base * (1 + random() * 0.2));
}

let counter = 0;
export function newId(): string {
  counter = (counter + 1) % 1_000_000;
  const random = globalThis.crypto?.randomUUID?.().replace(/-/g, '') ?? Math.random().toString(16).slice(2);
  return `${random.slice(0, 24)}${counter.toString(16).padStart(6, '0')}`;
}

export function envelope<T>(type: string, payload?: T): Envelope<T> {
  return { v: PROTOCOL_VERSION, type, id: newId(), payload };
}

interface Pending {
  resolve: (message: Envelope) => void;
  reject: (error: Error) => void;
  timer: unknown;
}

export class ConnectionLostError extends Error {
  constructor(message = 'The connection to the server was lost.') {
    super(message);
    this.name = 'ConnectionLostError';
  }
}

/**
 * One WebSocket to the server, reconnecting with growing waits. A request is answered by the envelope whose
 * replyTo is its id; the server answers a connection's requests in order, one at a time, so slow provider calls
 * should go through {@link postEnvelope} instead.
 */
export class ProtocolClient {
  private socket: SocketLike | null = null;
  private readonly pending = new Map<string, Pending>();
  private readonly timers: Timers;
  private attempt = 0;
  private retryTimer: unknown = null;
  private stopped = false;
  private helloSeen = false;
  state: ConnectionState = 'closed';

  constructor(private readonly options: ClientOptions) {
    this.timers = options.timers ?? defaultTimers;
  }

  connect(): void {
    this.stopped = false;
    this.open();
  }

  /** Stops for good: no more tries, and every waiting request fails. */
  close(): void {
    this.stopped = true;
    if (this.retryTimer !== null) this.timers.clearTimeout(this.retryTimer);
    this.retryTimer = null;
    const socket = this.socket;
    this.socket = null;
    socket?.close(1000, 'closed');
    this.failPending(new ConnectionLostError('Closed.'));
    this.setState('closed');
  }

  get connected(): boolean {
    return this.state === 'open';
  }

  /** Sends one request and waits for its reply. */
  request<T = unknown, R = unknown>(type: string, payload?: T, timeoutMs?: number): Promise<Envelope<R>> {
    const socket = this.socket;
    if (!socket || this.state !== 'open') return Promise.reject(new ConnectionLostError('Not connected to the server.'));
    const message = envelope(type, payload);
    return new Promise<Envelope<R>>((resolve, reject) => {
      const timer = this.timers.setTimeout(() => {
        this.pending.delete(message.id);
        reject(new Error(`The server didn't answer ${type} in time.`));
      }, timeoutMs ?? this.options.requestTimeoutMs ?? 60_000);
      this.pending.set(message.id, { resolve: resolve as (m: Envelope) => void, reject, timer });
      try {
        socket.send(JSON.stringify(message));
      } catch (error) {
        this.timers.clearTimeout(timer);
        this.pending.delete(message.id);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  private open(): void {
    if (this.stopped) return;
    this.helloSeen = false;
    this.setState(this.attempt === 0 ? 'connecting' : 'reconnecting');
    const create = this.options.createSocket ?? ((url: string) => new WebSocket(url) as unknown as SocketLike);
    let socket: SocketLike;
    try {
      socket = create(this.options.url);
    } catch {
      this.scheduleRetry();
      return;
    }

    this.socket = socket;
    socket.onmessage = (event) => this.receive(event.data);
    socket.onclose = () => {
      if (this.socket !== socket) return;
      this.socket = null;
      this.failPending(new ConnectionLostError());
      void this.afterClose();
    };
    socket.onerror = () => {
      // A close always follows; that is where it is handled.
    };
  }

  private receive(data: unknown): void {
    if (typeof data !== 'string') return;
    let message: Envelope;
    try {
      message = JSON.parse(data) as Envelope;
    } catch {
      return;
    }

    if (!message || typeof message.type !== 'string') return;
    if (message.type === 'server.incompatible') {
      this.stopped = true;
      this.setState('incompatible', (message.payload as { message?: string } | undefined)?.message);
      this.socket?.close(1000, 'incompatible');
      return;
    }

    if (message.type === 'server.hello') {
      this.helloSeen = true;
      this.attempt = 0;
      this.setState('open');
    }

    if (message.replyTo && this.pending.has(message.replyTo)) {
      const waiting = this.pending.get(message.replyTo)!;
      this.pending.delete(message.replyTo);
      this.timers.clearTimeout(waiting.timer);
      waiting.resolve(message);
      return;
    }

    this.options.onMessage(message);
  }

  private async afterClose(): Promise<void> {
    if (this.stopped) return;
    if (this.options.isSignedOut) {
      let signedOut = false;
      try {
        signedOut = await this.options.isSignedOut();
      } catch {
        signedOut = false;
      }

      if (signedOut) {
        this.stopped = true;
        this.setState('signedOut');
        return;
      }
    }

    this.scheduleRetry();
  }

  private scheduleRetry(): void {
    if (this.stopped) return;
    const delay = backoffDelay(this.attempt, this.timers.random);
    this.attempt++;
    this.setState('reconnecting', `${Math.round(delay / 100) / 10}`);
    this.retryTimer = this.timers.setTimeout(() => {
      this.retryTimer = null;
      this.open();
    }, delay);
  }

  private failPending(error: Error): void {
    for (const [id, waiting] of this.pending) {
      this.timers.clearTimeout(waiting.timer);
      waiting.reject(error);
      this.pending.delete(id);
    }
  }

  private setState(state: ConnectionState, detail?: string): void {
    this.state = state;
    this.options.onState?.(state, detail);
  }

  /** For tests: whether the last connection got its hello. */
  get sawHello(): boolean {
    return this.helloSeen;
  }
}

export interface FetchLike {
  (input: string, init?: { method?: string; headers?: Record<string, string>; body?: string; credentials?: 'same-origin' }): Promise<{
    status: number;
    ok: boolean;
    json(): Promise<unknown>;
  }>;
}

export class HttpStatusError extends Error {
  constructor(readonly status: number, message: string) {
    super(message);
    this.name = 'HttpStatusError';
  }
}

/** One request on POST /api/ipc: for calls that wait on the network, which would hold up the WebSocket's queue. */
export async function postEnvelope<T, R>(type: string, payload: T, fetcher: FetchLike = fetch as unknown as FetchLike): Promise<Envelope<R>> {
  const message = envelope(type, payload);
  const response = await fetcher('/api/ipc', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(message),
    credentials: 'same-origin',
  });
  const body = (await response.json().catch(() => null)) as Envelope<R> | { error?: string } | null;
  if (response.status === 401) throw new HttpStatusError(401, 'Signed out. Sign in again.');
  if (!response.ok || !body || !('type' in body)) {
    throw new HttpStatusError(response.status, (body as { error?: string } | null)?.error ?? `The server answered ${response.status}.`);
  }

  return body;
}
