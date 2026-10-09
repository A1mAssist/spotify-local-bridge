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
    isBuffering: false,
    hasContext: true,
    speed: 1,
    timestamp: Date.now(),
    positionAsOfTimestamp: 5000,
    context: { uri: "spotify:album:abc" },
    shuffle: false,
    repeat: 0,
  },
};
let metadataCalls = 0;
const metadataUrls = [];
let tick;
let commandTick;
let update;
let volumeUpdate;
const actions = [];
const commandQueue = [];
const controlResults = [];
const libraryCalls = [];
let holdFetch = false;
let releaseFetch;
let metadataMode = "getter";
let releaseMetadata;
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
const playback = {
  volume: 0.37,
  getVolume() { return this.volume; },
  async setVolume(volume) {
    assert.equal(this, playback);
    this.volume = volume;
    actions.push(`volume:${volume}`);
    volumeUpdate();
  },
  getEvents() {
    return { addListener(name, callback) {
      assert.equal(name, "volume");
      volumeUpdate = callback;
    } };
  },
};
const requestBuilder = { build() {
  let host;
  let path;
  return {
    withHost(value) { host = value; return this; },
    withPath(value) { path = value; return this; },
    withEndpointIdentifier(value) { assert.equal(value, "/metadata/4/track/{gid}"); return this; },
    withAbortSignal(value) { assert.equal(value.aborted, false); return this; },
    async send() {
      assert.equal(host, "https://spclient.wg.spotify.com");
      metadataCalls++;
      metadataUrls.push(host + path);
      if (metadataMode === "delayed") return new Promise((resolve) => { releaseMetadata = (body) => resolve({ body }); });
      if (metadataMode === "none") return { body: {} };
      if (metadataMode === "api") return { body: { external_id: [{ type: "isrc", id: "GBUM71703861" }] } };
      const body = Object.create({
        get external_id() { return [{ type: "isrc", id: "US-UM7-17-03861" }]; },
      });
      return { body };
    },
  };
} };
const registry = {
  _map: new Map([[Symbol.for("PlayerAPI"), player], [Symbol.for("LibraryAPI"), library]]),
  resolve(token) { return this._map.get(token); },
};
const window = {};
let bridgeNow = Date.now();
vm.runInNewContext(extension, {
  window,
  Date: class extends Date { static now() { return bridgeNow; } },
  AbortController,
  setTimeout: (callback, delay) => delay === 5000 ? (queueMicrotask(callback), 0) : setTimeout(callback, delay),
  clearTimeout,
  setInterval: (callback, delay) => { if (delay === 100) commandTick = callback; else tick = callback; },
  fetch: async (_url, options = {}) => {
    if ((options.method ?? "GET") === "GET") return { ok: true, json: async () => commandQueue.splice(0) };
    if (_url.endsWith("/control-result")) {
      controlResults.push(JSON.parse(options.body));
      return { ok: true };
    }
    assert.equal(options.signal?.aborted, false, "Metadata lookup must not consume the playback request timeout");
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
assert.equal(messages[0].volume_percent, null);
registry._map.set(Symbol.for("PlaybackAPI"), playback);
tick();
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).volume_percent, 37);
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
for (const percent of [37, 0, 100]) {
  commandQueue.push({ name: "volume", value: percent, id: `volume-${percent}` });
  await commandTick();
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(actions.at(-1), `volume:${percent / 100}`);
  assert.equal(messages.at(-1).volume_percent, percent);
  assert.equal(controlResults.at(-1).id, `volume-${percent}`);
  assert.equal(controlResults.at(-1).success, true);
}
const actionCount = actions.length;
for (const percent of [-1, 101, null, 0.5]) {
  commandQueue.push({ name: "volume", value: percent, id: "invalid-volume" });
  await commandTick();
  assert.equal(controlResults.at(-1).id, "invalid-volume");
  assert.equal(controlResults.at(-1).success, false);
}
assert.equal(actions.length, actionCount);
playback.volume = -1;
volumeUpdate();
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).volume_percent, null);
commandQueue.push({ name: "volume", value: 37, id: "unavailable-volume" });
await commandTick();
assert.equal(controlResults.at(-1).success, false);
assert.equal(actions.length, actionCount);
playback.getVolume = () => { throw new Error("Unavailable device"); };
volumeUpdate();
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).volume_percent, null);
assert.equal(messages.at(-1).track_uri, player._state.item.uri);
playback.getVolume = function () { return this.volume; };
playback.volume = 0.37;
volumeUpdate();
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).volume_percent, 37);
const setVolume = playback.setVolume;
playback.setVolume = async () => { throw new Error("Volume RPC failed"); };
commandQueue.push({ name: "volume", value: 0, id: "failed-volume" });
await commandTick();
assert.equal(controlResults.at(-1).id, "failed-volume");
assert.equal(controlResults.at(-1).success, false);
assert.equal(playback.volume, 0.37);
playback.setVolume = setVolume;
registry._map.set(Symbol.for("RequestBuilder"), requestBuilder);
tick();
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).isrc, "USUM71703861");
assert.equal(metadataUrls[0], "https://spclient.wg.spotify.com/metadata/4/track/13e54dcaa2a14cac882dacb96fe8f693", "Metadata must use the 128-bit GID, not the base62 track ID");
update({ data: { ...player._state, isPaused: true, positionAsOfTimestamp: 9000 } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).progress_ms, 9000);
assert.equal(messages.at(-1).is_playing, false);
update({ data: { ...player._state, isPaused: false, positionAsOfTimestamp: 2000, timestamp: Date.now() } });
await new Promise((resolve) => setImmediate(resolve));
assert.ok(messages.at(-1).progress_ms >= 2000 && messages.at(-1).progress_ms < 2100);
assert.equal(metadataCalls, 1);
metadataMode = "api";
update({ data: { ...player._state, item: { ...player._state.item, uri: "spotify:track:6rqhFgbbKwnb9MLmUQDhG6" } } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).isrc, "GBUM71703861");
assert.equal(metadataCalls, 2);
metadataMode = "none";
update({ data: { ...player._state, item: { ...player._state.item, uri: "spotify:track:3GCdLUSnKSMJhs4Tj6CV3s" } } });
await new Promise((resolve) => setImmediate(resolve));
assert.ok(metadataUrls.includes("https://spclient.wg.spotify.com/metadata/4/track/7927b778eb7c4124947e6e83aee81d4a"));
assert.ok(metadataUrls.every((url) => url.startsWith("https://spclient.wg.spotify.com/metadata/4/track/")));
for (const id of ["0fAkEtRaCk1D0000000abc", "0000000000000000000001", "ZZZZZZZZZZZZZZZZZZZZZZ"]) {
  const count = metadataUrls.length;
  update({ data: { ...player._state, item: { ...player._state.item, uri: `spotify:track:${id}` } } });
  await new Promise((resolve) => setImmediate(resolve));
  const urls = metadataUrls.slice(count);
  if (id[0] === "Z") assert.ok(urls.every((url) => !url.includes("metadata/4/track/")), "An overflowing ID must not create an invalid GID");
  else {
    const hex = id.startsWith("0f") ? "08428230d7ce24860143a3c2e112acde" : "00000000000000000000000000000001";
    assert.equal(urls[0], `https://spclient.wg.spotify.com/metadata/4/track/${hex}`);
  }
}
metadataMode = "none";
const retryTrack = "spotify:track:0000000000000000000002";
update({ data: { ...player._state, item: { ...player._state.item, uri: retryTrack } } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).isrc, null);
const firstMissCalls = metadataCalls;
update({ data: { ...player._state, item: { ...player._state.item, uri: retryTrack } } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(metadataCalls, firstMissCalls);
bridgeNow += 16000;
update({ data: { ...player._state, item: { ...player._state.item, uri: retryTrack } } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(metadataCalls, firstMissCalls, "A missing ISRC must not be retried after 15 seconds");
bridgeNow += 300001;
update({ data: { ...player._state, item: { ...player._state.item, uri: retryTrack } } });
await new Promise((resolve) => setImmediate(resolve));
assert.ok(metadataCalls > firstMissCalls);
metadataMode = "delayed";
const delayedTrack = "spotify:track:0000000000000000000003";
update({ data: { ...player._state, item: { ...player._state.item, uri: delayedTrack } } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).track_uri, delayedTrack, "Playback must publish before a slow ISRC lookup finishes");
assert.equal(messages.at(-1).isrc, null);
update({ data: { ...player._state, item: { ...player._state.item, uri: delayedTrack }, isPaused: true, positionAsOfTimestamp: 17000 } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).is_playing, false, "Pause updates must not wait for ISRC");
releaseMetadata({ external_ids: { isrc: "USUM71703861" } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).track_uri, delayedTrack);
assert.equal(messages.at(-1).isrc, "USUM71703861", "Late metadata must trigger a fresh snapshot");
update({ data: { ...player._state, item: { ...player._state.item, uri: "spotify:track:0000000000000000000004" } } });
await new Promise((resolve) => setImmediate(resolve));
const releaseOldTrack = releaseMetadata;
update({ data: { ...player._state, item: { ...player._state.item, uri: delayedTrack } } });
await new Promise((resolve) => setImmediate(resolve));
releaseOldTrack({ external_ids: { isrc: "GBUM71703861" } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).track_uri, delayedTrack);
assert.equal(messages.at(-1).isrc, "USUM71703861", "A previous track's late metadata must not replace the active ISRC");
metadataMode = "none";
const skipTrack = "spotify:track:0000000000000000000005";
const beforeSkip = metadataCalls;
update({ data: { ...player._state, item: { ...player._state.item, uri: skipTrack } } });
update({ data: { ...player._state, item: { ...player._state.item, uri: delayedTrack } } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(metadataCalls, beforeSkip, "A skipped track must not trigger a metadata request");
update({ data: { ...player._state, item: { ...player._state.item, uri: skipTrack } } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(metadataCalls, beforeSkip + 1, "A quickly revisited track must not inherit a negative cache entry");
metadataMode = "api";
holdFetch = true;
tick();
await new Promise((resolve) => setImmediate(resolve));
update({ data: { ...player._state, isPaused: true, positionAsOfTimestamp: 20000 } });
releaseFetch();
await new Promise((resolve) => setImmediate(resolve));
assert.equal(messages.at(-1).progress_ms, 20000);
assert.equal(messages.at(-1).is_playing, false);
bridgeNow = Date.now();
for (const [overrides, expected, playing] of [
  [{ speed: 1 }, 52000, true],
  [{ speed: 0.5 }, 51000, true],
  [{ speed: 2 }, 54000, true],
  [{ isPaused: true }, 50000, false],
  [{ isBuffering: true }, 50000, false],
  [{ hasContext: false }, 50000, false],
  [{ speed: 0 }, 50000, false],
]) {
  update({ data: { ...player._state, positionAsOfTimestamp: 50000, timestamp: bridgeNow - 2000, ...overrides } });
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(messages.at(-1).progress_ms, expected);
  assert.equal(messages.at(-1).is_playing, playing);
  assert.equal(messages.at(-1).playback_speed, overrides.speed ?? 1);
}
const beforeEpisode = metadataCalls;
update({ data: { ...player._state, item: { ...player._state.item, uri: "spotify:episode:0BxE4FqsDD1Ot4YuBXwAPp" } } });
await new Promise((resolve) => setImmediate(resolve));
assert.equal(metadataCalls, beforeEpisode, "Episodes must not request track metadata");
console.log("Spotify XPUI checks passed");
