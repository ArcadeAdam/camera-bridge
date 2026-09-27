'use strict';
const { EventEmitter } = require('node:events');
const { spawn } = require('node:child_process');
const path = require('node:path');

function createNativeEofProxy({ selfAddress, selfPort, targetPort, pythonPath, log = () => {}, idleTimeoutMs = 10000 }) {
  const server = new EventEmitter();
  let child;
  let bound = null;
  let stopping = false;
  let failureReported = false;
  let exited;
  const fail = (error) => {
    if (stopping || failureReported) return;
    failureReported = true;
    server.emit('error', error);
  };
  return {
    server,
    address() { return bound; },
    listen(callback) {
      child = spawn(pythonPath, ['-u', path.join(__dirname, 'native-eof-proxy.py'),
        '--host', selfAddress, '--port', String(selfPort), '--target-port', String(targetPort), '--timeout', String(idleTimeoutMs)],
      { windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'pipe'] });
      log('eof-proxy-start', { pid: child.pid });
      exited = new Promise((resolve) => child.once('close', resolve));
      let pending = '';
      let stderr = '';
      child.stdout.on('data', (chunk) => {
        pending += chunk.toString('utf8');
        if (pending.length > 65536) { fail(new Error('EOF helper output exceeded limit')); return; }
        let newline;
        while ((newline = pending.indexOf('\n')) >= 0) {
          const line = pending.slice(0, newline);
          pending = pending.slice(newline + 1);
          let message;
          try { message = JSON.parse(line); } catch { fail(new Error('EOF helper returned invalid metadata')); continue; }
          if (message.event === 'ready') {
            bound = { address: message.address, port: message.port, family: 'IPv4' };
            if (!stopping) callback();
          } else if (message.event === 'error') {
            const error = new Error(message.message);
            error.code = message.code;
            fail(error);
          } else if (['self-proxy-complete', 'self-proxy-rejected'].includes(message.event)) {
            const { event, ...metadata } = message;
            log(event, metadata);
          }
        }
      });
      child.stderr.on('data', (chunk) => { stderr = (stderr + chunk.toString('utf8')).slice(-500); });
      child.stdin.on('error', (error) => { if (!stopping) fail(error); });
      child.on('error', fail);
      child.once('close', (code) => {
        bound = null;
        if (!stopping) fail(new Error(`EOF helper exited (${code}): ${stderr.trim()}`));
      });
    },
    async stop() {
      if (stopping) { await exited; return; }
      stopping = true;
      if (!child || child.exitCode !== null) return;
      child.stdin.end('STOP\n');
      const force = setTimeout(() => child.kill(), 2000);
      await exited;
      clearTimeout(force);
    }
  };
}

module.exports = { createNativeEofProxy };
