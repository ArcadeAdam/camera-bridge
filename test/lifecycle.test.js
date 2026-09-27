'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const net = require('node:net');
const { spawn } = require('node:child_process');
const { inspectJpeg } = require('../lib/jpeg-stream');

const { ffmpegPath, ffmpegSkip, pythonPath, pythonSkip } = require('./runtime-paths');
async function reservePort() {
  const server = net.createServer();
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  return { port: server.address().port, close: () => new Promise((resolve) => server.close(resolve)) };
}
function request(port, pathname, method = 'GET') {
  return new Promise((resolve, reject) => {
    const req = http.request({ host: '127.0.0.1', port, path: pathname, method }, (res) => {
      const chunks = [];
      res.on('data', (chunk) => chunks.push(chunk));
      res.on('end', () => resolve({ status: res.statusCode, body: Buffer.concat(chunks) }));
    });
    req.on('error', reject);
    req.end();
  });
}
function alive(pid) {
  try { process.kill(pid, 0); return true; } catch (error) { if (error.code === 'ESRCH') return false; throw error; }
}
function launch(t, config) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'camera-proxy-test-'));
  const configPath = path.join(directory, 'config.json');
  fs.writeFileSync(configPath, JSON.stringify({ ffmpegPath, cameraName: 'unused', fps: 10, jpegQuality: 4, mirror: false, ...config }));
  const child = spawn(process.execPath, [path.join(__dirname, '..', 'server.js'), '--config', configPath, '--test-pattern'],
    { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '';
  child.stdout.on('data', (chunk) => { output += chunk; });
  child.stderr.on('data', (chunk) => { output += chunk; });
  const exited = new Promise((resolve) => child.once('exit', (code) => resolve(code)));
  const timeout = setTimeout(() => child.kill(), 7000);
  t.after(async () => {
    clearTimeout(timeout);
    if (child.exitCode === null) {
      try { await request(config.port, '/api/stop', 'POST'); await exited; } catch { child.kill(); }
    }
    fs.unlinkSync(configPath);
    fs.rmdirSync(directory);
  });
  return { child, exited, output: () => output };
}

for (const native of [false, true]) test(`daemon ${native ? 'native EOF' : 'TCP'} proxy shares image counters and stop removes both listeners and capture process`,
  { skip: ffmpegSkip || (native && pythonSkip), timeout: 10000 }, async (t) => {
    const main = await reservePort();
    const proxy = await reservePort();
    await main.close();
    await proxy.close();
    const app = launch(t, { port: main.port, selfAddress: '127.0.0.1', selfPort: proxy.port,
      eofCompat: native, pythonPath });
    let health;
    for (let attempt = 0; attempt < 100; attempt++) {
      await new Promise((resolve) => setTimeout(resolve, 25));
      try { health = JSON.parse((await request(main.port, '/health')).body); } catch {}
      if (health?.ok) break;
    }
    assert.equal(health?.ok, true, app.output());
    assert.equal(health.cameraAddress, `127.0.0.1:${proxy.port}`);
    assert.equal(health.eofCompat, native);
    const image = await request(proxy.port, '/img.jpg');
    assert.equal(image.status, 200);
    assert.deepEqual(inspectJpeg(image.body), { width: 320, height: 240 });
    health = JSON.parse((await request(main.port, '/health')).body);
    assert.equal(health.gameImageRequests, 1);
    const start = app.output().split('\n').find((line) => line.includes('capture-start '));
    const ffmpegPid = JSON.parse(start.slice(start.indexOf('capture-start ') + 14)).pid;
    assert.equal(alive(ffmpegPid), true);
    assert.equal((await request(main.port, '/api/stop', 'POST')).status, 200);
    assert.equal(await app.exited, 0, app.output());
    assert.equal(alive(app.child.pid), false);
    assert.equal(alive(ffmpegPid), false);
    if (native) {
      const helper = app.output().split('\n').find((line) => line.includes('eof-proxy-start '));
      const helperPid = JSON.parse(helper.slice(helper.indexOf('eof-proxy-start ') + 16)).pid;
      assert.equal(alive(helperPid), false);
    }
    await assert.rejects(request(main.port, '/health'), { code: 'ECONNREFUSED' });
    await assert.rejects(request(proxy.port, '/health'), { code: 'ECONNREFUSED' });
  });

for (const native of [false, true]) test(`${native ? 'native EOF' : 'TCP'} proxy bind failure exits clearly without starting capture or leaving main listener`,
  { skip: ffmpegSkip || (native && pythonSkip), timeout: 10000 }, async (t) => {
    const main = await reservePort();
    const occupied = await reservePort();
    t.after(occupied.close);
    await main.close();
    const app = launch(t, { port: main.port, selfAddress: '127.0.0.1', selfPort: occupied.port,
      eofCompat: native, pythonPath });
    assert.equal(await app.exited, 1, app.output());
    assert.match(app.output(), /self-proxy-error/);
    assert.match(app.output(), /EADDRINUSE/);
    assert.doesNotMatch(app.output(), /capture-start/);
    assert.equal(alive(app.child.pid), false);
    await assert.rejects(request(main.port, '/health'), { code: 'ECONNREFUSED' });
  });
