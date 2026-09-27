'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { JpegStream, inspectJpeg, MAX_JPEG_BYTES } = require('../lib/jpeg-stream');
const { FrameStore } = require('../lib/frame-store');

// Marker fixture tests framing, not visual decoding. APP metadata deliberately
// contains SOI and EOI byte pairs; scan data includes stuffed FF and a restart.
function jpeg(width = 320, height = 240, sof = 0xc0) {
  const head = Buffer.from([
    0xff, 0xd8,
    0xff, 0xe0, 0, 8, 0xff, 0xd9, 0xff, 0xd8, 0x41, 0x42,
    0xff, sof, 0, 17, 8, height >> 8, height & 255, width >> 8, width & 255,
    3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
    0xff, 0xda, 0, 12, 3, 1, 0, 2, 0x11, 3, 0x11, 0, 63, 0,
    0x12, 0xff, 0x00, 0x34, 0xff, 0xd0, 0x56, 0xff, 0xd9
  ]);
  return head;
}

test('JPEG stream recovers complete frames at every possible split', () => {
  const first = jpeg();
  const second = jpeg(320, 240);
  const input = Buffer.concat([Buffer.from([0, 1, 0xff, 2]), first, second]);
  for (let split = 0; split <= input.length; split++) {
    const frames = [];
    const parser = new JpegStream((frame) => frames.push(Buffer.from(frame)));
    parser.push(input.subarray(0, split));
    parser.push(input.subarray(split));
    assert.deepEqual(frames, [first, second], `split ${split}`);
  }
});

test('JPEG stream handles single byte chunks without mistaking metadata for boundaries', () => {
  const original = jpeg();
  const frames = [];
  const parser = new JpegStream((frame) => frames.push(Buffer.from(frame)));
  for (const byte of original) parser.push(Buffer.from([byte]));
  assert.deepEqual(frames, [original]);
  assert.deepEqual(inspectJpeg(frames[0]), { width: 320, height: 240 });
});

test('JPEG stream recovers after malformed segment and bounds incomplete frames', () => {
  const frames = [];
  const invalid = [];
  const parser = new JpegStream((frame) => frames.push(Buffer.from(frame)), (reason) => invalid.push(reason), 256);
  parser.push(Buffer.from([0xff, 0xd8, 0xff, 0xe0, 0, 1]));
  parser.push(jpeg());
  assert.equal(invalid.length, 1);
  assert.deepEqual(frames, [jpeg()]);
  const oversized = Buffer.concat([jpeg().subarray(0, -2), Buffer.alloc(300, 1)]);
  parser.push(oversized);
  assert.ok(parser.buffer.length <= 256);
  parser.push(jpeg());
  assert.equal(frames.length, 2);
  assert.ok(invalid.includes('oversize'));
});

test('frame store rejects wrong dimensions, progressive JPEG and stale frames', () => {
  let time = 1000;
  const store = new FrameStore({ maxAgeMs: 3000, now: () => time });
  assert.equal(store.get(), null);
  assert.equal(store.age(), null);
  assert.equal(store.publish(jpeg(640, 480)), false);
  assert.equal(store.publish(jpeg(320, 240, 0xc2)), false);
  assert.equal(store.publish(Buffer.from('not a jpeg')), false);
  assert.equal(store.publish(jpeg()), true);
  assert.equal(store.capturedFrames, 1);
  assert.equal(store.invalidFrames, 3);
  time = 4000;
  assert.ok(store.get());
  time = 4001;
  assert.equal(store.get(), null);
  assert.equal(store.age(), 3001);
  assert.equal(store.publish(jpeg()), true);
  assert.equal(store.age(), 0);
  assert.ok(store.get());
});

test('frame size ceiling leaves header space below the game 512 KiB response limit', () => {
  const store = new FrameStore();
  const original = jpeg();
  const oversized = Buffer.concat([original.subarray(0, -2), Buffer.alloc(MAX_JPEG_BYTES, 0), original.subarray(-2)]);
  assert.equal(store.publish(oversized), false);
  assert.equal(store.get(), null);
  assert.ok(MAX_JPEG_BYTES + 4096 < 0x80000);
});

module.exports = { jpeg };
