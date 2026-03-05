#!/usr/bin/env node

const { spawnSync } = require("node:child_process");

const platformPackageMap = {
  "linux-x64": { packageName: "@farsight-cda/ocw-linux-x64", binaryPath: "bin/ocw" },
  "darwin-arm64": { packageName: "@farsight-cda/ocw-darwin-arm64", binaryPath: "bin/ocw" },
  "win32-x64": { packageName: "@farsight-cda/ocw-win32-x64", binaryPath: "bin/ocw.exe" }
};

function getPackageForHost() {
  const key = `${process.platform}-${process.arch}`;
  return platformPackageMap[key] ?? null;
}

function resolveHostBinaryPath() {
  const match = getPackageForHost();
  if (!match) {
    const supported = Object.keys(platformPackageMap).join(", ");
    throw new Error(`Unsupported platform '${process.platform}-${process.arch}'. Supported: ${supported}.`);
  }

  try {
    return require.resolve(`${match.packageName}/${match.binaryPath}`);
  } catch {
    throw new Error(
      `Missing platform binary package '${match.packageName}'. Reinstall with 'npm i -g @farsight-cda/ocw'.`
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
