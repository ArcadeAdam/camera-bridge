'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const net = require('node:net');
const { createSelfProxy, validateSelfProxy } = require('../lib/self-proxy');

async function listen(server, host = '127.0.0.1') {
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, host, resolve); });
  return server.address().port;
}

function exchange(port, source, payload = 'GET /img.jpg HTTP/1.0\r\n\r\n') {
  return new Promise((resolve, reject) => {
    const chunks = [];
    const socket = net.connect({ host: '127.0.0.2', port, localAddress: source }, () => socket.write(payload));
    socket.setTimeout(2000, () => socket.destroy(new Error('Proxy exchange timed out')));
    socket.on('data', (chunk) => chunks.push(chunk));
    socket.on('error', (error) => { if (error.code !== 'ECONNRESET') reject(error); });
    socket.on('close', () => resolve(Buffer.concat(chunks)));
  });
}

test('self proxy validates exact locally assigned IPv4 and complete config', () => {
  const interfaces = { ethernet: [{ family: 'IPv4', address: '192.168.1.2' }] };
  assert.equal(validateSelfProxy({}, interfaces), false);
  assert.equal(validateSelfProxy({ selfAddress: '192.168.1.2', selfPort: 18080 }, interfaces), true);
  assert.throws(() => validateSelfProxy({ selfAddress: '192.168.1.3', selfPort: 18080 }, interfaces), /not assigned/);
  assert.throws(() => validateSelfProxy({ selfAddress: '0.0.0.0', selfPort: 18080 }, interfaces), /not assigned/);
  assert.throws(() => validateSelfProxy({ selfAddress: '192.168.1.2' }, interfaces), /together/);
  assert.throws(() => validateSelfProxy({ selfPort: 18080 }, interfaces), /together/);
  assert.throws(() => validateSelfProxy({ selfAddress: '192.168.1.2', selfPort: 0 }, interfaces), /integer/);
});

test('self proxy accepts identical source and preserves large responses, rejecting other peers before upstream connect', async (t) => {
  let connections = 0;
  let requests = 0;
  const body = Buffer.alloc(480 * 1024, 0xab);
  const response = Buffer.concat([Buffer.from(`HTTP/1.1 200 OK\r\nContent-Length: ${body.length}\r\nConnection: close\r\n\r\n`), body]);
  const upstream = net.createServer((socket) => {
    connections++;
    socket.once('data', () => { requests++; socket.end(response); });
  });
  const targetPort = await listen(upstream);
  const proxy = createSelfProxy({ selfAddress: '127.0.0.2', selfPort: 0, targetPort });
  await new Promise((resolve, reject) => { proxy.server.once('error', reject); proxy.listen(resolve); });
  t.after(async () => { await proxy.stop(); await new Promise((resolve) => upstream.close(resolve)); });
  const port = proxy.address().port;
  assert.deepEqual(await exchange(port, '127.0.0.2'), response);
  assert.equal(requests, 1);
  assert.equal(connections, 1);
  assert.equal((await exchange(port, '127.0.0.1')).length, 0);
  assert.equal(requests, 1);
  assert.equal(connections, 1, 'Rejected source must never establish upstream connection');
});

test('self proxy expires idle connections and closes active connections on stop', async (t) => {
  const upstreamSockets = new Set();
  const upstream = net.createServer((socket) => {
    upstreamSockets.add(socket);
    socket.once('close', () => upstreamSockets.delete(socket));
  });
  const targetPort = await listen(upstream);
  const proxy = createSelfProxy({ selfAddress: '127.0.0.2', selfPort: 0, targetPort, idleTimeoutMs: 80 });
  await new Promise((resolve, reject) => { proxy.server.once('error', reject); proxy.listen(resolve); });
  t.after(async () => {
    await proxy.stop();
    for (const socket of upstreamSockets) socket.destroy();
    await new Promise((resolve) => upstream.close(resolve));
  });
  const open = () => new Promise((resolve, reject) => {
    const socket = net.connect({ host: '127.0.0.2', port: proxy.address().port, localAddress: '127.0.0.2' }, () => resolve(socket));
    socket.on('error', reject);
  });
  const idle = await open();
  await new Promise((resolve) => idle.once('close', resolve));
  const active = await open();
  const closed = new Promise((resolve) => active.once('close', resolve));
  await proxy.stop();
  await closed;
  assert.equal(active.destroyed, true);
  assert.equal(proxy.address(), null);
});
