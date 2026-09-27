'use strict';
const { inspectJpeg, MAX_JPEG_BYTES } = require('./jpeg-stream');

class FrameStore {
  constructor({ maxAgeMs = 3000, now = Date.now } = {}) {
    this.maxAgeMs = maxAgeMs;
    this.now = now;
    this.frame = null;
    this.capturedAt = null;
    this.capturedFrames = 0;
    this.invalidFrames = 0;
  }
  publish(frame) {
    try {
      const { width, height } = inspectJpeg(frame);
      if (width !== 320 || height !== 240 || frame.length > MAX_JPEG_BYTES) {
        throw new Error('Expected a 320x240 JPEG under the game response limit');
      }
      this.frame = Buffer.from(frame);
      this.capturedAt = this.now();
      this.capturedFrames++;
      return true;
    } catch {
      this.invalidFrames++;
      return false;
    }
  }
  age() { return this.capturedAt === null ? null : Math.max(0, this.now() - this.capturedAt); }
  get() { return this.frame && this.age() <= this.maxAgeMs ? this.frame : null; }
}
module.exports = { FrameStore };
