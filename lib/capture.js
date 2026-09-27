'use strict';
const { spawn } = require('node:child_process');
const { JpegStream } = require('./jpeg-stream');

class Capture {
  constructor(config, store, log, testPattern = false) {
    this.config = config;
    this.store = store;
    this.log = log;
    this.testPattern = testPattern;
    this.child = null;
    this.timer = null;
    this.watchdog = null;
    this.stopping = false;
    this.status = 'starting';
    this.lastError = null;
    this.reconnects = 0;
    this.attempts = 0;
  }
  start() {
    if (this.stopping) return;
    this.status = this.attempts++ ? 'reconnecting' : 'starting';
    if (this.attempts > 1) this.reconnects++;
    const c = this.config;
    const input = this.testPattern
      ? ['-re', '-f', 'lavfi', '-i', `testsrc=size=320x240:rate=${c.fps}`]
      : ['-f', 'dshow', '-video_size', '320x240', '-framerate', String(c.fps), '-i', `video=${c.cameraName}`];
    const filters = `scale=320:240:flags=fast_bilinear,format=yuvj420p${c.mirror ? ',hflip' : ''}`;
    const args = ['-hide_banner', '-loglevel', 'warning', '-nostdin', ...input,
      '-an', '-vf', filters, '-r', String(c.fps), '-c:v', 'mjpeg',
      '-q:v', String(c.jpegQuality), '-f', 'image2pipe', 'pipe:1'];
    let stderr = '';
    let receivedAt = Date.now();
    let announced = false;
    const parser = new JpegStream((frame) => {
      if (!this.store.publish(frame)) return;
      receivedAt = Date.now();
      this.status = 'live';
      this.lastError = null;
      if (!announced) { this.log('capture-ready', { width: 320, height: 240 }); announced = true; }
    }, () => this.store.invalidFrames++);
    const child = spawn(c.ffmpegPath, args, { windowsHide: true, shell: false, stdio: ['ignore', 'pipe', 'pipe'] });
    this.child = child;
    this.log('capture-start', { source: this.testPattern ? 'test-pattern' : 'camera', attempt: this.attempts, pid: child.pid });
    child.stdout.on('data', (chunk) => parser.push(chunk));
    child.stderr.on('data', (chunk) => { stderr = (stderr + chunk.toString('utf8')).slice(-4000); });
    child.on('error', (error) => { this.lastError = `Unable to start capture: ${error.message}`; });
    this.watchdog = setInterval(() => {
      if (Date.now() - receivedAt > 15000 && !this.stopping) {
        this.lastError = 'Camera produced no valid frame for 15 seconds';
        this.log('capture-timeout', {});
        child.kill();
      }
    }, 5000);
    child.on('close', (code) => {
      clearInterval(this.watchdog);
      this.watchdog = null;
      this.child = null;
      if (this.stopping) return;
      this.status = 'reconnecting';
      if (!this.lastError) {
        const detail = stderr.replace(/[\r\n\t]+/g, ' ').replace(/[\x00-\x1f\x7f]/g, '').slice(-500).trim();
        this.lastError = `Capture stopped (${code ?? 'signal'})${detail ? ': ' + detail : ''}`;
      }
      this.log('capture-retry', { code, error: this.lastError });
      this.timer = setTimeout(() => this.start(), 2000);
    });
  }
  stop() {
    this.stopping = true;
    this.status = 'stopping';
    clearTimeout(this.timer);
    clearInterval(this.watchdog);
    if (!this.child) return Promise.resolve();
    const child = this.child;
    return new Promise((resolve) => {
      const timeout = setTimeout(() => { child.kill('SIGKILL'); resolve(); }, 2000);
      child.once('close', () => { clearTimeout(timeout); resolve(); });
      child.kill();
    });
  }
}
module.exports = { Capture };
