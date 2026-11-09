/// <reference types="chrome" />

// How the extension reaches a download manager. Two targets carry the same protocol envelopes
// (docs/ipc-protocol.md):
// - "local": the desktop app on this PC, through native messaging (NativeTransport).
// - "server": the user's own Inbound server (Docker or Linux), through POST /api/ipc with the token
//   this browser got when it was paired (ServerTransport).
// Nothing here decides what to send; background.ts does, and on any failure leaves the download in
// the browser, whichever target it was meant for.

export type TargetId = "local" | "server";

export const TARGET_LABELS: Readonly<Record<TargetId, string>> = { local: "This PC", server: "Server" };

export interface Envelope {
  v: number;
  type: string;
  id?: string;
  replyTo?: string;
  payload?: unknown;
}

export const PROTOCOL_VERSION = 1;

/** Reply types that mean the request failed, whatever the payload says. */
const ERROR_TYPES = new Set(["handoff.error", "server.incompatible"]);

export function extractError(payload: unknown): string {
  if (payload && typeof payload === "object") {
    const record = payload as Record<string, unknown>;
    if (typeof record.error === "string") {
      return record.error;
    }
    if (typeof record.message === "string") {
      return record.message;
    }
  }
  return "The native host reported an error.";
}

/** One way to the Host. */
export interface Transport {
  readonly target: TargetId;
  /** Whether the last exchange got an answer. */
  readonly connected: boolean;
  readonly lastError: string;
  /** One request; resolves with the reply's payload, rejects on no answer or an error reply. */
  send(type: string, payload: unknown, timeoutMs?: number, retries?: number): Promise<any>;
  /**
   * One request on a connection of its own, for anything that may take long (a folder picker, a delete):
   * resolves with the whole reply envelope, whatever its type.
   */
  request(type: string, payload: unknown, timeoutMs: number, timeoutMessage: string): Promise<Envelope>;
}

// ---- This PC: native messaging -------------------------------------------------------------------

type NativeRuntime = Pick<typeof chrome.runtime, "connectNative" | "lastError">;

/**
 * The desktop app through the native-messaging host. One shared connection for quick requests and
 * the app's events, opened when first needed, plus one-off connections (see request).
 */
export class NativeTransport implements Transport {
  readonly target = "local" as const;
  lastError = "";
  private port: chrome.runtime.Port | null = null;
  private readonly pending = new Map<string, {
    resolve: (payload: any) => void;
    reject: (error: Error) => void;
  }>();

  constructor(
    private readonly hostName: string,
    /** Messages on the shared connection that answer no request (job.event and the like). */
    private readonly onEvent: (message: Envelope) => void = () => undefined,
    private readonly runtime: NativeRuntime = chrome.runtime,
  ) {}

  get connected(): boolean {
    return this.port !== null;
  }

  async send(type: string, payload: unknown, timeoutMs = 12000, retries = 1): Promise<any> {
    let lastError: Error | null = null;
    for (let attempt = 0; attempt <= retries; attempt++) {
      try {
        return await this.sendOnce(type, payload, timeoutMs);
      } catch (error) {
        lastError = error instanceof Error ? error : new Error(String(error));
        if (attempt < retries) {
          this.disconnect();
          await new Promise((resolve) => setTimeout(resolve, 300));
        }
      }
    }

    throw lastError ?? new Error("Could not reach the desktop app.");
  }

  private sendOnce(type: string, payload: unknown, timeoutMs: number): Promise<any> {
    const port = this.ensurePort();
    const id = crypto.randomUUID();
    const envelope: Envelope = { v: PROTOCOL_VERSION, type, id, payload };

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error("Timed out waiting for the desktop app."));
      }, timeoutMs);

      this.pending.set(id, {
        resolve: (result) => { clearTimeout(timer); resolve(result); },
        reject: (error) => { clearTimeout(timer); reject(error); },
      });

      try {
        port.postMessage(envelope);
      } catch (error) {
        this.pending.delete(id);
        clearTimeout(timer);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  private ensurePort(): chrome.runtime.Port {
    if (this.port) {
      return this.port;
    }

    const port = this.runtime.connectNative(this.hostName);
    port.onMessage.addListener((message: Envelope) => this.received(message));
    port.onDisconnect.addListener(() => {
      const message = this.runtime.lastError?.message ?? "Native host disconnected.";
      this.lastError = message;
      this.port = null;
      for (const request of this.pending.values()) request.reject(new Error(message));
      this.pending.clear();
    });
    this.port = port;
    this.lastError = "";
    return port;
  }

  private received(message: Envelope): void {
    if (message.replyTo) {
      const request = this.pending.get(message.replyTo);
      if (request) {
        this.pending.delete(message.replyTo);
        if (ERROR_TYPES.has(message.type)) {
          request.reject(new Error(extractError(message.payload)));
        } else {
          request.resolve(message.payload);
        }
        return;
      }
    }

    this.onEvent(message);
  }

  /**
   * The Host answers one command at a time per connection, so anything that may take long goes on a
   * connection of its own, closed after the answer: on the shared one it would hold up an automatic
   * handoff past its time limit, the browser would keep its copy, and the app could still take the queued one.
   */
  request(type: string, payload: unknown, timeoutMs: number, timeoutMessage: string): Promise<Envelope> {
    const port = this.runtime.connectNative(this.hostName);
    const id = crypto.randomUUID();
    return new Promise((resolve, reject) => {
      let finished = false;
      const finish = (result: Envelope | null, error?: Error): void => {
        if (finished) return;
        finished = true;
        clearTimeout(timer);
        try { port.disconnect(); } catch { /* already closed */ }
        if (error || !result) reject(error ?? new Error(timeoutMessage)); else resolve(result);
      };
      const timer = setTimeout(() => finish(null, new Error(timeoutMessage)), timeoutMs);
      port.onMessage.addListener((message: Envelope) => {
        if (message.replyTo === id) finish(message);
      });
      port.onDisconnect.addListener(() => finish(null,
        new Error(this.runtime.lastError?.message ?? "The connection to the desktop app closed.")));
      try { port.postMessage({ v: PROTOCOL_VERSION, type, id, payload }); }
      catch (error) { finish(null, error instanceof Error ? error : new Error(String(error))); }
    });
  }

  disconnect(): void {
    if (!this.port) return;
    try { this.port.disconnect(); } catch { /* ignore */ }
    this.port = null;
  }
}

// ---- The server: POST /api/ipc with a paired token -------------------------------------------------

/**
 * What pairing stores (chrome.storage.local, key SERVER_STORAGE_KEY). The token is never sent to the extension's pages
 * or to content scripts. On Chrome and Edge background.ts closes storage.local to content scripts; Firefox has no
 * storage.local.setAccessLevel, so there content scripts can read it.
 */
export interface ServerPairing {
  /** The server's origin, as normalizeServerAddress gives it: scheme, host and port, no path. */
  address: string;
  token: string;
  /** The server's id for this pairing, as its web UI lists it. */
  id?: string;
  name?: string;
  pairedAt?: number;
}

export const SERVER_STORAGE_KEY = "server";

/** The part of the Fetch API this file uses, so the tests can pass their own. */
export type FetchLike = (input: string, init: {
  method: string;
  headers: Record<string, string>;
  body?: string;
  signal?: AbortSignal;
  credentials?: "omit";
  cache?: "no-store";
  redirect?: "error";
}) => Promise<{ status: number; text(): Promise<string> }>;

const defaultFetch: FetchLike = (input, init) => fetch(input, init);

/** A server answer that means this browser's pairing is gone (401): pairing again is the only fix. */
export class ServerUnpairedError extends Error {
  readonly unpaired = true;
}

/**
 * No HTTP answer at all: the connection failed (refused, no route, a TLS failure, or the browser blocked it). The
 * browser's own words go along, since a fetch error says no more than that, and "not answering" alone sent the
 * user to check a server that was running (EXT.B6). Wording that never matches readableError's desktop-app
 * patterns, so a server problem is never explained as one.
 */
export function unreachable(address: string, error?: unknown): string {
  const raw = error instanceof Error ? error.message : typeof error === "string" ? error : "";
  const said = raw.trim().replace(/[.\s]+$/, "").slice(0, 200);
  const https = address.startsWith("https:")
    ? " For an https:// address the browser must also trust the server's certificate: open the address in a tab to see whether it does."
    : "";
  return `Could not connect to the Inbound server at ${address}${said ? ` (the browser said: ${said})` : ""}. ` +
    `Check that it is running and that this address is right.${https}`;
}

/**
 * POST with the token in Authorization. Cookies are never sent (credentials "omit"), and a redirect is an
 * error rather than followed, so the token goes to the paired address only.
 */
function postJson(fetchImpl: FetchLike, url: string, body: unknown, timeoutMs: number,
  headers: Record<string, string>): Promise<{ status: number; body: any }> {
  return requestJson(fetchImpl, "POST", url, body, timeoutMs, headers);
}

/** One HTTP request as postJson describes it; a request without a body (DELETE) sends no Content-Type. */
async function requestJson(fetchImpl: FetchLike, method: "POST" | "DELETE", url: string, body: unknown,
  timeoutMs: number, headers: Record<string, string>): Promise<{ status: number; body: any }> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetchImpl(url, {
      method,
      headers: body === undefined ? { ...headers } : { "Content-Type": "application/json", ...headers },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      signal: controller.signal,
      credentials: "omit",
      cache: "no-store",
      redirect: "error",
    });
    const text = await response.text();
    let parsed: any;
    try { parsed = text ? JSON.parse(text) : undefined; } catch { parsed = undefined; }
    return { status: response.status, body: parsed };
  } catch (error) {
    if (controller.signal.aborted) throw new TimeoutError();
    throw error;
  } finally {
    clearTimeout(timer);
  }
}

class TimeoutError extends Error {}

/** The user's own server, paired from the extension's Settings. Each request is one HTTP request. */
export class ServerTransport implements Transport {
  readonly target = "server" as const;
  connected = false;
  lastError = "";
  /** Called when the server starts or stops answering (the badge's "!"), not for every request. */
  onStateChange: (() => void) | undefined;
  // "unknown" until a request has been made: a server never asked is not a server that is down (U-EX-4).
  private state: "unknown" | "up" | "down" = "unknown";

  constructor(readonly pairing: ServerPairing, private readonly fetchImpl: FetchLike = defaultFetch) {}

  /** True only after a request to the server failed, and until one gets an answer. */
  get down(): boolean {
    return this.state === "down";
  }

  /** Records whether the server answered (pairing counts as an answer). */
  settle(connected: boolean): void {
    this.connected = connected;
    const next = connected ? "up" : "down";
    if (next === this.state) return;
    this.state = next;
    try { this.onStateChange?.(); } catch { /* the badge is a convenience */ }
  }

  get address(): string {
    return this.pairing.address;
  }

  async request(type: string, payload: unknown, timeoutMs: number, timeoutMessage: string): Promise<Envelope> {
    const id = crypto.randomUUID();
    let answer: { status: number; body: any };
    try {
      answer = await postJson(this.fetchImpl, `${this.pairing.address}/api/ipc`,
        { v: PROTOCOL_VERSION, type, id, payload }, timeoutMs,
        { Authorization: `Bearer ${this.pairing.token}` });
    } catch (error) {
      throw this.failed(error instanceof TimeoutError ? timeoutMessage : unreachable(this.pairing.address, error));
    }

    const { status, body } = answer;
    if (status === 401) {
      this.settle(false);
      this.lastError = typeof body?.error === "string" && body.error
        ? body.error
        : "This browser isn't paired with the server, or its pairing was removed. Pair it again.";
      throw new ServerUnpairedError(this.lastError);
    }

    // An envelope answers the request, a refusal of the paired role (403) included.
    if (body && typeof body === "object" && typeof body.type === "string") {
      this.lastError = "";
      this.settle(true);
      return body as Envelope;
    }

    const reason = typeof body?.error === "string" && body.error ? body.error : `The Inbound server answered HTTP ${status}.`;
    throw this.failed(reason, status < 500 && status !== 0);
  }

  async send(type: string, payload: unknown, timeoutMs = 12000): Promise<any> {
    const reply = await this.request(type, payload, timeoutMs, "Timed out waiting for the Inbound server.");
    if (ERROR_TYPES.has(reply.type)) {
      throw new Error(extractError(reply.payload));
    }
    return reply.payload;
  }

  /**
   * Asks the server to remove this browser's pairing (DELETE /api/pair/self with the token), so the token
   * stops working there too. True only on 204; false when the server did not answer in time, refused, or
   * is too old to know the call. Never throws: Unpair forgets the token here whatever the answer.
   */
  async revokeSelf(timeoutMs = 4000): Promise<boolean> {
    try {
      const answer = await requestJson(this.fetchImpl, "DELETE", `${this.pairing.address}/api/pair/self`, undefined,
        timeoutMs, { Authorization: `Bearer ${this.pairing.token}` });
      return answer.status === 204;
    } catch {
      return false;
    }
  }

  private failed(message: string, connected = false): Error {
    this.lastError = message;
    this.settle(connected);
    return new Error(message);
  }
}

/**
 * The server's address as typed, as an origin, plus the host-permission pattern asked for at pairing.
 * A missing scheme means http; a path is dropped (the server answers at its root); a user name or
 * password in the address is refused, since it would go to the server with every request.
 */
export function normalizeServerAddress(input: string): { ok: true; origin: string; pattern: string; cleartext: boolean } | { ok: false; error: string } {
  const text = input.trim();
  if (!text) return { ok: false, error: "Type the server's address, for example http://192.168.1.20:8080." };
  const withScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(text) ? text : `http://${text}`;
  let url: URL;
  try { url = new URL(withScheme); } catch { return { ok: false, error: "That is not a web address." }; }
  if (url.protocol !== "http:" && url.protocol !== "https:") {
    return { ok: false, error: "The server's address starts with http:// or https://." };
  }
  if (url.username || url.password) {
    return { ok: false, error: "Leave the user name and password out of the address." };
  }
  if (!url.hostname) return { ok: false, error: "That address has no server name." };
  // 127.x only as a dotted IPv4 address (the URL parser has already turned 127.1 into 127.0.0.1): a name such as
  // 127.example.com is any host on the network. The web UI's hosts.ts uses the same rule.
  const loopback = url.hostname === "localhost" || url.hostname === "[::1]" || /^127(\.\d{1,3}){3}$/.test(url.hostname);
  return {
    ok: true,
    origin: url.origin,
    // Match patterns leave the port out; the pattern covers every port of that name.
    pattern: `${url.protocol}//${url.hostname}/*`,
    cleartext: url.protocol === "http:" && !loopback,
  };
}

/** The pairing code as the web UI shows it, with spaces and dashes taken out; null when it can't be one. */
export function normalizePairingCode(input: string): string | null {
  const code = input.replace(/[\s-]+/g, "").toUpperCase();
  return /^[A-Z0-9]{8}$/.test(code) ? code : null;
}

/** The name the server lists this browser under, for example "Chrome on Windows". */
export function pairingName(userAgent: string): string {
  const browser = /Firefox\//.test(userAgent) ? "Firefox"
    : /Edg\//.test(userAgent) ? "Edge"
      : /OPR\//.test(userAgent) ? "Opera"
        : /Chrome\//.test(userAgent) ? "Chrome" : "Browser";
  const system = /Windows/.test(userAgent) ? "Windows"
    : /CrOS/.test(userAgent) ? "ChromeOS"
      : /Android/.test(userAgent) ? "Android"
        : /Mac OS X|Macintosh/.test(userAgent) ? "macOS"
          : /Linux/.test(userAgent) ? "Linux" : "";
  return system ? `${browser} on ${system}` : browser;
}

/**
 * Turns the one-time code from the server's web UI into a token. The token is checked with one ping before
 * anything is stored, so a pairing that is saved is one that works.
 */
export async function pairWithServer(address: string, code: string, name: string,
  fetchImpl: FetchLike = defaultFetch, timeoutMs = 15000): Promise<ServerPairing> {
  const normalized = normalizeServerAddress(address);
  if (!normalized.ok) throw new Error(normalized.error);
  const cleanCode = normalizePairingCode(code);
  if (!cleanCode) throw new Error("The pairing code has 8 letters and digits. Show one in the web UI under Settings, Sign-in and browsers.");

  let answer: { status: number; body: any };
  try {
    answer = await postJson(fetchImpl, `${normalized.origin}/api/pair`, { code: cleanCode, name: name.slice(0, 100) }, timeoutMs, {});
  } catch (error) {
    throw new Error(error instanceof TimeoutError ? `The Inbound server at ${normalized.origin} took too long to answer.` : unreachable(normalized.origin, error));
  }

  const { status, body } = answer;
  if (status !== 200 || body?.ok !== true || typeof body.token !== "string" || !body.token.startsWith("ibt_")) {
    if (typeof body?.error === "string" && body.error) throw new Error(body.error);
    throw new Error(status === 404
      ? `${normalized.origin} is not an Inbound server, or one too old to pair with.`
      : `The Inbound server answered HTTP ${status}.`);
  }

  const pairing: ServerPairing = {
    address: normalized.origin,
    token: body.token,
    id: typeof body.id === "string" ? body.id : undefined,
    name: typeof body.name === "string" ? body.name : name,
    pairedAt: Date.now(),
  };
  await new ServerTransport(pairing, fetchImpl).send("host.ping", {}, timeoutMs);
  return pairing;
}

/** A stored pairing, or null when nothing usable is stored. */
export function readPairing(value: unknown): ServerPairing | null {
  if (!value || typeof value !== "object") return null;
  const record = value as Record<string, unknown>;
  if (typeof record.token !== "string" || !record.token.startsWith("ibt_") || typeof record.address !== "string") return null;
  const normalized = normalizeServerAddress(record.address);
  if (!normalized.ok || normalized.origin !== record.address) return null;
  return {
    address: normalized.origin,
    token: record.token,
    id: typeof record.id === "string" ? record.id : undefined,
    name: typeof record.name === "string" ? record.name : undefined,
    pairedAt: typeof record.pairedAt === "number" ? record.pairedAt : undefined,
  };
}

// ---- Which target ----------------------------------------------------------------------------------

/** True when a native error means the desktop app is not installed (not registered) in this browser. */
export function isNativeHostMissing(error: string | undefined): boolean {
  return /not found|no such native application/i.test(error ?? "");
}

/**
 * The targets a download can go to. The server, once paired. This PC, unless the server is paired and the
 * desktop app is not installed here: then the choice would only offer something that can't work. Never empty.
 */
export function availableTargets(state: { serverPaired: boolean; localMissing: boolean }): TargetId[] {
  const targets: TargetId[] = [];
  if (!state.serverPaired || !state.localMissing) targets.push("local");
  if (state.serverPaired) targets.push("server");
  return targets;
}

/**
 * Where an automatic catch goes: the configured default, which is the server only while it is paired.
 * There is no fallback to the other target on failure: the download stays in the browser instead, so the
 * user's cookies only ever go where they chose.
 */
export function automaticTarget(defaultTarget: TargetId | undefined, serverPaired: boolean): TargetId {
  return defaultTarget === "server" && serverPaired ? "server" : "local";
}

/** The target the folder window starts on: the default when it is offered, else the first offered. */
export function initialTarget(defaultTarget: TargetId | undefined, available: readonly TargetId[]): TargetId {
  return defaultTarget && available.includes(defaultTarget) ? defaultTarget : available[0] ?? "local";
}
