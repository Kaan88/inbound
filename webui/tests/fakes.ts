// A fake WebSocket and fake timers for the protocol client's tests.
import type { Envelope, SocketLike, Timers } from '../src/lib/protocol';

export class FakeSocket implements SocketLike {
  readyState = 0;
  onopen: ((event: unknown) => void) | null = null;
  onmessage: ((event: { data: unknown }) => void) | null = null;
  onclose: ((event: { code: number; reason?: string }) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;
  sent: Envelope[] = [];
  closed = false;

  constructor(readonly url: string) {}

  send(data: string): void {
    this.sent.push(JSON.parse(data) as Envelope);
  }

  close(): void {
    this.closed = true;
  }

  receive(message: Partial<Envelope> & { type: string }): void {
    this.onmessage?.({ data: JSON.stringify({ v: 1, id: 'srv', ...message }) });
  }

  drop(code = 1006): void {
    this.readyState = 3;
    this.onclose?.({ code });
  }
}

export class FakeTimers implements Timers {
  now = 0;
  private next = 1;
  private readonly waiting = new Map<number, { at: number; handler: () => void }>();
  delays: number[] = [];

  setTimeout(handler: () => void, ms: number): unknown {
    const id = this.next++;
    this.waiting.set(id, { at: this.now + ms, handler });
    this.delays.push(ms);
    return id;
  }

  clearTimeout(handle: unknown): void {
    this.waiting.delete(handle as number);
  }

  random(): number {
    return 0;
  }

  /** Moves time on, running what falls due in order. */
  advance(ms: number): void {
    const until = this.now + ms;
    for (;;) {
      const due = [...this.waiting.entries()].filter(([, t]) => t.at <= until).sort((a, b) => a[1].at - b[1].at)[0];
      if (!due) break;
      this.waiting.delete(due[0]);
      this.now = due[1].at;
      due[1].handler();
    }
    this.now = until;
  }
}

/** Lets awaited promises inside the client settle. */
export async function settle(): Promise<void> {
  for (let i = 0; i < 5; i++) await Promise.resolve();
}
