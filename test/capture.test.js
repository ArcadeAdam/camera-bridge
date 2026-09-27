'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { Capture } = require('../lib/capture');
const { FrameStore } = require('../lib/frame-store');
const { inspectJpeg } = require('../lib/jpeg-stream');

const { ffmpegPath, ffmpegSkip } = require('./runtime-paths');
test('capture runtime produces baseline test-pattern JPEGs and shuts down cleanly',
  { skip: ffmpegSkip, timeout: 10000 }, async () => {
    const store = new FrameStore();
    const capture = new Capture({ ffmpegPath, cameraName: 'unused', fps: 10, jpegQuality: 4, mirror: false }, store, () => {}, true);
    try {
      capture.start();
      const deadline = Date.now() + 5000;
      while (store.capturedFrames < 3 && Date.now() < deadline) await new Promise((resolve) => setTimeout(resolve, 50));
      assert.ok(store.capturedFrames >= 3, capture.lastError || 'Capture did not produce three frames');
      assert.equal(store.invalidFrames, 0);
      assert.deepEqual(inspectJpeg(store.get()), { width: 320, height: 240 });
      assert.equal(capture.status, 'live');
    } finally { await capture.stop(); }
    assert.equal(capture.child, null);
    assert.equal(capture.status, 'stopping');
  });
