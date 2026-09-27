'use strict';

// The game has a 512 KiB response buffer. Leave headroom for HTTP headers.
const MAX_JPEG_BYTES = 500 * 1024;

// Parse JPEG markers, including length-delimited metadata and stuffed scan bytes.
// A simple SOI/EOI search is insufficient: metadata may contain either sequence.
class JpegStream {
  constructor(onFrame, onInvalid = () => {}, maxFrameBytes = MAX_JPEG_BYTES) {
    this.onFrame = onFrame;
    this.onInvalid = onInvalid;
    this.maxFrameBytes = maxFrameBytes;
    this.buffer = Buffer.alloc(0);
    this.reset();
  }

  reset() { this.cursor = 0; this.started = false; this.scan = false; }

  discard(reason) {
    this.onInvalid(reason);
    this.buffer = this.buffer.subarray(2);
    this.reset();
  }

  push(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (true) {
      if (!this.started) {
        const start = this.buffer.indexOf(Buffer.from([0xff, 0xd8]));
        if (start < 0) {
          this.buffer = this.buffer.length && this.buffer[this.buffer.length - 1] === 0xff
            ? Buffer.from([0xff]) : Buffer.alloc(0);
          return;
        }
        this.buffer = this.buffer.subarray(start);
        this.started = true;
        this.cursor = 2;
      }
      if (this.cursor > this.maxFrameBytes) { this.discard('oversize'); continue; }
      if (this.scan) {
        const at = this.buffer.indexOf(0xff, this.cursor);
        if (at < 0) { this.cursor = this.buffer.length; break; }
        if (at + 1 >= this.buffer.length) { this.cursor = at; break; }
        const marker = this.buffer[at + 1];
        if (marker === 0x00 || (marker >= 0xd0 && marker <= 0xd7)) {
          this.cursor = at + 2;
          continue;
        }
        if (marker === 0xff) { this.cursor = at + 1; continue; }
        this.cursor = at;
        this.scan = false;
      }
      if (this.cursor + 2 > this.buffer.length) break;
      if (this.buffer[this.cursor] !== 0xff) { this.discard('invalid-marker'); continue; }
      let at = this.cursor + 1;
      while (at < this.buffer.length && this.buffer[at] === 0xff) at++;
      if (at >= this.buffer.length) break;
      const marker = this.buffer[at];
      if (marker === 0xd9) {
        const end = at + 1;
        if (end > this.maxFrameBytes) this.onInvalid('oversize');
        else this.onFrame(this.buffer.subarray(0, end));
        this.buffer = this.buffer.subarray(end);
        this.reset();
        continue;
      }
      if (marker === 0xd8 || marker === 0x00 || (marker >= 0xd0 && marker <= 0xd7)) {
        this.discard('unexpected-marker'); continue;
      }
      if (marker === 0x01) { this.cursor = at + 1; continue; }
      if (at + 3 > this.buffer.length) break;
      const length = this.buffer.readUInt16BE(at + 1);
      if (length < 2) { this.discard('invalid-segment-length'); continue; }
      const end = at + 1 + length;
      if (end > this.maxFrameBytes) { this.discard('oversize'); continue; }
      if (end > this.buffer.length) break;
      this.cursor = end;
      if (marker === 0xda) this.scan = true;
    }
    if (this.buffer.length > this.maxFrameBytes) {
      this.discard('oversize');
      this.push(Buffer.alloc(0));
    }
  }
}

function inspectJpeg(buffer) {
  if (buffer.length < 4 || buffer.readUInt16BE(0) !== 0xffd8 ||
      buffer.readUInt16BE(buffer.length - 2) !== 0xffd9) throw new Error('Incomplete JPEG');
  let at = 2;
  let dimensions;
  while (at < buffer.length - 2) {
    if (buffer[at++] !== 0xff) throw new Error('Invalid JPEG marker');
    while (buffer[at] === 0xff) at++;
    const marker = buffer[at++];
    if (marker === 0x01) continue;
    if (at + 2 > buffer.length) throw new Error('Truncated JPEG segment');
    const length = buffer.readUInt16BE(at);
    if (length < 2 || at + length > buffer.length) throw new Error('Invalid JPEG segment');
    const isSof = marker >= 0xc0 && marker <= 0xcf && ![0xc4, 0xc8, 0xcc].includes(marker);
    if (isSof) {
      if (marker !== 0xc0) throw new Error('JPEG must use baseline encoding');
      if (length < 8 || buffer[at + 2] !== 8) throw new Error('Invalid baseline JPEG');
      dimensions = { width: buffer.readUInt16BE(at + 5), height: buffer.readUInt16BE(at + 3) };
    }
    if (marker === 0xda) {
      if (!dimensions) throw new Error('JPEG has no baseline dimensions');
      return dimensions;
    }
    at += length;
  }
  throw new Error('JPEG has no image scan');
}

module.exports = { JpegStream, inspectJpeg, MAX_JPEG_BYTES };
