'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const net = require('node:net');
const { spawnSync } = require('node:child_process');
const path = require('node:path');
const { createNativeEofProxy } = require('../lib/native-eof-proxy');
const { pythonPath, pythonSkip } = require('./runtime-paths');

test('validated request terminator skips grace delay while missing suffix still waits and queued data is checked',
  { skip: pythonSkip }, () => {
    const script = `
import runpy, sys
module = runpy.run_path(sys.argv[1])
padding = module['request_padding']
selector = module['select']
class Socket:
    def __init__(self, chunks): self.chunks = list(chunks)
    def recv(self, count):
        result = self.chunks.pop(0)
        assert len(result) <= count
        return result
def exercise(initial, chunks):
    sock = Socket(chunks)
    waits = []
    def ready(read, write, error, timeout):
        waits.append(timeout)
        return ([sock] if sock.chunks else [], [], [])
    selector.select = ready
    return padding(sock, initial), waits
count, waits = exercise(b'\\x00', [])
assert count == 1 and waits == [0], (count, waits)
count, waits = exercise(b'', [])
assert count == 0 and 0 < waits[0] <= 0.02, (count, waits)
count, waits = exercise(b'', [b'\\x00'])
assert count == 1 and waits[0] > 0 and waits[1] == 0, (count, waits)
for initial, chunks in [(b'\\x00', [b'x']), (b'\\x00' * 16, [b'\\x00'])]:
    try: exercise(initial, chunks)
    except ValueError: pass
    else: raise AssertionError('Queued invalid suffix was accepted')
print('padding timing and validation passed')
`;
    const result = spawnSync(pythonPath, ['-c', script, path.join(__dirname, '..', 'lib', 'native-eof-proxy.py')], { windowsHide: true, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /padding timing and validation passed/);
  });

async function listen(server) {
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  return server.address().port;
}
function exchange(port, source, request = 'GET /img.jpg HTTP/1.0\r\n\r\n', fragment = null) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    const socket = net.connect({ host: '127.0.0.2', port, localAddress: source }, () => {
      socket.write(request);
      if (fragment !== null) setTimeout(() => socket.write(fragment), 5);
    });
    socket.setTimeout(3000, () => socket.destroy(new Error('Native proxy exchange timed out')));
    socket.on('data', (chunk) => chunks.push(chunk));
    socket.on('error', (error) => { if (error.code !== 'ECONNRESET') reject(error); });
    socket.on('close', () => resolve(Buffer.concat(chunks)));
  });
}
function alive(pid) {
  try { process.kill(pid, 0); return true; } catch (error) { if (error.code === 'ESRCH') return false; throw error; }
}

test('native EOF proxy preserves exact normal payload, rejects foreign peers and POSTs, then stops helper',
  { skip: pythonSkip, timeout: 10000 }, async (t) => {
    let requests = 0;
    let connections = 0;
    const forwardedRequests = [];
    const body = Buffer.alloc(480 * 1024, 0xab);
    const response = Buffer.concat([Buffer.from(`HTTP/1.1 200 OK\r\nContent-Length: ${body.length}\r\nConnection: close\r\n\r\n`), body]);
    const upstream = net.createServer((socket) => {
      connections++;
      socket.once('data', (request) => { requests++; forwardedRequests.push(request); socket.end(response); });
    });
    const targetPort = await listen(upstream);
    const logs = [];
    const proxy = createNativeEofProxy({ selfAddress: '127.0.0.2', selfPort: 0, targetPort, pythonPath,
      log: (event, details) => logs.push({ event, ...details }) });
    await new Promise((resolve, reject) => { proxy.server.on('error', reject); proxy.listen(resolve); });
    t.after(async () => { await proxy.stop(); await new Promise((resolve) => upstream.close(resolve)); });
    const pid = logs.find((item) => item.event === 'eof-proxy-start').pid;
    const port = proxy.address().port;
    assert.equal(alive(pid), true);
    assert.deepEqual(await exchange(port, '127.0.0.2'), response, 'Urgent byte must never appear in normal response');
    assert.equal(requests, 1);
    const request = 'GET /img.jpg HTTP/1.0\r\n\r\n';
    assert.deepEqual(await exchange(port, '127.0.0.2', request + '\x00'), response);
    assert.deepEqual(await exchange(port, '127.0.0.2', request, '\x00'), response, 'Split C-string terminator is accepted');
    assert.deepEqual(await exchange(port, '127.0.0.2', request + '\x00'.repeat(16)), response);
    assert.equal(requests, 4);
    assert.ok(forwardedRequests.every((bytes) => bytes.equals(Buffer.from(request))), 'NUL padding must be stripped before upstream');
    assert.equal((await exchange(port, '127.0.0.1')).length, 0);
    assert.equal((await exchange(port, '127.0.0.2', 'POST /api/stop HTTP/1.0\r\n\r\n')).length, 0);
    assert.equal((await exchange(port, '127.0.0.2', request + 'x')).length, 0);
    assert.equal((await exchange(port, '127.0.0.2', request, 'x')).length, 0);
    assert.equal((await exchange(port, '127.0.0.2', request + request)).length, 0);
    assert.equal((await exchange(port, '127.0.0.2', request + '\x00'.repeat(17))).length, 0);
    assert.equal(connections, 4, 'Rejected peers, bodies, padding and methods must not connect upstream');
    await proxy.stop();
    assert.equal(alive(pid), false);
    assert.equal(proxy.address(), null);
    const complete = logs.find((item) => item.event === 'self-proxy-complete' && item.urgentByteSent);
    assert.ok(complete);
    assert.equal(complete.bytesDownstream, response.length);
    assert.equal(complete.imageBytes, body.length);
    assert.equal(complete.hadError, false);
    assert.ok(logs.some((item) => item.event === 'self-proxy-complete' && item.paddingLength === 1 && item.urgentByteSent));
    assert.ok(logs.some((item) => item.event === 'self-proxy-complete' && item.paddingLength === 16 && item.urgentByteSent));
  });
