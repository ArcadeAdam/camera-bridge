'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { resolveExecutable } = require('../server');

// The installed cabinet config is optional for tests. A source checkout can use
// CAMERA_TEST_CONFIG, CAMERA_FFMPEG_PATH / CAMERA_PYTHON_PATH, or PATH instead.
const configPath = process.env.CAMERA_TEST_CONFIG
  ? path.resolve(process.env.CAMERA_TEST_CONFIG) : path.join(__dirname, '..', 'bridge.config.json');
let config = {};
let configError;
try {
  if (fs.existsSync(configPath)) config = JSON.parse(fs.readFileSync(configPath, 'utf8').replace(/^\uFEFF/, ''));
  else if (process.env.CAMERA_TEST_CONFIG) configError = `Test config was not found: ${configPath}`;
} catch (error) { configError = `Cannot read test runtime config: ${error.message}`; }

function runtime(configKey, envVar, names) {
  if (configError) return { skip: configError };
  try {
    return { executable: resolveExecutable({ configured: config[configKey], configKey, envVar, names, configDirectory: path.dirname(configPath) }), skip: false };
  } catch (error) { return { skip: `Runtime integration test skipped: ${error.message}` }; }
}

const ffmpeg = runtime('ffmpegPath', 'CAMERA_FFMPEG_PATH', ['ffmpeg']);
const python = runtime('pythonPath', 'CAMERA_PYTHON_PATH', ['python', 'python3']);
module.exports = { ffmpegPath: ffmpeg.executable, ffmpegSkip: ffmpeg.skip,
  pythonPath: python.executable, pythonSkip: python.skip };
