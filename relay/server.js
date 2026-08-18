'use strict';

const http = require('http');
const crypto = require('crypto');
const { WebSocketServer, WebSocket } = require('ws');

const PORT = Number(process.env.PORT || 10000);
const SESSION_TTL_MS = Number(process.env.SESSION_TTL_MS || 2 * 60 * 60 * 1000);
const MAX_MESSAGE_BYTES = Number(process.env.MAX_MESSAGE_BYTES || 16 * 1024 * 1024);
const MAX_PENDING_BYTES_PER_LANE = Number(process.env.MAX_PENDING_BYTES_PER_LANE || 1024 * 1024);
const MAX_SNAPSHOT_BYTES = Number(process.env.MAX_SNAPSHOT_BYTES || 64 * 1024 * 1024);
const HEARTBEAT_MS = Number(process.env.HEARTBEAT_MS || 25000);
const SESSION_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
const SESSION_CODE_CHARS = 8;
const LANE_NAMES = ['gameplay', 'preview', 'path', 'blueprint'];

const sessions = new Map();
const rateBuckets = new Map();

function json(res, status, body) {
  const payload = Buffer.from(JSON.stringify(body));
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': String(payload.length),
    'cache-control': 'no-store'
  });
  res.end(payload);
}

function requestIp(req) {
  const forwarded = String(req.headers['x-forwarded-for'] || '').split(',')[0].trim();
  return forwarded || req.socket.remoteAddress || 'unknown';
}

function allowApiRequest(req) {
  const ip = requestIp(req);
  const now = Date.now();
  let bucket = rateBuckets.get(ip);
  if (!bucket || now - bucket.startedAt >= 60_000) {
    bucket = { startedAt: now, count: 0 };
    rateBuckets.set(ip, bucket);
  }
  bucket.count += 1;
  return bucket.count <= 30;
}

function randomToken() {
  return crypto.randomBytes(32).toString('base64url');
}

function randomCode() {
  let raw = '';
  for (let i = 0; i < SESSION_CODE_CHARS; i += 1) {
    raw += SESSION_ALPHABET[crypto.randomInt(0, SESSION_ALPHABET.length)];
  }
  return raw.slice(0, 4) + '-' + raw.slice(4);
}

function normalizeCode(value) {
  const raw = String(value || '').toUpperCase().replace(/[^A-Z0-9]/g, '');
  if (raw.length !== SESSION_CODE_CHARS) return null;
  return raw.slice(0, 4) + '-' + raw.slice(4);
}

function bearerToken(req) {
  const header = String(req.headers.authorization || '');
  const match = /^Bearer\s+(.+)$/i.exec(header);
  return match ? match[1].trim() : '';
}

function makeLaneState() {
  return {
    host: null,
    client: null,
    pendingHostToClient: [],
    pendingClientToHost: [],
    pendingHostBytes: 0,
    pendingClientBytes: 0,
    everPaired: false,
    resyncRequired: false
  };
}

function createSession() {
  let code;
  do {
    code = randomCode();
  } while (sessions.has(code));

  const now = Date.now();
  const session = {
    code,
    hostToken: randomToken(),
    clientToken: randomToken(),
    createdAt: now,
    expiresAt: now + SESSION_TTL_MS,
    clientClaimed: false,
    snapshot: null,
    lanes: LANE_NAMES.map(() => makeLaneState())
  };
  sessions.set(code, session);
  return session;
}

function closeSocket(socket, code, reason) {
  if (!socket) return;
  try {
    socket.close(code, reason);
  } catch {
    try { socket.terminate(); } catch { }
  }
}

function clearPending(lane) {
  lane.pendingHostToClient.length = 0;
  lane.pendingClientToHost.length = 0;
  lane.pendingHostBytes = 0;
  lane.pendingClientBytes = 0;
}

function disposeSession(session, reason) {
  for (const lane of session.lanes) {
    closeSocket(lane.host, 1001, reason);
    closeSocket(lane.client, 1001, reason);
    lane.host = null;
    lane.client = null;
    clearPending(lane);
  }
  session.snapshot = null;
  sessions.delete(session.code);
}

function cleanupExpired() {
  const now = Date.now();
  for (const session of sessions.values()) {
    if (now >= session.expiresAt) disposeSession(session, 'session expired');
  }

  for (const [ip, bucket] of rateBuckets.entries()) {
    if (now - bucket.startedAt > 5 * 60_000) rateBuckets.delete(ip);
  }
}

function readBody(req, maxBytes = 64 * 1024) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let total = 0;
    let tooLarge = false;
    req.on('data', chunk => {
      if (tooLarge) return;
      total += chunk.length;
      if (total > maxBytes) {
        tooLarge = true;
        chunks.length = 0;
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => {
      if (tooLarge) {
        const error = new Error('request body too large');
        error.code = 'BODY_TOO_LARGE';
        reject(error);
        return;
      }
      if (chunks.length === 0) {
        resolve({});
        return;
      }
      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')));
      } catch {
        reject(new Error('invalid json'));
      }
    });
    req.on('error', reject);
  });
}

function readRawBody(req, maxBytes) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let total = 0;
    let tooLarge = false;
    req.on('data', chunk => {
      if (tooLarge) return;
      total += chunk.length;
      if (total > maxBytes) {
        tooLarge = true;
        chunks.length = 0;
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => {
      if (tooLarge) {
        const error = new Error('snapshot too large');
        error.code = 'BODY_TOO_LARGE';
        reject(error);
        return;
      }
      resolve(Buffer.concat(chunks));
    });
    req.on('error', reject);
  });
}

function safeSnapshotName(value) {
  const raw = String(value || 'COI_COOP_SYNC.save').trim();
  const leaf = raw.replace(/^.*[\\/]/, '').replace(/[^A-Za-z0-9._ -]/g, '_').slice(0, 120);
  return leaf || 'COI_COOP_SYNC.save';
}

function snapshotMeta(snapshot) {
  if (!snapshot) return null;
  return {
    name: snapshot.name,
    size: snapshot.buffer.length,
    sha256: snapshot.sha256,
    publishedAt: snapshot.publishedAt
  };
}

function findSession(codeValue) {
  const code = normalizeCode(codeValue);
  const session = code ? sessions.get(code) : null;
  if (!session || Date.now() >= session.expiresAt) return null;
  return session;
}

function hasSessionToken(session, req, role) {
  if (!session) return false;
  const token = bearerToken(req);
  if (role === 'host') return token === session.hostToken;
  if (role === 'client') return token === session.clientToken;
  return token === session.hostToken || token === session.clientToken;
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);

    if (req.method === 'GET' && url.pathname === '/health') {
      json(res, 200, { ok: true, sessions: sessions.size });
      return;
    }

    if (!allowApiRequest(req)) {
      json(res, 429, { error: 'rate_limited' });
      return;
    }

    if (req.method === 'POST' && url.pathname === '/api/session') {
      const session = createSession();
      json(res, 201, {
        code: session.code,
        hostToken: session.hostToken,
        expiresAt: new Date(session.expiresAt).toISOString(),
        maxSnapshotBytes: MAX_SNAPSHOT_BYTES
      });
      return;
    }

    const snapshotMetaMatch = /^\/api\/session\/([^/]+)\/snapshot\/meta$/.exec(url.pathname);
    if (req.method === 'GET' && snapshotMetaMatch) {
      const session = findSession(snapshotMetaMatch[1]);
      if (!session) {
        json(res, 404, { error: 'session_not_found' });
        return;
      }
      if (!hasSessionToken(session, req, 'either')) {
        json(res, 401, { error: 'unauthorized' });
        return;
      }
      if (!session.snapshot) {
        json(res, 404, { error: 'snapshot_not_found' });
        return;
      }
      json(res, 200, snapshotMeta(session.snapshot));
      return;
    }

    const snapshotMatch = /^\/api\/session\/([^/]+)\/snapshot$/.exec(url.pathname);
    if (req.method === 'PUT' && snapshotMatch) {
      const session = findSession(snapshotMatch[1]);
      if (!session) {
        json(res, 404, { error: 'session_not_found' });
        return;
      }
      if (!hasSessionToken(session, req, 'host')) {
        json(res, 401, { error: 'unauthorized' });
        return;
      }

      let buffer;
      try {
        buffer = await readRawBody(req, MAX_SNAPSHOT_BYTES);
      } catch (error) {
        if (error && error.code === 'BODY_TOO_LARGE') {
          json(res, 413, { error: 'snapshot_too_large', maxBytes: MAX_SNAPSHOT_BYTES });
          return;
        }
        throw error;
      }
      if (buffer.length === 0) {
        json(res, 400, { error: 'snapshot_empty' });
        return;
      }

      const sha256 = crypto.createHash('sha256').update(buffer).digest('hex').toUpperCase();
      session.snapshot = {
        name: safeSnapshotName(req.headers['x-coi-save-name']),
        buffer,
        sha256,
        publishedAt: new Date().toISOString()
      };
      json(res, 201, snapshotMeta(session.snapshot));
      console.log(`snapshot published session=${session.code} bytes=${buffer.length} sha256=${sha256}`);
      return;
    }

    if (req.method === 'GET' && snapshotMatch) {
      const session = findSession(snapshotMatch[1]);
      if (!session) {
        json(res, 404, { error: 'session_not_found' });
        return;
      }
      if (!hasSessionToken(session, req, 'either')) {
        json(res, 401, { error: 'unauthorized' });
        return;
      }
      if (!session.snapshot) {
        json(res, 404, { error: 'snapshot_not_found' });
        return;
      }

      const snapshot = session.snapshot;
      res.writeHead(200, {
        'content-type': 'application/octet-stream',
        'content-length': String(snapshot.buffer.length),
        'cache-control': 'no-store',
        'x-coi-save-name': snapshot.name,
        'x-coi-save-sha256': snapshot.sha256
      });
      res.end(snapshot.buffer);
      return;
    }

    const joinMatch = /^\/api\/session\/([^/]+)\/join$/.exec(url.pathname);
    if (req.method === 'POST' && joinMatch) {
      await readBody(req);
      const session = findSession(joinMatch[1]);
      if (!session) {
        json(res, 404, { error: 'session_not_found' });
        return;
      }

      // Until real in-process snapshot/catch-up exists, a gameplay disconnect
      // invalidates this deterministic session. Recovery starts a new session
      // from a freshly published host snapshot.
      if (session.lanes[0].resyncRequired) {
        json(res, 409, {
          error: 'resync_required',
          reason: 'gameplay_disconnected_restart_session'
        });
        return;
      }

      // One remote player per development session. Repeated calls return the
      // same token while the original gameplay session has not been invalidated.
      session.clientClaimed = true;
      json(res, 200, {
        code: session.code,
        clientToken: session.clientToken,
        expiresAt: new Date(session.expiresAt).toISOString(),
        snapshot: snapshotMeta(session.snapshot)
      });
      return;
    }

    json(res, 404, { error: 'not_found' });
  } catch (error) {
    console.error('HTTP error', error);
    if (!res.headersSent) json(res, 500, { error: 'internal_error' });
    else res.end();
  }
});

const wss = new WebSocketServer({
  noServer: true,
  maxPayload: MAX_MESSAGE_BYTES,
  perMessageDeflate: false
});

function queuePending(lane, role, payload) {
  const buffer = Buffer.from(payload);
  if (role === 'host') {
    lane.pendingHostToClient.push(buffer);
    lane.pendingHostBytes += buffer.length;
    while (lane.pendingHostBytes > MAX_PENDING_BYTES_PER_LANE && lane.pendingHostToClient.length > 0) {
      lane.pendingHostBytes -= lane.pendingHostToClient.shift().length;
    }
  } else {
    lane.pendingClientToHost.push(buffer);
    lane.pendingClientBytes += buffer.length;
    while (lane.pendingClientBytes > MAX_PENDING_BYTES_PER_LANE && lane.pendingClientToHost.length > 0) {
      lane.pendingClientBytes -= lane.pendingClientToHost.shift().length;
    }
  }
}

function flushPending(lane) {
  if (!lane.host || !lane.client) return;
  if (lane.host.readyState !== WebSocket.OPEN || lane.client.readyState !== WebSocket.OPEN) return;

  lane.everPaired = true;

  for (const payload of lane.pendingHostToClient) lane.client.send(payload, { binary: true });
  for (const payload of lane.pendingClientToHost) lane.host.send(payload, { binary: true });
  clearPending(lane);

  try { lane.host.send('PEER_READY'); } catch { }
  try { lane.client.send('PEER_READY'); } catch { }
}

function invalidateGameplayLane(session, lane, disconnectedRole) {
  if (!lane.everPaired || lane.resyncRequired) return;

  lane.resyncRequired = true;
  clearPending(lane);
  console.warn(`gameplay resync required session=${session.code} after ${disconnectedRole} disconnect`);

  const peerRole = disconnectedRole === 'host' ? 'client' : 'host';
  const peer = lane[peerRole];
  if (peer && peer.readyState === WebSocket.OPEN) {
    closeSocket(peer, 1012, 'RESYNC_REQUIRED');
  }
}

function attachPeer(session, role, laneIndex, ws) {
  const lane = session.lanes[laneIndex];
  const previous = lane[role];
  if (previous && previous !== ws) closeSocket(previous, 1012, 'replaced by reconnect');
  lane[role] = ws;
  ws.isAlive = true;
  ws.sessionCode = session.code;
  ws.role = role;
  ws.laneIndex = laneIndex;

  ws.on('pong', () => { ws.isAlive = true; });

  ws.on('message', (data, isBinary) => {
    if (!isBinary) return;
    const peerRole = role === 'host' ? 'client' : 'host';
    const peer = lane[peerRole];
    if (peer && peer.readyState === WebSocket.OPEN) {
      peer.send(data, { binary: true });
    } else {
      queuePending(lane, role, data);
    }
  });

  ws.on('close', () => {
    if (lane[role] === ws) lane[role] = null;
    if (laneIndex === 0) invalidateGameplayLane(session, lane, role);
  });

  ws.on('error', error => {
    console.warn(`WebSocket ${session.code} ${role} lane=${laneIndex} error: ${error.message}`);
  });

  flushPending(lane);
  console.log(`relay connected session=${session.code} role=${role} lane=${laneIndex}:${LANE_NAMES[laneIndex]}`);
}

server.on('upgrade', (req, socket, head) => {
  try {
    const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);
    if (url.pathname !== '/relay') {
      socket.destroy();
      return;
    }

    const code = normalizeCode(url.searchParams.get('code'));
    const role = String(url.searchParams.get('role') || '').toLowerCase();
    const token = bearerToken(req);
    const laneIndex = Number(url.searchParams.get('lane'));
    const session = code ? sessions.get(code) : null;

    if (!session
        || Date.now() >= session.expiresAt
        || (role !== 'host' && role !== 'client')
        || !Number.isInteger(laneIndex)
        || laneIndex < 0
        || laneIndex >= LANE_NAMES.length
        || token !== (role === 'host' ? session.hostToken : session.clientToken)) {
      socket.write('HTTP/1.1 401 Unauthorized\r\nConnection: close\r\n\r\n');
      socket.destroy();
      return;
    }

    if (laneIndex === 0 && session.lanes[0].resyncRequired) {
      socket.write(
        'HTTP/1.1 409 Conflict\r\n'
        + 'Connection: close\r\n'
        + 'Content-Type: text/plain; charset=utf-8\r\n'
        + 'X-COI-Coop-Reason: RESYNC_REQUIRED\r\n'
        + 'Content-Length: 15\r\n\r\n'
        + 'RESYNC_REQUIRED');
      socket.destroy();
      return;
    }

    wss.handleUpgrade(req, socket, head, ws => attachPeer(session, role, laneIndex, ws));
  } catch {
    socket.destroy();
  }
});

const heartbeat = setInterval(() => {
  cleanupExpired();
  for (const ws of wss.clients) {
    if (ws.isAlive === false) {
      try { ws.terminate(); } catch { }
      continue;
    }
    ws.isAlive = false;
    try { ws.ping(); } catch { }
  }
}, HEARTBEAT_MS);
heartbeat.unref();

server.listen(PORT, '0.0.0.0', () => {
  console.log(`COI-Coop relay listening on 0.0.0.0:${PORT}`);
});

function shutdown() {
  clearInterval(heartbeat);
  for (const session of [...sessions.values()]) disposeSession(session, 'server shutdown');
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 10_000).unref();
}

process.on('SIGTERM', shutdown);
process.on('SIGINT', shutdown);
