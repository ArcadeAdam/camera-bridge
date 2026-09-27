'use strict';
const net = require('node:net');
const os = require('node:os');

function validateSelfProxy(config, interfaces = os.networkInterfaces()) {
  const hasAddress = config.selfAddress !== undefined;
  const hasPort = config.selfPort !== undefined;
  if (!hasAddress && !hasPort) return false;
  if (!hasAddress || !hasPort) throw new Error('selfAddress and selfPort must be configured together');
  if (typeof config.selfAddress !== 'string' || net.isIP(config.selfAddress) !== 4) throw new Error('selfAddress must be a local IPv4 address');
  if (!Number.isInteger(config.selfPort) || config.selfPort < 1 || config.selfPort > 65535) throw new Error('selfPort must be an integer from 1 to 65535');
  const assigned = Object.values(interfaces).flat().some((entry) => entry &&
    (entry.family === 'IPv4' || entry.family === 4) && entry.address === config.selfAddress);
  if (!assigned) throw new Error(`selfAddress ${config.selfAddress} is not assigned to this computer`);
  return true;
}

function createSelfProxy({ selfAddress, selfPort, targetPort, log = () => {}, idleTimeoutMs = 10000 }) {
  const sockets = new Set();
  let rejected = 0;
  let accepted = 0;
  let completed = 0;
  const track = (socket) => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
    return socket;
  };
  const server = net.createServer({ pauseOnConnect: true }, (client) => {
    // Reject other machines before opening an upstream connection or reading data.
    if (client.remoteAddress !== selfAddress) {
      if (++rejected <= 5) log('self-proxy-rejected', { remoteAddress: client.remoteAddress });
      client.destroy();
      return;
    }
    const connection = ++accepted;
    const startedAt = Date.now();
    let hadError = false;
    let timedOut = false;
    let closedSockets = 0;
    track(client);
    const upstream = track(net.connect({ host: '127.0.0.1', port: targetPort }));
    const destroy = () => { client.destroy(); upstream.destroy(); };
    const timeout = () => { timedOut = true; destroy(); };
    const failed = () => { hadError = true; destroy(); };
    const traceClosed = (socketHadError) => {
      hadError ||= socketHadError;
      if (++closedSockets !== 2) return;
      if (++completed <= 10) log('self-proxy-complete', {
        connection, completed, bytesUpstream: upstream.bytesWritten, bytesDownstream: client.bytesWritten,
        upstreamBytesRead: upstream.bytesRead, clientBytesRead: client.bytesRead,
        hadError, timedOut, readableEnded: { client: client.readableEnded, upstream: upstream.readableEnded },
        durationMs: Date.now() - startedAt
      });
    };
    client.setTimeout(idleTimeoutMs, timeout);
    upstream.setTimeout(idleTimeoutMs, timeout);
    client.on('error', failed);
    upstream.on('error', failed);
    client.once('close', traceClosed);
    upstream.once('close', traceClosed);
    client.once('close', () => upstream.destroy());
    upstream.once('close', () => { if (!upstream.readableEnded) client.destroy(); });
    upstream.once('connect', () => { client.pipe(upstream); upstream.pipe(client); });
  });
  return {
    server,
    listen(callback) { server.listen(selfPort, selfAddress, callback); },
    address() { return server.address(); },
    stop() {
      return new Promise((resolve) => {
        server.close(() => resolve());
        for (const socket of sockets) socket.destroy();
      });
    }
  };
}

module.exports = { createSelfProxy, validateSelfProxy };
