'use strict';

const crypto = require('crypto');
const { spawn } = require('child_process');
const { WebSocket } = require('ws');

const port = 18000 + Math.floor(Math.random() * 1000);
const baseUrl = `http://127.0.0.1:${port}`;
const wsBase = `ws://127.0.0.1:${port}/relay`;

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

async function waitForHealth() {
  let lastError;
  for (let i = 0; i < 50; i += 1) {
    try {
      const response = await fetch(`${baseUrl}/health`);
      if (response.ok) return;
    } catch (error) {
      lastError = error;
    }
    await delay(100);
  }
  throw lastError || new Error('relay did not become healthy');
}

async function postJson(url) {
  const response = await fetch(url, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: '{}'
  });
  const body = await response.json();
  if (!response.ok) throw new Error(`${url} -> ${response.status}: ${JSON.stringify(body)}`);
  return body;
}

async function putSnapshot(code, hostToken, name, payload) {
  const response = await fetch(`${baseUrl}/api/session/${encodeURIComponent(code)}/snapshot`, {
    method: 'PUT',
    headers: {
      Authorization: `Bearer ${hostToken}`,
      'content-type': 'application/octet-stream',
      'x-coi-save-name': name
    },
    body: payload
  });
  const body = await response.json();
  if (!response.ok) throw new Error(`snapshot upload -> ${response.status}: ${JSON.stringify(body)}`);
  return body;
}

async function getSnapshotMeta(code, clientToken) {
  const response = await fetch(`${baseUrl}/api/session/${encodeURIComponent(code)}/snapshot/meta`, {
    headers: { Authorization: `Bearer ${clientToken}` }
  });
  const body = await response.json();
  if (!response.ok) throw new Error(`snapshot meta -> ${response.status}: ${JSON.stringify(body)}`);
  return body;
}

async function getSnapshot(code, clientToken) {
  const response = await fetch(`${baseUrl}/api/session/${encodeURIComponent(code)}/snapshot`, {
    headers: { Authorization: `Bearer ${clientToken}` }
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(`snapshot download -> ${response.status}: ${text}`);
  }
  const bytes = Buffer.from(await response.arrayBuffer());
  return {
    bytes,
    sha256: response.headers.get('x-coi-save-sha256'),
    name: response.headers.get('x-coi-save-name')
  };
}

function openSocket(code, role, lane, token) {
  return new Promise((resolve, reject) => {
    const url = `${wsBase}?code=${encodeURIComponent(code)}&role=${role}&lane=${lane}`;
    const ws = new WebSocket(url, {
      headers: { Authorization: `Bearer ${token}` }
    });
    const timer = setTimeout(() => reject(new Error(`${role} lane=${lane} websocket open timeout`)), 5000);
    ws.once('open', () => {
      clearTimeout(timer);
      resolve(ws);
    });
    ws.once('error', error => {
      clearTimeout(timer);
      reject(error);
    });
  });
}

function waitForBinary(ws, expected) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      cleanup();
      reject(new Error(`binary receive timeout; expected ${expected.toString('hex')}`));
    }, 5000);

    function cleanup() {
      clearTimeout(timer);
      ws.off('message', onMessage);
      ws.off('error', onError);
    }

    function onError(error) {
      cleanup();
      reject(error);
    }

    function onMessage(data, isBinary) {
      if (!isBinary) return;
      const actual = Buffer.from(data);
      if (!actual.equals(expected)) {
        cleanup();
        reject(new Error(`binary mismatch: ${actual.toString('hex')} != ${expected.toString('hex')}`));
        return;
      }
      cleanup();
      resolve();
    }

    ws.on('message', onMessage);
    ws.on('error', onError);
  });
}

async function expectJoinResyncRequired(code) {
  const response = await fetch(`${baseUrl}/api/session/${encodeURIComponent(code)}/join`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: '{}'
  });
  const body = await response.json();
  if (response.status !== 409 || body.error !== 'resync_required') {
    throw new Error(`expected resync_required join rejection, got ${response.status}: ${JSON.stringify(body)}`);
  }
}

async function expectGameplayReconnectRejected(code, token) {
  let rejected = false;
  try {
    const socket = await openSocket(code, 'client', 0, token);
    try { socket.terminate(); } catch { }
  } catch (error) {
    rejected = /409/.test(String(error && error.message ? error.message : error));
  }
  if (!rejected) throw new Error('gameplay reconnect was not rejected with HTTP 409');
}

async function main() {
  const server = spawn(process.execPath, ['server.js'], {
    cwd: __dirname,
    env: {
      ...process.env,
      PORT: String(port),
      SESSION_TTL_MS: '60000',
      MAX_SNAPSHOT_BYTES: String(2 * 1024 * 1024)
    },
    stdio: ['ignore', 'pipe', 'pipe']
  });

  server.stdout.on('data', chunk => process.stdout.write(`[relay] ${chunk}`));
  server.stderr.on('data', chunk => process.stderr.write(`[relay] ${chunk}`));

  let host;
  let client;
  let previewHost;
  let previewClient;
  let previewClient2;
  try {
    await waitForHealth();

    const created = await postJson(`${baseUrl}/api/session`);
    if (!created.code || !created.hostToken) throw new Error('create session response missing fields');

    const snapshotPayload = Buffer.concat([
      Buffer.from('COI-SNAPSHOT-SMOKE\0', 'utf8'),
      crypto.randomBytes(8192)
    ]);
    const expectedSnapshotHash = crypto.createHash('sha256').update(snapshotPayload).digest('hex').toUpperCase();
    const uploaded = await putSnapshot(created.code, created.hostToken, 'COOP_SMOKE_HOST.save', snapshotPayload);
    if (uploaded.sha256 !== expectedSnapshotHash
        || uploaded.size !== snapshotPayload.length
        || uploaded.name !== 'COOP_SMOKE_HOST.save') {
      throw new Error(`snapshot upload metadata mismatch: ${JSON.stringify(uploaded)}`);
    }

    const joined = await postJson(`${baseUrl}/api/session/${encodeURIComponent(created.code)}/join`);
    if (!joined.clientToken) throw new Error('join response missing client token');
    if (!joined.snapshot
        || joined.snapshot.sha256 !== expectedSnapshotHash
        || joined.snapshot.size !== snapshotPayload.length) {
      throw new Error(`join response missing correct snapshot metadata: ${JSON.stringify(joined)}`);
    }

    const meta = await getSnapshotMeta(created.code, joined.clientToken);
    if (meta.sha256 !== expectedSnapshotHash || meta.size !== snapshotPayload.length) {
      throw new Error(`snapshot metadata mismatch: ${JSON.stringify(meta)}`);
    }

    const downloaded = await getSnapshot(created.code, joined.clientToken);
    const downloadedHash = crypto.createHash('sha256').update(downloaded.bytes).digest('hex').toUpperCase();
    if (!downloaded.bytes.equals(snapshotPayload)
        || downloadedHash !== expectedSnapshotHash
        || downloaded.sha256 !== expectedSnapshotHash
        || downloaded.name !== 'COOP_SMOKE_HOST.save') {
      throw new Error('snapshot download did not match the exact uploaded bytes/hash/name');
    }

    host = await openSocket(created.code, 'host', 0, created.hostToken);
    client = await openSocket(created.code, 'client', 0, joined.clientToken);

    const hostPayload = Buffer.from([0x43, 0x4f, 0x49, 0x01, 0x02, 0x03]);
    const clientPayload = Buffer.from([0x99, 0x10, 0x20, 0x30, 0x40]);

    const clientReceived = waitForBinary(client, hostPayload);
    host.send(hostPayload, { binary: true });
    await clientReceived;

    const hostReceived = waitForBinary(host, clientPayload);
    client.send(clientPayload, { binary: true });
    await hostReceived;

    // Sidecar lanes are allowed to recover independently because they do not own
    // authoritative simulation state.
    previewHost = await openSocket(created.code, 'host', 1, created.hostToken);
    previewClient = await openSocket(created.code, 'client', 1, joined.clientToken);
    previewClient.terminate();
    previewClient = null;
    await delay(150);
    previewClient2 = await openSocket(created.code, 'client', 1, joined.clientToken);

    // Gameplay remains fail-closed after disconnect. Conservative recovery uses
    // a fresh session with a newly published host snapshot.
    client.terminate();
    client = null;
    await delay(200);
    await expectJoinResyncRequired(created.code);
    await expectGameplayReconnectRejected(created.code, joined.clientToken);

    console.log('RELAY SMOKE PASS - snapshot round-trip is byte-identical, binary relay works, sidecars reconnect, gameplay reconnect requires fresh-snapshot resync');
  } finally {
    try { host?.terminate(); } catch { }
    try { client?.terminate(); } catch { }
    try { previewHost?.terminate(); } catch { }
    try { previewClient?.terminate(); } catch { }
    try { previewClient2?.terminate(); } catch { }
    try { server.kill('SIGTERM'); } catch { }
    await delay(200);
    if (!server.killed) {
      try { server.kill('SIGKILL'); } catch { }
    }
  }
}

main().catch(error => {
  console.error('RELAY SMOKE FAIL:', error && error.stack ? error.stack : error);
  process.exitCode = 1;
});
