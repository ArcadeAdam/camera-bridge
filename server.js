#!/usr/bin/env node
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { FrameStore } = require('./lib/frame-store');
const { Capture } = require('./lib/capture');
const { createBridgeServer } = require('./lib/http-server');
const { createSelfProxy, validateSelfProxy } = require('./lib/self-proxy');
const { createNativeEofProxy } = require('./lib/native-eof-proxy');

function resolveExecutable({ configured, configKey, envVar, names, configDirectory = __dirname, env = process.env }) {
  const explicit = configured !== undefined ? configured : env[envVar];
  const searchPath = Object.entries(env).find(([key]) => key.toUpperCase() === 'PATH')?.[1] || '';
  const directories = searchPath.split(path.delimiter).filter(Boolean).map((directory) => directory.replace(/^"(.*)"$/, '$1'));
  const exists = (candidate) => {
    try { return fs.statSync(candidate).isFile(); } catch { return false; }
  };
  const variants = (name) => process.platform === 'win32' && !path.extname(name) ? [name, name + '.exe'] : [name];
  const find = (name, checkConfigDirectory) => {
    const direct = path.isAbsolute(name) || /[\\/]/.test(name);
    const candidates = direct ? variants(path.resolve(configDirectory, name)) : [
      ...(checkConfigDirectory ? variants(path.resolve(configDirectory, name)) : []),
      ...directories.flatMap((directory) => variants(path.resolve(directory, name)))
    ];
    return candidates.find(exists);
  };
  if (explicit !== undefined) {
    if (typeof explicit !== 'string' || !explicit.trim() || /[\x00-\x1f]/.test(explicit)) throw new Error(`${configKey} / ${envVar} must name an executable`);
    const resolved = find(explicit, configured !== undefined);
    if (resolved) return resolved;
    throw new Error(`${configKey} / ${envVar} executable was not found: ${explicit}`);
  }
  for (const name of names) {
    const resolved = find(name, false);
    if (resolved) return resolved;
  }
  throw new Error(`No ${names[0]} executable found; set ${configKey}, set ${envVar}, or add it to PATH`);
}

function resolveRuntimePaths(config, configDirectory = __dirname, env = process.env, includePython = config.eofCompat === true) {
  const ffmpegPath = resolveExecutable({ configured: config.ffmpegPath, configKey: 'ffmpegPath',
    envVar: 'CAMERA_FFMPEG_PATH', names: ['ffmpeg'], configDirectory, env });
  const pythonPath = includePython ? resolveExecutable({ configured: config.pythonPath, configKey: 'pythonPath',
    envVar: 'CAMERA_PYTHON_PATH', names: ['python', 'python3'], configDirectory, env }) : config.pythonPath;
  return { ffmpegPath, pythonPath };
}

function loadConfig(args = process.argv.slice(2)) {
  let configPath = path.join(__dirname, 'bridge.config.json');
  let portOverride;
  let testPattern = false;
  for (let at = 0; at < args.length; at++) {
    const arg = args[at];
    if (arg === '--test-pattern') testPattern = true;
    else if (arg === '--config' && args[at + 1]) configPath = path.resolve(args[++at]);
    else if (arg === '--port' && args[at + 1]) portOverride = Number(args[++at]);
    else throw new Error(`Unknown or incomplete argument: ${arg}`);
  }
  const defaults = { cameraName: 'USB Video Device', port: 80, fps: 10, jpegQuality: 4, mirror: false };
  const config = { ...defaults, ...JSON.parse(fs.readFileSync(configPath, 'utf8').replace(/^\uFEFF/, '')) };
  if (portOverride !== undefined) config.port = portOverride;
  for (const [key, minimum, maximum] of [['port', 1, 65535], ['fps', 1, 30], ['jpegQuality', 2, 31]]) {
    if (!Number.isInteger(config[key]) || config[key] < minimum || config[key] > maximum) throw new Error(`${key} must be an integer from ${minimum} to ${maximum}`);
  }
  if (typeof config.cameraName !== 'string' || !config.cameraName.trim() || /[\x00-\x1f]/.test(config.cameraName)) throw new Error('cameraName must be an exact capture-device name');
  if (typeof config.mirror !== 'boolean') throw new Error('mirror must be true or false');
  validateSelfProxy(config);
  if (config.eofCompat !== undefined && typeof config.eofCompat !== 'boolean') throw new Error('eofCompat must be true or false');
  if (config.eofCompat) {
    if (!config.selfAddress) throw new Error('eofCompat requires selfAddress and selfPort');
  }
  Object.assign(config, resolveRuntimePaths(config, path.dirname(configPath)));
  return { config, testPattern };
}

function main() {
  let loaded;
  try { loaded = loadConfig(); } catch (error) { console.error(`Camera Bridge: ${error.message}`); process.exitCode = 1; return; }
  const { config, testPattern } = loaded;
  fs.mkdirSync(path.join(__dirname, 'logs'), { recursive: true });
  const logPath = path.join(__dirname, 'logs', 'bridge.log');
  const log = (event, details) => {
    const line = `${new Date().toISOString()} ${event} ${JSON.stringify(details)}\n`;
    try { fs.appendFileSync(logPath, line, 'utf8'); } catch (error) { console.error(`Could not write bridge log: ${error.message}`); }
    process.stdout.write(line);
  };
  const store = new FrameStore();
  const capture = new Capture(config, store, log, testPattern);
  let shuttingDown = false;
  let server;
  let proxy;
  let cameraAddress = null;
  const shutdown = async () => {
    if (shuttingDown) return;
    shuttingDown = true;
    log('shutdown', {});
    const closed = new Promise((resolve) => server.close(resolve));
    server.closeIdleConnections?.();
    const force = setTimeout(() => server.closeAllConnections?.(), 1500);
    await Promise.all([capture.stop(), closed, proxy?.stop()]);
    clearTimeout(force);
  };
  server = createBridgeServer({ store, capture, config, indexPath: path.join(__dirname, 'index.html'), log, onStop: shutdown, cameraAddress: () => cameraAddress });
  server.on('error', (error) => {
    log('http-error', { code: error.code, message: error.message });
    if (error.code === 'EADDRINUSE') console.error(`Preview port ${config.port} is busy. Choose another port in bridge.config.json. With a selfAddress listener, keep the emulator pointed to selfAddress:selfPort.`);
    process.exitCode = 1;
    shutdown();
  });
  server.listen(config.port, '127.0.0.1', () => {
    log('listening', { address: '127.0.0.1', port: config.port, source: testPattern ? 'test-pattern' : 'camera' });
    if (shuttingDown) return;
    if (config.selfAddress) {
      const createProxy = config.eofCompat ? createNativeEofProxy : createSelfProxy;
      proxy = createProxy({ selfAddress: config.selfAddress, selfPort: config.selfPort, targetPort: config.port, pythonPath: config.pythonPath, log });
      proxy.server.on('error', (error) => {
        log('self-proxy-error', { address: config.selfAddress, port: config.selfPort, code: error.code, message: error.message });
        console.error(`Cannot listen for the game at ${config.selfAddress}:${config.selfPort}: ${error.message}`);
        process.exitCode = 1;
        shutdown();
      });
      proxy.listen(() => {
        if (shuttingDown) return;
        cameraAddress = `${config.selfAddress}:${config.selfPort}`;
        log('self-proxy-listening', { address: config.selfAddress, port: config.selfPort, allowedSource: config.selfAddress });
        capture.start();
      });
    } else capture.start();
  });
  process.once('SIGINT', shutdown);
  process.once('SIGTERM', shutdown);
}

if (require.main === module) main();
module.exports = { loadConfig, resolveExecutable, resolveRuntimePaths };
