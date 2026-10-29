// End-to-end test against `wrangler dev`. Run: npm test (starts the dev server itself).
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import WebSocket from "ws";

const PORT = 8799;
const BASE = `http://127.0.0.1:${PORT}`;
let server;

before(async () => {
  server = spawn("npx", ["wrangler", "dev", "--port", String(PORT), "--ip", "127.0.0.1"], {
    stdio: "ignore",
    env: { ...process.env, WRANGLER_SEND_METRICS: "false" },
  });
  for (let i = 0; i < 120; i++) {
    try {
      if ((await fetch(BASE)).ok) return;
    } catch {}
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error("wrangler dev did not start");
});

after(() => server?.kill());

async function createRoom() {
  const res = await fetch(`${BASE}/rooms`, { method: "POST" });
  assert.equal(res.status, 200);
  return (await res.json()).code;
}

function connect(code, name) {
  const ws = new WebSocket(`ws://127.0.0.1:${PORT}/rooms/${code}/ws?name=${encodeURIComponent(name)}`);
  const inbox = [];
  const waiters = [];
  ws.on("message", (data) => {
    const msg = JSON.parse(data.toString());
    const i = waiters.findIndex((w) => w.pred(msg));
    if (i >= 0) waiters.splice(i, 1)[0].resolve(msg);
    else inbox.push(msg);
  });
  const next = (pred, ms = 3000) => {
    const i = inbox.findIndex(pred);
    if (i >= 0) return Promise.resolve(inbox.splice(i, 1)[0]);
    return new Promise((resolve, reject) => {
      const w = { pred, resolve };
      waiters.push(w);
      setTimeout(() => {
        const j = waiters.indexOf(w);
        if (j >= 0) {
          waiters.splice(j, 1);
          reject(new Error("timeout"));
        }
      }, ms);
    });
  };
  const opened = new Promise((r, j) => {
    ws.on("open", r);
    ws.on("unexpected-response", (_req, res) => j(new Error(`HTTP ${res.statusCode}`)));
  });
  return { ws, next, opened, send: (m) => ws.send(JSON.stringify(m)), inbox };
}

test("rejects malformed and unknown codes", async () => {
  await assert.rejects(connect("NOPE", "x").opened, /HTTP 400/);
  const c = connect("ABCD-EFGH-JKMN", "x");
  await assert.rejects(c.opened, /HTTP 404/);
});

test("nothing is forwarded until both players agree", async () => {
  const code = await createRoom();
  assert.match(code, /^[A-HJ-NP-Z2-9]{12}$/);

  const a = connect(code, "Wimbles");
  await a.opened;
  const aWelcome = await a.next((m) => m.t === "welcome");

  const b = connect(code.slice(0, 4) + "-" + code.slice(4, 8).toLowerCase() + "-" + code.slice(8), "Kaia");
  await b.opened;
  const bWelcome = await b.next((m) => m.t === "welcome");
  assert.deepEqual(bWelcome.members.map((m) => m.name), ["Wimbles"]);

  const joined = await a.next((m) => m.t === "joined");
  assert.equal(joined.name, "Kaia");
  assert.equal(joined.id, bWelcome.you);

  // A sends before any consent: B must not get it.
  a.send({ t: "op", data: "early" });
  await assert.rejects(b.next((m) => m.t === "op", 500));

  // Only A agrees: still nothing.
  a.send({ t: "consent", peer: bWelcome.you, accept: true });
  const toB = await b.next((m) => m.t === "consent");
  assert.equal(toB.from, aWelcome.you);
  a.send({ t: "op", data: "half" });
  await assert.rejects(b.next((m) => m.t === "op", 500));

  // B agrees: both get linked, ops flow both ways.
  b.send({ t: "consent", peer: aWelcome.you, accept: true });
  await a.next((m) => m.t === "linked");
  await b.next((m) => m.t === "linked");
  a.send({ t: "op", data: "light-1" });
  const op = await b.next((m) => m.t === "op");
  assert.deepEqual(op, { t: "op", from: aWelcome.you, data: "light-1" });
  b.send({ t: "op", data: "light-2" });
  assert.equal((await a.next((m) => m.t === "op")).data, "light-2");

  // B withdraws: unlinked and forwarding stops.
  b.send({ t: "consent", peer: aWelcome.you, accept: false });
  await a.next((m) => m.t === "unlinked");
  a.send({ t: "op", data: "after" });
  await assert.rejects(b.next((m) => m.t === "op", 500));

  // B leaves: A is told.
  b.ws.close();
  const left = await a.next((m) => m.t === "left");
  assert.equal(left.id, bWelcome.you);
  a.ws.close();
});

test("oversized messages are refused", async () => {
  const code = await createRoom();
  const a = connect(code, "A");
  await a.opened;
  await a.next((m) => m.t === "welcome");
  a.send({ t: "op", data: "x".repeat(20000) });
  const err = await a.next((m) => m.t === "error");
  assert.equal(err.code, "too_large");
  a.ws.close();
});
