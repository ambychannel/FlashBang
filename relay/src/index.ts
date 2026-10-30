// FlashBang relay: one Durable Object per room code. It forwards scene messages
// between players who have both agreed to sync with each other, and stores nothing
// except the room's existence and who agreed to whom.

export interface Env {
  ROOMS: DurableObjectNamespace;
}

// No 0/O, 1/I/L so codes are easy to read out and type.
const CODE_ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
const CODE_LENGTH = 12;

const MAX_MEMBERS = 8;
const MAX_MESSAGE_CHARS = 16 * 1024;
const MAX_BINARY_BYTES = 256 * 1024;
const MAX_BINARY_HEADER_BYTES = 256;
const MAX_NAME_CHARS = 32;
const RATE_PER_SECOND = 30;
const RATE_BURST = 60;
const BYTES_PER_SECOND = 8 * 1024 * 1024;
const BYTES_BURST = 16 * 1024 * 1024;
const ROOM_IDLE_MS = 24 * 60 * 60 * 1000;

export function normalizeCode(raw: string): string | null {
  const code = raw.toUpperCase().replace(/[\s-]/g, "");
  if (code.length !== CODE_LENGTH) return null;
  for (const ch of code) if (!CODE_ALPHABET.includes(ch)) return null;
  return code;
}

function newCode(): string {
  const bytes = new Uint8Array(CODE_LENGTH);
  crypto.getRandomValues(bytes);
  let code = "";
  // 31 symbols; rejection sampling keeps the distribution even.
  let i = 0;
  while (code.length < CODE_LENGTH) {
    if (i >= bytes.length) {
      crypto.getRandomValues(bytes);
      i = 0;
    }
    const b = bytes[i++];
    if (b < 248) code += CODE_ALPHABET[b % CODE_ALPHABET.length];
  }
  return code;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const parts = url.pathname.split("/").filter(Boolean);

    if (parts.length === 0) return new Response("FlashBang relay");

    if (parts[0] === "rooms" && parts.length === 1 && request.method === "POST") {
      for (let attempt = 0; attempt < 5; attempt++) {
        const code = newCode();
        const stub = env.ROOMS.get(env.ROOMS.idFromName(code));
        const res = await stub.fetch("https://room/init", { method: "POST" });
        if (res.ok) return json({ code });
      }
      return json({ error: "Could not create a room. Try again." }, 503);
    }

    if (parts[0] === "rooms" && parts.length === 3 && parts[2] === "ws") {
      const code = normalizeCode(decodeURIComponent(parts[1]));
      if (!code) return json({ error: "That room code isn't valid." }, 400);
      if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket")
        return json({ error: "Expected a WebSocket." }, 426);
      const stub = env.ROOMS.get(env.ROOMS.idFromName(code));
      return stub.fetch(new Request("https://room/ws" + url.search, request));
    }

    return json({ error: "Not found." }, 404);
  },
};

interface Member {
  id: string;
  name: string;
}

type ClientMessage =
  | { t: "consent"; peer: string; accept: boolean }
  | { t: "op"; data: string; to?: string }
  | { t: "ping" };

export class Room implements DurableObject {
  private consents = new Set<string>(); // "a>b" means a agreed to sync with b
  private buckets = new Map<string, { tokens: number; at: number }>();
  private byteBuckets = new Map<string, { tokens: number; at: number }>();

  constructor(private ctx: DurableObjectState, private env: Env) {
    ctx.blockConcurrencyWhile(async () => {
      const saved = await ctx.storage.get<string[]>("consents");
      if (saved) this.consents = new Set(saved);
    });
  }

  async fetch(request: Request): Promise<Response> {
    const url = new URL(request.url);

    if (url.pathname === "/init") {
      if (await this.ctx.storage.get("created")) return new Response("taken", { status: 409 });
      await this.ctx.storage.put("created", Date.now());
      await this.ctx.storage.setAlarm(Date.now() + ROOM_IDLE_MS);
      return new Response("ok");
    }

    if (url.pathname === "/ws") {
      if (!(await this.ctx.storage.get("created")))
        return json({ error: "No room with that code. Check the code or create a new room." }, 404);

      const members = this.members();
      if (members.length >= MAX_MEMBERS) return json({ error: "This room is full." }, 403);

      const name = sanitizeName(url.searchParams.get("name") ?? "");
      const me: Member = { id: randomId(), name };

      const pair = new WebSocketPair();
      const [client, server] = Object.values(pair);
      this.ctx.acceptWebSocket(server);
      server.serializeAttachment(me);

      send(server, { t: "welcome", you: me.id, members, consents: this.consentsFor(me.id) });
      this.broadcast({ t: "joined", id: me.id, name: me.name }, me.id);

      return new Response(null, { status: 101, webSocket: client });
    }

    return new Response("not found", { status: 404 });
  }

  async webSocketMessage(ws: WebSocket, raw: string | ArrayBuffer): Promise<void> {
    const me = ws.deserializeAttachment() as Member;

    if (typeof raw !== "string") {
      this.forwardBinary(ws, me, raw);
      return;
    }
    if (raw.length > MAX_MESSAGE_CHARS) {
      send(ws, { t: "error", code: "too_large", message: "Message too large." });
      return;
    }
    if (!this.allow(me.id)) {
      send(ws, { t: "error", code: "rate", message: "Sending too fast; some updates were dropped." });
      return;
    }

    let msg: ClientMessage;
    try {
      msg = JSON.parse(raw);
    } catch {
      return;
    }

    switch (msg.t) {
      case "ping":
        send(ws, { t: "pong" });
        return;

      case "consent": {
        if (typeof msg.peer !== "string" || msg.peer === me.id) return;
        const peer = this.socketOf(msg.peer);
        if (!peer) return;
        const wasLinked = this.linked(me.id, msg.peer);
        if (msg.accept) this.consents.add(`${me.id}>${msg.peer}`);
        else this.consents.delete(`${me.id}>${msg.peer}`);
        await this.saveConsents();

        send(peer, { t: "consent", from: me.id, accept: !!msg.accept });
        const nowLinked = this.linked(me.id, msg.peer);
        if (nowLinked && !wasLinked) {
          send(ws, { t: "linked", peer: msg.peer });
          send(peer, { t: "linked", peer: me.id });
        } else if (wasLinked && !nowLinked) {
          send(ws, { t: "unlinked", peer: msg.peer });
          send(peer, { t: "unlinked", peer: me.id });
        }
        return;
      }

      case "op": {
        if (typeof msg.data !== "string") return;
        const out = JSON.stringify({ t: "op", from: me.id, data: msg.data });
        for (const other of this.ctx.getWebSockets()) {
          const them = other.deserializeAttachment() as Member;
          if (them.id === me.id) continue;
          if (msg.to && msg.to !== them.id) continue;
          if (!this.linked(me.id, them.id)) continue;
          trySend(other, out);
        }
        return;
      }
    }
  }

  async webSocketClose(ws: WebSocket): Promise<void> {
    await this.leave(ws);
  }

  async webSocketError(ws: WebSocket): Promise<void> {
    await this.leave(ws);
  }

  async alarm(): Promise<void> {
    if (this.ctx.getWebSockets().length > 0) {
      await this.ctx.storage.setAlarm(Date.now() + ROOM_IDLE_MS);
      return;
    }
    await this.ctx.storage.deleteAll();
  }

  private async leave(ws: WebSocket): Promise<void> {
    const me = ws.deserializeAttachment() as Member | null;
    if (!me) return;
    try {
      ws.close(1000, "left");
    } catch {}
    for (const key of [...this.consents])
      if (key.startsWith(`${me.id}>`) || key.endsWith(`>${me.id}`)) this.consents.delete(key);
    await this.saveConsents();
    this.buckets.delete(me.id);
    this.byteBuckets.delete(me.id);
    this.broadcast({ t: "left", id: me.id }, me.id);
    await this.ctx.storage.setAlarm(Date.now() + ROOM_IDLE_MS);
  }

  private members(): Member[] {
    return this.ctx
      .getWebSockets()
      .filter((ws) => ws.readyState === WebSocket.OPEN)
      .map((ws) => ws.deserializeAttachment() as Member);
  }

  private socketOf(id: string): WebSocket | undefined {
    return this.ctx.getWebSockets().find((ws) => (ws.deserializeAttachment() as Member).id === id);
  }

  private linked(a: string, b: string): boolean {
    return this.consents.has(`${a}>${b}`) && this.consents.has(`${b}>${a}`);
  }

  private consentsFor(id: string): { from: string; to: string }[] {
    return [...this.consents]
      .map((k) => k.split(">"))
      .filter(([a, b]) => a === id || b === id)
      .map(([from, to]) => ({ from, to }));
  }

  private async saveConsents(): Promise<void> {
    await this.ctx.storage.put("consents", [...this.consents]);
  }

  private broadcast(msg: unknown, except?: string): void {
    const out = JSON.stringify(msg);
    for (const ws of this.ctx.getWebSockets()) {
      if ((ws.deserializeAttachment() as Member).id === except) continue;
      trySend(ws, out);
    }
  }

  // Binary frames carry bulk data (mod files) to one linked player. Layout, both ways:
  // 2-byte big-endian header length, a JSON header, then the payload. The sender's header is
  // {to}; the relay replaces it with {from} so nobody can pretend to be someone else.
  private forwardBinary(ws: WebSocket, me: Member, raw: ArrayBuffer): void {
    if (raw.byteLength > MAX_BINARY_BYTES + MAX_BINARY_HEADER_BYTES + 2) {
      send(ws, { t: "error", code: "too_large", message: "Message too large." });
      return;
    }
    if (!this.allow(me.id) || !this.allowBytes(me.id, raw.byteLength)) {
      send(ws, { t: "error", code: "rate", message: "Sending too fast; some updates were dropped." });
      return;
    }

    const bytes = new Uint8Array(raw);
    if (bytes.length < 2) return;
    const headerLength = (bytes[0] << 8) | bytes[1];
    if (headerLength > MAX_BINARY_HEADER_BYTES || bytes.length < 2 + headerLength) return;

    let header: { to?: unknown };
    try {
      header = JSON.parse(new TextDecoder().decode(bytes.subarray(2, 2 + headerLength)));
    } catch {
      return;
    }

    const to = header?.to;
    if (typeof to !== "string" || to === me.id || !this.linked(me.id, to)) return;
    const target = this.socketOf(to);
    if (!target) return;

    const payload = bytes.subarray(2 + headerLength);
    if (payload.length > MAX_BINARY_BYTES) {
      send(ws, { t: "error", code: "too_large", message: "Message too large." });
      return;
    }

    const outHeader = new TextEncoder().encode(JSON.stringify({ from: me.id }));
    const out = new Uint8Array(2 + outHeader.length + payload.length);
    out[0] = outHeader.length >> 8;
    out[1] = outHeader.length & 0xff;
    out.set(outHeader, 2);
    out.set(payload, 2 + outHeader.length);

    try {
      target.send(out);
    } catch {}
  }

  private allow(id: string): boolean {
    return take(this.buckets, id, 1, RATE_PER_SECOND, RATE_BURST);
  }

  private allowBytes(id: string, count: number): boolean {
    return take(this.byteBuckets, id, count, BYTES_PER_SECOND, BYTES_BURST);
  }
}

function take(
  buckets: Map<string, { tokens: number; at: number }>,
  id: string,
  cost: number,
  perSecond: number,
  burst: number,
): boolean {
  const now = Date.now();
  const b = buckets.get(id) ?? { tokens: burst, at: now };
  b.tokens = Math.min(burst, b.tokens + ((now - b.at) / 1000) * perSecond);
  b.at = now;
  const ok = b.tokens >= cost;
  if (ok) b.tokens -= cost;
  buckets.set(id, b);
  return ok;
}

function sanitizeName(raw: string): string {
  const clean = raw.replace(/[\u0000-\u001f\u007f]/g, "").trim().slice(0, MAX_NAME_CHARS);
  return clean || "Guest";
}

function randomId(): string {
  const bytes = new Uint8Array(6);
  crypto.getRandomValues(bytes);
  return [...bytes].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function send(ws: WebSocket, msg: unknown): void {
  trySend(ws, JSON.stringify(msg));
}

function trySend(ws: WebSocket, data: string): void {
  try {
    ws.send(data);
  } catch {}
}
