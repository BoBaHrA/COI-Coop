'use strict';

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
      SESSION_TTL_MS: '60000'
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

    const joined = await postJson(`${baseUrl}/api/session/${encodeURIComponent(created.code)}/join`);
    if (!joined.clientToken) throw new Error('join response missing client token');

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

    // Gameplay is deliberately fail-closed until snapshot/catch-up exists.
    client.terminate();
    client = null;
    await delay(200);
    await expectJoinResyncRequired(created.code);
    await expectGameplayReconnectRejected(created.code, joined.clientToken);

    console.log('RELAY SMOKE PASS - binary relay works, sidecars reconnect, gameplay reconnect requires resync');
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
