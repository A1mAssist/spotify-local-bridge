import http from "node:http";
import { fileURLToPath } from "node:url";

const HOST = "127.0.0.1";
const PORT = 48711;
const MAX_BODY = 16 * 1024;
const MAX_AGE_MS = 10_000;
const SPOTIFY_ORIGIN = "https://xpui.app.spotify.com";

function validState(value) {
  const text = (v, max) => typeof v === "string" && v.length <= max;
  const count = (v) => Number.isInteger(v) && v >= 0 && v <= 43_200_000;
  return value && typeof value === "object" && !Array.isArray(value)
    && (value.track_uri === null || text(value.track_uri, 512))
    && text(value.title, 1000)
    && Array.isArray(value.artists) && value.artists.length <= 20
    && value.artists.every((artist) => text(artist, 300))
    && (value.artist_uris === undefined || (Array.isArray(value.artist_uris)
      && value.artist_uris.length <= value.artists.length
      && value.artist_uris.every((uri) => uri === null || text(uri, 512))))
    && text(value.album, 1000)
    && (value.album_uri === undefined || value.album_uri === null || text(value.album_uri, 512))
    && (value.isrc === undefined || value.isrc === null || (typeof value.isrc === "string"
      && /^[A-Z]{2}[A-Z0-9]{3}\d{7}$/.test(value.isrc)))
    && (value.image_url === undefined || value.image_url === null || text(value.image_url, 1024))
    && (value.is_local === undefined || typeof value.is_local === "boolean")
    && count(value.duration_ms) && count(value.progress_ms)
    && typeof value.is_playing === "boolean"
    && (value.context_uri === null || text(value.context_uri, 512))
    && typeof value.shuffle === "boolean"
    && Number.isInteger(value.repeat) && value.repeat >= 0 && value.repeat <= 2;
}

export function createBridgeServer() {
  let state = null;
  let updatedAt = 0;

  const server = http.createServer((request, response) => {
    response.setHeader("Cache-Control", "no-store");
    if (request.headers.host !== `${HOST}:${server.address().port}`) {
      response.writeHead(403).end();
      return;
    }
    const origin = request.headers.origin;
    if (origin === SPOTIFY_ORIGIN) {
      response.setHeader("Access-Control-Allow-Origin", origin);
      response.setHeader("Vary", "Origin");
      response.setHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
      response.setHeader("Access-Control-Allow-Headers", "Content-Type");
      response.setHeader("Access-Control-Allow-Private-Network", "true");
    }

    if (request.url === "/ingest" && request.method === "OPTIONS") {
      response.writeHead(origin === SPOTIFY_ORIGIN ? 204 : 403).end();
      return;
    }

    if (request.url === "/ingest" && request.method === "POST") {
      if (origin !== SPOTIFY_ORIGIN || !request.headers["content-type"]?.startsWith("application/json")) {
        response.writeHead(403).end();
        return;
      }
      const chunks = [];
      let bytes = 0;
      let oversized = false;
      request.on("data", (chunk) => {
        if (oversized) return;
        bytes += chunk.length;
        if (bytes > MAX_BODY) {
          oversized = true;
          response.writeHead(413).end();
          return;
        }
        chunks.push(chunk);
      });
      request.on("end", () => {
        if (oversized) return;
        try {
          const next = JSON.parse(Buffer.concat(chunks).toString("utf8"));
          if (!validState(next)) throw new Error("Invalid state");
          state = next;
          updatedAt = Date.now();
          response.writeHead(204).end();
        } catch {
          response.writeHead(400).end();
        }
      });
      return;
    }

    if (request.url === "/state" && request.method === "GET") {
      if (!state || Date.now() - updatedAt > MAX_AGE_MS) {
        response.writeHead(204).end();
        return;
      }
      response.setHeader("Content-Type", "application/json; charset=utf-8");
      const elapsed = state.is_playing ? Date.now() - updatedAt : 0;
      const progress = Math.min(state.duration_ms, state.progress_ms + elapsed);
      response.writeHead(200).end(JSON.stringify({ ...state, progress_ms: progress, updated_at_ms: updatedAt }));
      return;
    }

    response.writeHead(404).end();
  });
  return server;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  createBridgeServer().listen(PORT, HOST, () => {
    console.log(`Spotify Local Bridge: http://${HOST}:${PORT}/state`);
  });
}
