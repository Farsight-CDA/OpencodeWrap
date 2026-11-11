#!/usr/bin/env node

const { spawnSync } = require("node:child_process");

const packageName = "@farsight-cda/ocw-win32-x64";

function resolveHostBinaryPath() {
  if (process.platform !== "win32" || process.arch !== "x64") {
    throw new Error(`Unsupported platform '${process.platform}-${process.arch}'. Only Windows x64 is supported.`);
  }

  try {
    return require.resolve(`${packageName}/bin/ocw.exe`);
  } catch {
    throw new Error(
      `Missing platform binary package '${packageName}'. Reinstall with 'npm i -g @farsight-cda/ocw'.`
    );
  }
}

function run() {
  const binaryPath = resolveHostBinaryPath();
  const result = spawnSync(binaryPath, process.argv.slice(2), {
    stdio: "inherit"
  });

  if (result.error) {
    throw result.error;
  }

  process.exit(result.status ?? 1);
}

try {
  run();
} catch (error) {
  const message = error instanceof Error ? error.message : String(error);
  console.error(`ocw launcher error: ${message}`);
  process.exit(1);
}
