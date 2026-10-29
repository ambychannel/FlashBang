# FlashBang relay

A tiny Cloudflare Worker that passes FlashBang scene messages between players in the same room.

- One room per code (12 letters and digits, shown as `XXXX-XXXX-XXXX`). Codes are made by the relay, so they're random and hard to guess.
- Nothing is passed between two players until **both** of them agree to sync with each other. This is enforced here, not just in the plugin.
- It stores only that the room exists and who agreed to whom. Rooms are deleted after 24 hours with nobody in them.
- Limits: 8 players per room, 16 KB per message, about 30 messages a second per player.

Runs on Cloudflare's free plan (Durable Objects with SQLite storage).

## Messages

Client to relay: `{t:"consent", peer, accept}`, `{t:"op", data, to?}`, `{t:"ping"}`.

Relay to client: `welcome {you, members, consents}`, `joined {id, name}`, `left {id}`, `consent {from, accept}`, `linked {peer}`, `unlinked {peer}`, `op {from, data}`, `error {code, message}`, `pong`.

HTTP: `POST /rooms` returns `{code}`. `GET /rooms/{code}/ws?name=...` opens the WebSocket.

## Develop

```
npm install
npm test        # starts a local relay and runs the end-to-end tests
npm run dev     # local relay on http://127.0.0.1:8787
npm run deploy  # needs a Cloudflare account; see the main README
```
