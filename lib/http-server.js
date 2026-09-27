'use strict';
const http = require('node:http');
const fs = require('node:fs');

function createBridgeServer({ store, capture, config, indexPath, log = () => {}, onStop = () => {}, cameraAddress = () => null }) {
  const startedAt = Date.now();
  const counts = { gameImageRequests: 0, gameResponsesCompleted: 0, previewImageRequests: 0, imageFailures: 0, unknownRequests: 0, totalRequests: 0 };
  let stopping = false;
  const send = (res, status, body, contentType = 'text/plain; charset=utf-8', extra = {}) => {
    const data = Buffer.isBuffer(body) ? body : Buffer.from(body);
    res.writeHead(status, { 'Content-Type': contentType, 'Content-Length': data.length,
      Connection: 'close', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff', ...extra });
    res.end(data);
  };
  const server = http.createServer((req, res) => {
    counts.totalRequests++;
    const pathname = (req.url || '/').split('?')[0];
    if (req.method === 'GET' && (pathname === '/img.jpg' || pathname === '/preview.jpg')) {
      const game = pathname === '/img.jpg';
      if (game) counts.gameImageRequests++; else counts.previewImageRequests++;
      const frame = store.get();
      if (game && counts.gameImageRequests <= 5) log('game-image-request', { number: counts.gameImageRequests, ready: !!frame });
      if (game) {
        const requestNumber = counts.gameImageRequests;
        res.once('finish', () => {
          const completed = ++counts.gameResponsesCompleted;
          if (completed <= 5) log('game-response-complete', { requestNumber, completed,
            statusCode: res.statusCode, imageBytes: frame?.length ?? 0 });
        });
        res.once('close', () => {
          if (!res.writableFinished && requestNumber <= 5) log('game-response-aborted', { requestNumber, imageBytes: frame?.length ?? 0 });
        });
      }
      if (!frame) { counts.imageFailures++; send(res, 503, 'Camera frame is not ready.\n', undefined, { 'Retry-After': '1' }); return; }
      send(res, 200, frame, 'image/jpeg');
      return;
    }
    if (req.method === 'GET' && pathname === '/health') {
      const frame = store.get();
      const status = stopping ? 'stopping' : frame ? 'live' : capture.status === 'live' ? 'stale' : capture.status;
      send(res, 200, JSON.stringify({ app: 'camera-bridge', ok: !!frame && !stopping, status,
        source: capture.testPattern ? 'test-pattern' : 'camera', cameraName: config.cameraName,
        port: server.address()?.port ?? config.port, width: 320, height: 240, captureFps: config.fps,
        cameraAddress: cameraAddress() || `127.0.0.1:${server.address()?.port ?? config.port}`,
        eofCompat: config.eofCompat === true,
        frameAgeMs: store.age(), capturedFrames: store.capturedFrames, invalidFrames: store.invalidFrames,
        ...counts, reconnects: capture.reconnects, uptimeMs: Date.now() - startedAt, lastError: capture.lastError }), 'application/json; charset=utf-8');
      return;
    }
    if (req.method === 'GET' && pathname === '/') {
      fs.readFile(indexPath, (error, data) => {
        if (error) send(res, 503, 'Camera Bridge is running. Open /health for status.\n');
        else send(res, 200, data, 'text/html; charset=utf-8');
      });
      return;
    }
    if (req.method === 'POST' && pathname === '/api/stop') {
      const port = server.address()?.port;
      const permittedHosts = new Set([`127.0.0.1:${port}`, `localhost:${port}`]);
      if (port === 80) { permittedHosts.add('127.0.0.1'); permittedHosts.add('localhost'); }
      let allowed = permittedHosts.has((req.headers.host || '').toLowerCase());
      if (req.headers.origin) {
        try {
          const origin = new URL(req.headers.origin);
          allowed = allowed && origin.protocol === 'http:' && permittedHosts.has(origin.host.toLowerCase());
        } catch { allowed = false; }
      }
      if (!allowed || (req.headers['sec-fetch-site'] && !['same-origin', 'none'].includes(req.headers['sec-fetch-site']))) {
        send(res, 403, 'Stop requires a local same-origin request.\n'); return;
      }
      if (!stopping) {
        stopping = true;
        log('stop-request', {});
        res.once('finish', () => setImmediate(onStop));
      }
      send(res, 200, JSON.stringify({ ok: true, status: 'stopping' }), 'application/json; charset=utf-8');
      return;
    }
    counts.unknownRequests++;
    if (counts.unknownRequests <= 10) log('unknown-request', { method: req.method, path: pathname.slice(0, 160).replace(/[\x00-\x1f\x7f]/g, '') });
    send(res, 404, 'Not found.\n');
  });
  server.requestTimeout = 5000;
  server.headersTimeout = 5000;
  server.keepAliveTimeout = 1;
  server.maxHeadersCount = 50;
  server.on('clientError', (_error, socket) => {
    if (socket.writable) socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n');
    else socket.destroy();
  });
  return server;
}
module.exports = { createBridgeServer };
