'use strict';

const http = require('http');
const crypto = require('crypto');
const { WebSocketServer, WebSocket } = require('ws');

const PORT = Number(process.env.PORT || 10000);
const SESSION_TTL_MS = Number(process.env.SESSION_TTL_MS || 2 * 60 * 60 * 1000);
const MAX_MESSAGE_BYTES = Number(process.env.MAX_MESSAGE_BYTES || 16 * 1024 * 1024);
const MAX_PENDING_BYTES_PER_LANE = Number(process.env.MAX_PENDING_BYTES_PER_LANE || 1024 * 1024);
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

function makeLaneState() {
  return {
    host: null,
    client: null,
    pendingHostToClient: [],
    pendingClientToHost: [],
    pendingHostBytes: 0,
    pendingClientBytes: 0
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

function disposeSession(session, reason) {
  for (const lane of session.lanes) {
    closeSocket(lane.host, 1001, reason);
    closeSocket(lane.client, 1001, reason);
    lane.host = null;
    lane.client = null;
    lane.pendingHostToClient.length = 0;
    lane.pendingClientToHost.length = 0;
    lane.pendingHostBytes = 0;
    lane.pendingClientBytes = 0;
  }
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
    req.on('data', chunk => {
      total += chunk.length;
      if (total > maxBytes) {
        reject(new Error('request body too large'));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => {
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
        expiresAt: new Date(session.expiresAt).toISOString()
      });
      return;
    }

    const joinMatch = /^\/api\/session\/([^/]+)\/join$/.exec(url.pathname);
    if (req.method === 'POST' && joinMatch) {
      await readBody(req);
      const code = normalizeCode(joinMatch[1]);
      const session = code ? sessions.get(code) : null;
      if (!session || Date.now() >= session.expiresAt) {
        json(res, 404, { error: 'session_not_found' });
        return;
      }

      // One remote player per development session. Repeated calls return the
      // same token so a launcher can be retried without creating another slot.
      session.clientClaimed = true;
      json(res, 200, {
        code: session.code,
        clientToken: session.clientToken,
        expiresAt: new Date(session.expiresAt).toISOString()
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

  for (const payload of lane.pendingHostToClient) lane.client.send(payload, { binary: true });
  for (const payload of lane.pendingClientToHost) lane.host.send(payload, { binary: true });
  lane.pendingHostToClient.length = 0;
  lane.pendingClientToHost.length = 0;
  lane.pendingHostBytes = 0;
  lane.pendingClientBytes = 0;

  try { lane.host.send('PEER_READY'); } catch { }
  try { lane.client.send('PEER_READY'); } catch { }
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
    const token = String(url.searchParams.get('token') || '');
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
