'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { loadConfig, resolveExecutable, resolveRuntimePaths } = require('../server');

function fixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'camera-runtime-test-'));
  const files = [];
  const directories = [];
  const makeExecutable = (subdirectory, name) => {
    const folder = path.join(directory, subdirectory);
    if (!fs.existsSync(folder)) { fs.mkdirSync(folder); directories.push(folder); }
    const target = path.join(folder, name + (process.platform === 'win32' ? '.exe' : ''));
    fs.writeFileSync(target, 'runtime discovery fixture; never executed');
    files.push(target);
    return target;
  };
  const writeConfig = (config) => {
    const target = path.join(directory, 'config.json');
    fs.writeFileSync(target, JSON.stringify(config));
    if (!files.includes(target)) files.push(target);
    return target;
  };
  t.after(() => {
    for (const file of files) fs.unlinkSync(file);
    for (const folder of directories) fs.rmdirSync(folder);
    fs.rmdirSync(directory);
  });
  return { directory, makeExecutable, writeConfig };
}

test('runtime discovery prefers explicit config, then environment, then PATH', (t) => {
  const f = fixture(t);
  const configured = f.makeExecutable('configured', 'ffmpeg');
  const fromEnvironment = f.makeExecutable('environment', 'ffmpeg');
  const fromPath = f.makeExecutable('path', 'ffmpeg');
  const base = { configKey: 'ffmpegPath', envVar: 'CAMERA_FFMPEG_PATH', names: ['ffmpeg'], configDirectory: f.directory };
  assert.equal(resolveExecutable({ ...base, configured, env: { CAMERA_FFMPEG_PATH: fromEnvironment, PATH: path.dirname(fromPath) } }), configured);
  assert.equal(resolveExecutable({ ...base, env: { CAMERA_FFMPEG_PATH: fromEnvironment, PATH: path.dirname(fromPath) } }), fromEnvironment);
  assert.equal(resolveExecutable({ ...base, env: { Path: `"${path.dirname(fromPath)}"` } }), fromPath);
});

test('relative configured runtime paths resolve beside the config, not the current directory', (t) => {
  const f = fixture(t);
  const ffmpeg = f.makeExecutable('runtime', 'ffmpeg');
  const configPath = f.writeConfig({ ffmpegPath: path.relative(f.directory, ffmpeg), cameraName: 'Portable Test Camera', fps: 30 });
  const loaded = loadConfig(['--config', configPath]);
  assert.equal(loaded.config.ffmpegPath, ffmpeg);
  assert.equal(loaded.config.cameraName, 'Portable Test Camera');
  assert.equal(loaded.config.fps, 30);
  assert.equal(loaded.testPattern, false);
});

test('missing explicit runtime is reported instead of silently using another executable', (t) => {
  const f = fixture(t);
  const fromPath = f.makeExecutable('path', 'ffmpeg');
  assert.throws(() => resolveExecutable({ configured: path.join(f.directory, 'missing.exe'), configKey: 'ffmpegPath',
    envVar: 'CAMERA_FFMPEG_PATH', names: ['ffmpeg'], env: { PATH: path.dirname(fromPath) } }), /executable was not found/);
  assert.throws(() => resolveExecutable({ configKey: 'ffmpegPath', envVar: 'CAMERA_FFMPEG_PATH', names: ['ffmpeg'], env: {} }), /set ffmpegPath, set CAMERA_FFMPEG_PATH/);
});

test('helper discovery is optional until EOF compatibility needs it', (t) => {
  const f = fixture(t);
  const ffmpegPath = f.makeExecutable('ffmpeg', 'ffmpeg');
  const pythonPath = f.makeExecutable('python', 'python3');
  assert.deepEqual(resolveRuntimePaths({ ffmpegPath }, f.directory, {}), { ffmpegPath, pythonPath: undefined });
  assert.equal(resolveRuntimePaths({ ffmpegPath, eofCompat: true }, f.directory,
    { CAMERA_PYTHON_PATH: pythonPath }).pythonPath, pythonPath);
  assert.equal(resolveRuntimePaths({ ffmpegPath, eofCompat: true }, f.directory,
    { PATH: path.dirname(pythonPath) }).pythonPath, pythonPath);
});

test('normal startup still requires its configuration file', (t) => {
  const f = fixture(t);
  assert.throws(() => loadConfig(['--config', path.join(f.directory, 'missing.json')]), { code: 'ENOENT' });
});

test('runtime integration tests clearly skip when configured executables are unavailable', (t) => {
  const f = fixture(t);
  const configPath = f.writeConfig({ ffmpegPath: 'missing/ffmpeg', pythonPath: 'missing/python' });
  const childEnv = { ...process.env, CAMERA_TEST_CONFIG: configPath };
  delete childEnv.NODE_TEST_CONTEXT;
  const result = spawnSync(process.execPath, ['--test', '--test-reporter=tap',
    path.join(__dirname, 'capture.test.js'), path.join(__dirname, 'lifecycle.test.js'), path.join(__dirname, 'native-eof-proxy.test.js')],
  { windowsHide: true, encoding: 'utf8', env: childEnv });
  assert.equal(result.status, 0, result.stderr || result.stdout);
  assert.equal((result.stdout.match(/# SKIP Runtime integration test skipped:/g) || []).length, 7, result.stdout);
  assert.match(result.stdout, /ffmpegPath.*executable was not found/);
  assert.match(result.stdout, /pythonPath.*executable was not found/);
});
