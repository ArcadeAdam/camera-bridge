'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const net = require('node:net');
const { createBridgeServer } = require('../lib/http-server');
const { FrameStore } = require('../lib/frame-store');

function frame() {
  return Buffer.from([0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, 0, 240, 1, 64, 3,
    1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xff, 0xda, 0, 12,
    3, 1, 0, 2, 0x11, 3, 0x11, 0, 63, 0, 0x12, 0xff, 0xd9]);
}

async function fixture(t, options = {}) {
  let now = 1000;
  const store = new FrameStore({ now: () => now });
  store.publish(frame());
  const capture = { status: 'live', testPattern: true, reconnects: 0, lastError: null };
  let stopped = 0;
  const server = createBridgeServer({ store, capture, config: { cameraName: 'test', port: 0 },
    indexPath: __filename, onStop: () => { stopped++; }, ...options });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  t.after(() => new Promise((resolve) => { server.close(resolve); server.closeAllConnections(); }));
  return { server, store, port: server.address().port, expire: () => { now = 4001; }, stopped: () => stopped };
}

function request(port, path, options = {}) {
  return new Promise((resolve, reject) => {
    const req = http.request({ hostname: '127.0.0.1', port, path, ...options }, (res) => {
      const chunks = [];
      res.on('data', (chunk) => chunks.push(chunk));
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks) }));
      res.on('error', reject);
    });
    req.on('error', reject);
    req.end();
  });
}

test('exact hostless HTTP/1.0 game request receives a complete image and socket closes', async (t) => {
  const { port } = await fixture(t);
  const response = await new Promise((resolve, reject) => {
    const chunks = [];
    const socket = net.connect(port, '127.0.0.1', () => socket.write('GET /img.jpg HTTP/1.0\r\n\r\n'));
    socket.setTimeout(2000, () => socket.destroy(new Error('Server did not close HTTP response')));
    socket.on('data', (chunk) => chunks.push(chunk));
    socket.on('end', () => resolve(Buffer.concat(chunks)));
    socket.on('error', reject);
  });
  const split = response.indexOf('\r\n\r\n');
  const headers = response.subarray(0, split).toString('ascii');
  assert.match(headers, /^HTTP\/1\.[01] 200 OK/);
  assert.match(headers, /Content-Type: image\/jpeg/i);
  assert.match(headers, new RegExp(`Content-Length: ${frame().length}`, 'i'));
  assert.match(headers, /Connection: close/i);
  assert.doesNotMatch(headers, /Transfer-Encoding:/i);
  assert.deepEqual(response.subarray(split + 4), frame());
});

test('concurrent game and preview requests retain accurate separate counts', async (t) => {
  const { port } = await fixture(t);
  const replies = await Promise.all(Array.from({ length: 40 }, (_, index) => request(port, index % 2 ? '/preview.jpg?nonce=1' : '/img.jpg')));
  assert.ok(replies.every((reply) => reply.status === 200 && reply.body.equals(frame())));
  const health = JSON.parse((await request(port, '/health')).body);
  assert.equal(health.app, 'camera-bridge');
  assert.equal(health.ok, true);
  assert.equal(health.gameImageRequests, 20);
  assert.equal(health.previewImageRequests, 20);
  assert.equal(health.totalRequests, 41);
  assert.equal(health.frameAgeMs, 0);
});

test('expired frame returns 503 and health reports stale rather than serving old photo', async (t) => {
  const { port, expire } = await fixture(t);
  expire();
  const reply = await request(port, '/img.jpg');
  assert.equal(reply.status, 503);
  const health = JSON.parse((await request(port, '/health')).body);
  assert.equal(health.ok, false);
  assert.equal(health.status, 'stale');
  assert.equal(health.imageFailures, 1);
  assert.equal(health.gameImageRequests, 1);
});

test('stop endpoint blocks foreign Origin, invalid Host, cross-site fetch and GET', async (t) => {
  const app = await fixture(t);
  assert.equal((await request(app.port, '/api/stop', { method: 'POST', headers: { Origin: 'https://example.com' } })).status, 403);
  assert.equal((await request(app.port, '/api/stop', { method: 'POST', headers: { Host: 'example.com' } })).status, 403);
  assert.equal((await request(app.port, '/api/stop', { method: 'POST', headers: { 'Sec-Fetch-Site': 'cross-site' } })).status, 403);
  assert.equal((await request(app.port, '/api/stop')).status, 404);
  assert.equal(app.stopped(), 0);
  assert.equal((await request(app.port, '/api/stop', { method: 'POST', headers: { Origin: `http://127.0.0.1:${app.port}` } })).status, 200);
  await new Promise(setImmediate);
  assert.equal(app.stopped(), 1);
});
