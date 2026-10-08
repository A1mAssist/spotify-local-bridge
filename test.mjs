import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";

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
let commandTick;
let update;
const actions = [];
const commandQueue = [];
const controlResults = [];
const libraryCalls = [];
let holdFetch = false;
let releaseFetch;
let metadataMode = "getter";
player._events = { addListener(name, callback) {
  assert.equal(name, "update");
  update = callback;
} };
player.pause = async () => actions.push("pause");
player.resume = async () => actions.push("resume");
player.skipToNext = async () => actions.push("next");
player.skipToPrevious = async () => actions.push("previous");
player.seekTo = async (position) => actions.push(`seek:${position}`);
player.setShuffle = async (state) => actions.push(`shuffle:${state}`);
player.setRepeat = async (state) => actions.push(`repeat:${state}`);
const library = {
  contains: async (...uris) => {
    libraryCalls.push(["contains", ...uris]);
    return uris.map((uri) => uri.endsWith("0BxE4FqsDD1Ot4YuBXwAPp"));
  },
  add: async ({ uris }) => libraryCalls.push(["add", ...uris]),
  remove: async ({ uris }) => libraryCalls.push(["remove", ...uris]),
};
const cosmos = { get: async (url) => {
  cosmosCalls++;
  if (metadataMode === "api") {
    if (url.startsWith("sp://")) return {};
    return { external_ids: { isrc: "GBUM71703861" } };
  }
  const metadata = Object.create({
    get external_id() { return [{ type: "isrc", id: "US-UM7-17-03861" }]; },
  });
  return metadata;
} };
const registry = {
  _map: new Map([[Symbol.for("PlayerAPI"), player], [Symbol.for("LibraryAPI"), library]]),
  resolve(token) { return this._map.get(token); },
};
const window = {};
vm.runInNewContext(extension, {
  window,
  AbortController,
  setTimeout,
  clearTimeout,
  setInterval: (callback, delay) => { if (delay === 100) commandTick = callback; else tick = callback; },
  fetch: async (_url, options = {}) => {
    if ((options.method ?? "GET") === "GET") return { ok: true, json: async () => commandQueue.splice(0) };
    if (_url.endsWith("/control-result")) {
      controlResults.push(JSON.parse(options.body));
      return { ok: true };
    }
    messages.push(JSON.parse(options.body));
    if (holdFetch) {
      holdFetch = false;
      await new Promise((resolve) => { releaseFetch = resolve; });
    }
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
commandQueue.push(
  { name: "pause" },
  { name: "resume" },
  { name: "next" },
  { name: "previous" },
  { name: "seek", value: 12000 },
  { name: "shuffle", value: 1 },
  { name: "repeat", value: 2 },
  { name: "library-check", id: "check-1", uris: ["spotify:track:0BxE4FqsDD1Ot4YuBXwAPp", "spotify:track:other"] },
  { name: "library-add", id: "add-1", uris: ["spotify:track:other"] },
  { name: "library-remove", id: "remove-1", uris: ["spotify:track:other"] },
);
await commandTick();
assert.deepEqual(actions, ["pause", "resume", "next", "previous", "seek:12000", "shuffle:true", "repeat:2"]);
assert.deepEqual(libraryCalls, [
  ["contains", "spotify:track:0BxE4FqsDD1Ot4YuBXwAPp", "spotify:track:other"],
  ["add", "spotify:track:other"],
  ["remove", "spotify:track:other"],
]);
assert.deepEqual(controlResults.filter((result) => result.id).map((result) => [result.id, result.success]), [
  ["check-1", true], ["add-1", true], ["remove-1", true],
]);
assert.deepEqual(controlResults.find((result) => result.id === "check-1").values, [true, false]);
registry._map.set(Symbol.for("Cosmos"), cosmos);
tick();
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).isrc, "USUM71703861");
update({ data: { ...player._state, isPaused: true, positionAsOfTimestamp: 9000 } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).progress_ms, 9000);
assert.equal(messages.at(-1).is_playing, false);
update({ data: { ...player._state, isPaused: false, positionAsOfTimestamp: 2000, timestamp: Date.now() } });
await new Promise((resolve) => setImmediate(resolve));
assert.ok(messages.at(-1).progress_ms >= 2000 && messages.at(-1).progress_ms < 2100);
assert.equal(cosmosCalls, 1);
metadataMode = "api";
update({ data: { ...player._state, item: { ...player._state.item, uri: "spotify:track:6rqhFgbbKwnb9MLmUQDhG6" } } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).isrc, "GBUM71703861");
assert.equal(cosmosCalls, 3);
holdFetch = true;
tick();
await new Promise((resolve) => setImmediate(resolve));
update({ data: { ...player._state, isPaused: true, positionAsOfTimestamp: 20000 } });
releaseFetch();
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).progress_ms, 20000);
assert.equal(messages.at(-1).is_playing, false);
console.log("Spotify XPUI checks passed");
