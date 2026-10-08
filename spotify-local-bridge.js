// NAME: Spotify Local Bridge
// DESCRIPTION: Reads Spotify's PlayerAPI and exposes playback to a loopback-only companion.

(function SpotifyLocalBridge() {
  const endpoint = "http://127.0.0.1:48711/ingest";
  const commandsEndpoint = "http://127.0.0.1:48711/commands";
  let pending = false;
  let commandsPending = false;
  let warned = false;
  let player = null;
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
    try {
      const response = await fetch(commandsEndpoint, { cache: "no-store" });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const commands = await response.json();
      for (const command of commands) await executeCommand(command);
    } catch (error) {
      if (!warned) console.warn("Spotify Local Bridge is waiting for its local server:", error);
      warned = true;
    } finally {
      commandsPending = false;
    }
  }

  async function executeCommand(command) {
    if (!command || typeof command.name !== "string") return;
    switch (command.name) {
      case "pause": return player.pause?.();
      case "resume": return player.resume?.();
      case "next": return player.nextTrack?.() ?? player.skipNext?.();
      case "previous": return player.previousTrack?.() ?? player.skipPrevious?.();
      case "seek": {
        const position = Number(command.value);
        if (Number.isFinite(position) && position >= 0) return player.seek?.(position);
        return;
      }
      default: return;
    }
  }

  function cleanIsrc(value) {
    if (typeof value !== "string") return null;
    const cleaned = value.replace(/[^A-Za-z0-9]/g, "").toUpperCase();
    return /^[A-Z]{2}[A-Z0-9]{3}\d{7}$/.test(cleaned) ? cleaned : null;
  }

  function findIsrc(value, depth = 0) {
    if (!value || depth > 8 || typeof value !== "object") return null;
    for (const [key, child] of Object.entries(value)) {
      const direct = /isrc/i.test(key) ? cleanIsrc(child) : null;
      if (direct) return direct;
      if (child && typeof child === "object") {
        const record = child;
        if (/isrc/i.test(String(record.type ?? ""))) {
          const nestedDirect = cleanIsrc(record.id) || cleanIsrc(record.value);
          if (nestedDirect) return nestedDirect;
        }
        const nested = findIsrc(child, depth + 1);
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
      try {
        const metadata = await Promise.race([
          cosmos.get(`sp://metadata/track/${trackId}`),
          new Promise((_, reject) => setTimeout(() => reject(new Error("Cosmos timeout")), 1500)),
        ]);
        return findIsrc(metadata);
      } catch {
        return null;
      }
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
