// NAME: Spotify Local Bridge
// DESCRIPTION: Reads Spotify's PlayerAPI and exposes playback to a loopback-only companion.

(function SpotifyLocalBridge() {
  const endpoint = "http://127.0.0.1:48711/ingest";
  const commandsEndpoint = "http://127.0.0.1:48711/commands";
  const controlResultEndpoint = "http://127.0.0.1:48711/control-result";
  let pending = false;
  let commandsPending = false;
  let warned = false;
  let player = null;
  let library = null;
  let cosmos = null;
  let registry = null;
  let latestState = null;
  let queued = false;
  const isrcFallbackCache = new Map();

  function resolve(name) {
    const token = [...(registry?._map?.keys() ?? [])].find((key) => key?.description === name);
    return token ? registry.resolve(token) : null;
  }

  window.__spotifyLocalBridgeStart = (services) => {
    if (player) return;
    registry = services;
    const connect = () => {
      try {
        player = resolve("PlayerAPI");
        cosmos = player?._cosmos ?? resolve("Cosmos");
        if (!player?._state) player = null;
      } catch {
        player = null;
      }
      if (!player) setTimeout(connect, 500);
      else {
        latestState = player._state;
        player._events?.addListener?.("update", ({ data }) => {
          latestState = data ?? null;
          publish();
        });
        publish();
        pollCommands();
      }
    };
    connect();
    setInterval(publish, 1000);
    setInterval(pollCommands, 100);
  };

  async function pollCommands() {
    if (!player || commandsPending) return;
    commandsPending = true;
    let longPoll = false;
    try {
      const response = await fetch(commandsEndpoint, { cache: "no-store" });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      longPoll = response.headers?.get?.("X-Spotify-Local-Bridge-Long-Poll") === "1";
      const commands = await response.json();
      for (const command of commands) await executeCommand(command);
    } catch (error) {
      if (!warned) console.warn("Spotify Local Bridge is waiting for its local server:", error);
      warned = true;
    } finally {
      commandsPending = false;
      if (longPoll) pollCommands();
    }
  }

  async function executeCommand(command) {
    if (!command || typeof command.name !== "string") return;
    if (command.name === "library-check" || command.name === "library-add" || command.name === "library-remove") {
      library ??= resolve("LibraryAPI");
      const uris = Array.isArray(command.uris) ? command.uris.filter((uri) => typeof uri === "string") : [];
      if (!library || uris.length !== command.uris?.length || uris.length === 0)
        return reportControl(command.name, false, "LibraryAPI method not found or invalid URIs", command.id);
      try {
        if (command.name === "library-check") {
          const values = await library.contains(...uris);
          await reportControl(command.name, true, null, command.id, Array.isArray(values) ? values.map(Boolean) : null);
        } else {
          const method = command.name === "library-add" ? library.add : library.remove;
          if (typeof method !== "function") throw new Error("LibraryAPI mutation method not found");
          await method.call(library, { uris });
          await reportControl(command.name, true, null, command.id);
        }
      } catch (error) {
        await reportControl(command.name, false, error?.message ?? String(error), command.id);
      }
      return;
    }
    let action;
    let args = [];
    switch (command.name) {
      case "pause": action = player.pause; break;
      case "resume": action = player.resume; break;
      case "next": action = player.skipToNext ?? player.nextTrack ?? player.skipNext; break;
      case "previous": action = player.skipToPrevious ?? player.previousTrack ?? player.skipPrevious; break;
      case "seek": {
        const position = Number(command.value);
        if (!Number.isFinite(position) || position < 0) return reportControl(command.name, false, "invalid position");
        action = player.seekTo ?? player.seek;
        args = [position];
        break;
      }
      case "shuffle":
        action = player.setShuffle;
        args = [Number(command.value) === 1];
        break;
      case "repeat":
        action = player.setRepeat;
        args = [Number(command.value)];
        break;
      default: return;
    }
    if (typeof action !== "function") return reportControl(command.name, false, "PlayerAPI method not found", command.id);
    try {
      await action.apply(player, args);
      await reportControl(command.name, true, null, command.id);
    } catch (error) {
      await reportControl(command.name, false, error?.message ?? String(error), command.id);
    }
  }

  async function reportControl(name, success, error = null, id = null, values = null) {
    try {
      await fetch(controlResultEndpoint, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id, name, success, error, values }),
      });
    } catch { }
  }

  function cleanIsrc(value) {
    if (typeof value !== "string") return null;
    const cleaned = value.replace(/[^A-Za-z0-9]/g, "").toUpperCase();
    return /^[A-Z]{2}[A-Z0-9]{3}\d{7}$/.test(cleaned) ? cleaned : null;
  }

  function findIsrc(value, depth = 0, seen = new Set()) {
    if (!value || depth > 10 || (typeof value !== "object" && typeof value !== "function") || seen.has(value)) return null;
    seen.add(value);

    try {
      if (typeof value.toJSON === "function") {
        const json = value.toJSON();
        const fromJson = findIsrc(json, depth + 1, seen);
        if (fromJson) return fromJson;
      }
    } catch { }

    const keys = new Set(Object.keys(value));
    try {
      for (const key of Object.getOwnPropertyNames(value)) keys.add(key);
      const prototype = Object.getPrototypeOf(value);
      if (prototype) for (const key of Object.getOwnPropertyNames(prototype)) keys.add(key);
    } catch { }

    for (const key of keys) {
      let child;
      try { child = value[key]; } catch { continue; }
      const direct = /isrc/i.test(key) ? cleanIsrc(child) : null;
      if (direct) return direct;
      if (child && typeof child === "object") {
        const record = child;
        if (/isrc/i.test(String(record.type ?? ""))) {
          const nestedDirect = cleanIsrc(record.id) || cleanIsrc(record.value);
          if (nestedDirect) return nestedDirect;
        }
        const nested = findIsrc(child, depth + 1, seen);
        if (nested) return nested;
      }
    }
    return null;
  }

  async function getTrackIsrc(item, meta) {
    const direct = [meta.isrc, meta.isrc_id, meta.track_isrc, item?.isrc,
      item?.external_ids?.isrc].map(cleanIsrc).find(Boolean);
    if (direct) return direct;
    if (Array.isArray(item?.external_id)) {
      const external = item.external_id.filter((entry) => entry?.type === "isrc")
        .map((entry) => cleanIsrc(entry.id || entry.value)).find(Boolean);
      if (external) return external;
    }
    const embedded = findIsrc(meta) || findIsrc(item);
    if (embedded) return embedded;
    const trackId = typeof item?.uri === "string" ? item.uri.split(":").pop() : null;
    cosmos ??= player?._cosmos ?? resolve("Cosmos");
    if (!trackId || !cosmos?.get) return null;
    if (isrcFallbackCache.has(trackId)) return isrcFallbackCache.get(trackId);
    const lookup = (async () => {
      for (const url of [`sp://metadata/track/${trackId}`, `https://api.spotify.com/v1/tracks/${trackId}`]) {
        try {
          const metadata = await Promise.race([
            cosmos.get(url),
            new Promise((_, reject) => setTimeout(() => reject(new Error("Cosmos timeout")), 1500)),
          ]);
          const isrc = findIsrc(metadata);
          if (isrc) return isrc;
        } catch { }
      }
      return null;
    })();
    isrcFallbackCache.set(trackId, lookup);
    return lookup;
  }

  async function snapshot() {
    const state = latestState;
    const item = state?.item;
    const meta = item?.metadata ?? {};
    const isrc = await getTrackIsrc(item, meta);
    const artists = item?.artists?.filter((artist) => artist.name) ?? [];
    const duration = Number(state?.duration ?? item?.duration?.milliseconds ?? meta.duration) || 0;
    const progress = item ? (Number(state.positionAsOfTimestamp) || 0)
      + (state.isPaused ? 0 : Math.max(0, Date.now() - (Number(state.timestamp) || Date.now()))) : 0;

    return {
      track_uri: item?.uri ?? null,
      title: item?.name ?? meta.title ?? "",
      artists: artists.length ? artists.map((artist) => artist.name)
        : (meta.artist_name ? [meta.artist_name] : []),
      artist_uris: artists.length ? artists.map((artist) => artist.uri ?? null)
        : (meta.artist_uri ? [meta.artist_uri] : []),
      album: item?.album?.name ?? meta.album_title ?? "",
      album_uri: item?.album?.uri ?? meta.album_uri ?? null,
      isrc,
      image_url: item?.album?.images?.[0]?.url ?? item?.images?.[0]?.url
        ?? meta.image_large_url ?? meta.image_url ?? null,
      is_local: Boolean(item?.isLocal),
      duration_ms: Math.max(0, Math.round(duration)),
      progress_ms: Math.max(0, Math.min(Math.round(progress), duration || Infinity)),
      is_playing: Boolean(item && !state.isPaused),
      context_uri: state?.context?.uri ?? null,
      shuffle: Boolean(state?.shuffle),
      repeat: Number(state?.repeat) || 0,
    };
  }

  async function publish() {
    if (!player) return;
    if (pending) {
      queued = true;
      return;
    }
    pending = true;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 2500);
    try {
      const response = await fetch(endpoint, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(await snapshot()),
        signal: controller.signal,
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      warned = false;
    } catch (error) {
      if (!warned) console.warn("Spotify Local Bridge is waiting for its local server:", error);
      warned = true;
    } finally {
      clearTimeout(timeout);
      pending = false;
      if (queued) {
        queued = false;
        publish();
      }
    }
  }

})();
