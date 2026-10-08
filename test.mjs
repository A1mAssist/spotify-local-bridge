import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import http from "node:http";
import vm from "node:vm";
import { createBridgeServer } from "./bridge.mjs";

const extension = await readFile(new URL("./spotify-local-bridge.js", import.meta.url), "utf8");
const messages = [];
const player = {
  _state: {
    item: {
      uri: "spotify:track:0BxE4FqsDD1Ot4YuBXwAPp",
      name: "505",
      artists: [{ name: "Arctic Monkeys", uri: "spotify:artist:7Ln80lUS6He07XvHI8qqHH" }],
      album: { name: "Favourite Worst Nightmare", uri: "spotify:album:1pC9EKmMbDH8M6o7l4s3sM", images: [{ url: "spotify:image:abc" }] },
      metadata: { isrc: "invalid", track_isrc: "invalid" },
      external_id: {},
    },
    duration: 253000,
    isPaused: false,
    timestamp: Date.now(),
    positionAsOfTimestamp: 5000,
    context: { uri: "spotify:album:abc" },
    shuffle: false,
    repeat: 0,
  },
};
let cosmosCalls = 0;
let tick;
const cosmos = { get: async () => {
  cosmosCalls++;
  return { external_id: [{ type: "isrc", id: "US-UM7-17-03861" }] };
} };
const registry = {
  _map: new Map([[Symbol.for("PlayerAPI"), player]]),
  resolve(token) { return this._map.get(token); },
};
const window = {};
vm.runInNewContext(extension, {
  window,
  AbortController,
  setTimeout,
  clearTimeout,
  setInterval: (callback) => { tick = callback; },
  fetch: async (_url, options) => {
    messages.push(JSON.parse(options.body));
    return { ok: true };
  },
  console,
});
window.__spotifyLocalBridgeStart(registry);
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages[0].track_uri, player._state.item.uri);
assert.ok(messages[0].progress_ms >= 5000 && messages[0].progress_ms < 5100);
assert.equal(messages[0].is_playing, true);
assert.equal(messages[0].artist_uris[0], player._state.item.artists[0].uri);
assert.equal(messages[0].album_uri, player._state.item.album.uri);
assert.equal(messages[0].isrc, null);
assert.equal(messages[0].image_url, "spotify:image:abc");
registry._map.set(Symbol.for("Cosmos"), cosmos);
tick();
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).isrc, "USUM71703861");
player._state.isPaused = true;
player._state.positionAsOfTimestamp = 9000;
tick();
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).progress_ms, 9000);
assert.equal(messages.at(-1).is_playing, false);
player._state.isPaused = false;
player._state.positionAsOfTimestamp = 2000;
player._state.timestamp = Date.now();
tick();
await new Promise((resolve) => setImmediate(resolve));
assert.ok(messages.at(-1).progress_ms >= 2000 && messages.at(-1).progress_ms < 2100);
assert.equal(cosmosCalls, 1);

const server = createBridgeServer();
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const base = `http://127.0.0.1:${server.address().port}`;
const origin = "https://xpui.app.spotify.com";

try {
  assert.equal((await fetch(`${base}/state`)).status, 204);
  const wrongHost = await new Promise((resolve, reject) => {
    http.get(`${base}/state`, { headers: { Host: "untrusted.example" } }, (response) => {
      response.resume();
      resolve(response.statusCode);
    }).on("error", reject);
  });
  assert.equal(wrongHost, 403);
  assert.equal((await fetch(`${base}/ingest`, { method: "OPTIONS", headers: { Origin: origin } })).status, 204);
  assert.equal((await fetch(`${base}/ingest`, {
    method: "POST", headers: { Origin: "https://example.com", "Content-Type": "application/json" }, body: "{}",
  })).status, 403);

  const state = {
    track_uri: "spotify:track:0BxE4FqsDD1Ot4YuBXwAPp",
    title: "505",
    artists: ["Arctic Monkeys"],
    album: "Favourite Worst Nightmare",
    duration_ms: 253000,
    progress_ms: 5000,
    is_playing: true,
    context_uri: null,
    shuffle: false,
    repeat: 0,
  };
  const post = (value) => fetch(`${base}/ingest`, {
    method: "POST", headers: { Origin: origin, "Content-Type": "application/json" }, body: JSON.stringify(value),
  });
  assert.equal((await post({ ...state, progress_ms: -1 })).status, 400);
  assert.equal((await post({ ...state, artist_uris: [42] })).status, 400);
  assert.equal((await post({ ...state, isrc: "invalid" })).status, 400);
  assert.equal((await post({ ...state, isrc: "USUM71703861" })).status, 204);
  assert.equal((await (await fetch(`${base}/state`)).json()).isrc, "USUM71703861");
  assert.equal((await post(state)).status, 204);
  const response = await fetch(`${base}/state`);
  assert.equal(response.status, 200);
  const current = await response.json();
  assert.equal(current.track_uri, state.track_uri);
  assert.ok(current.progress_ms >= 5000);
  assert.ok(current.updated_at_ms > 0);
  assert.equal((await post({ ...state, is_playing: false, progress_ms: 9000 })).status, 204);
  const paused = await (await fetch(`${base}/state`)).json();
  assert.equal(paused.progress_ms, 9000);
  assert.equal(paused.is_playing, false);
  assert.equal((await post({ ...state, progress_ms: 2000 })).status, 204);
  const seeked = await (await fetch(`${base}/state`)).json();
  assert.ok(seeked.progress_ms >= 2000 && seeked.progress_ms < 3000);
  console.log("Bridge checks passed");
} finally {
  await new Promise((resolve) => server.close(resolve));
}
