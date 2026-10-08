// NAME: Spotify Local Bridge
// DESCRIPTION: Exposes the Spotify client's playback state to a loopback-only companion.

(function SpotifyLocalBridge() {
  if (!window.Spicetify?.Player?.addEventListener || !window.Spicetify.Player.data) {
    setTimeout(SpotifyLocalBridge, 500);
    return;
  }

  const endpoint = "http://127.0.0.1:48711/ingest";
  let pending = false;
  let warned = false;
  let lastAttemptAt = 0;
  const isrcFallbackCache = new Map();

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
      const external = item.external_id.map((entry) => cleanIsrc(entry?.id || entry?.value)).find(Boolean);
      if (external) return external;
    }
    const embedded = findIsrc(meta) || findIsrc(item);
    if (embedded) return embedded;
    const trackId = typeof item?.uri === "string" ? item.uri.split(":").pop() : null;
    if (!trackId || !Spicetify.CosmosAsync?.get) return null;
    if (isrcFallbackCache.has(trackId)) return isrcFallbackCache.get(trackId);
    const lookup = (async () => {
      try {
        const metadata = await Promise.race([
          Spicetify.CosmosAsync.get(`sp://metadata/track/${trackId}`),
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
    const player = Spicetify.Player;
    const state = player.data;
    const item = state?.item;
    const meta = item?.metadata ?? {};
    const isrc = await getTrackIsrc(item, meta);
    const artists = item?.artists?.filter((artist) => artist.name) ?? [];
    const duration = Number(state?.duration ?? item?.duration?.milliseconds ?? meta.duration) || 0;
    let progress = 0;
    if (item) {
      try {
        progress = Number(player.getProgress()) || 0;
      } catch {
        progress = Number(state?.positionAsOfTimestamp) || 0;
      }
    }

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
    if (pending) return;
    pending = true;
    lastAttemptAt = Date.now();
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
    }
  }

  Spicetify.Player.addEventListener("songchange", publish);
  Spicetify.Player.addEventListener("onplaypause", publish);
  Spicetify.Player.addEventListener("onprogress", () => {
    if (Date.now() - lastAttemptAt >= 1000) publish();
  });
  setInterval(publish, 1000);
  publish();
})();
