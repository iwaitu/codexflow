/* eslint-disable @typescript-eslint/no-require-imports */
'use strict';

const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const MACHO_MAGICS = new Set([
  0xfeedface, // MH_MAGIC (32-bit BE)
  0xcefaedfe, // MH_CIGAM (32-bit LE)
  0xfeedfacf, // MH_MAGIC_64 (64-bit BE)
  0xcffaedfe, // MH_CIGAM_64 (64-bit LE)
  0xcafebabe, // FAT_MAGIC (universal BE)
  0xbebafeca, // FAT_CIGAM (universal LE)
  0xcafebabf, // FAT_MAGIC_64 (universal 64-bit BE)
  0xbfbafeca, // FAT_CIGAM_64 (universal 64-bit LE)
]);

const NESTED_BUNDLE_EXTENSIONS = new Set(['.app', '.framework', '.xpc', '.appex', '.plugin']);

/**
 * Checks whether a regular file begins with a Mach-O or universal binary magic header.
 * Avoids signing plain text / .NET managed .dll files that are not native Mach-O binaries.
 */
function isMachOFile(filePath) {
  let fd;
  try {
    const stat = fs.lstatSync(filePath);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 4) {
      return false;
    }
    fd = fs.openSync(filePath, 'r');
    const buf = Buffer.alloc(4);
    const bytesRead = fs.readSync(fd, buf, 0, 4, 0);
    if (bytesRead < 4) {
      return false;
    }
    const magic = buf.readUInt32BE(0);
    return MACHO_MAGICS.has(magic);
  } catch {
    return false;
  } finally {
    if (fd !== undefined) {
      try {
        fs.closeSync(fd);
      } catch {
        // ignore close failure
      }
    }
  }
}

/**
 * Reads CFBundleExecutable from a `.app/Contents/Info.plist` when available so we don't sign
 * an app's main executable ahead of its enclosing bundle (which causes codesign to validate
 * nested frameworks before they have been signed).
 */
function readBundleMainExecutable(bundleDir) {
  const plistPath = path.join(bundleDir, 'Contents', 'Info.plist');
  if (!fs.existsSync(plistPath)) {
    return null;
  }
  try {
    const content = fs.readFileSync(plistPath, 'utf-8');
    const match = /<key>\s*CFBundleExecutable\s*<\/key>\s*<string>\s*([^<]+?)\s*<\/string>/i.exec(content);
    if (match && match[1]) {
      return path.join(bundleDir, 'Contents', 'MacOS', match[1].trim());
    }
  } catch {
    // Binary plist or unreadable; fall back gracefully
  }
  return null;
}

/**
 * Collects all Mach-O files and nested macOS bundles inside an `.app` directory,
 * ordered strictly inside-out (deepest paths first; at identical depth, files before bundles).
 */
function collectInsideOutSignTargets(appPath) {
  const root = path.resolve(appPath);
  const machOFiles = [];
  const nestedBundles = [];
  const bundleMainExecutables = new Set();

  const rootMainExe = readBundleMainExecutable(root);
  if (rootMainExe) {
    bundleMainExecutables.add(rootMainExe);
  }

  function walk(dir) {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }

    for (const entry of entries) {
      const fullPath = path.join(dir, entry.name);
      if (entry.isSymbolicLink()) {
        continue;
      }
      if (entry.isDirectory()) {
        const ext = path.extname(entry.name).toLowerCase();
        if (ext === '.app' && fullPath !== root) {
          const nestedMainExe = readBundleMainExecutable(fullPath);
          if (nestedMainExe) {
            bundleMainExecutables.add(nestedMainExe);
          }
        }
        walk(fullPath);
        if (NESTED_BUNDLE_EXTENSIONS.has(ext) && fullPath !== root) {
          nestedBundles.push(fullPath);
        }
      } else if (entry.isFile() && isMachOFile(fullPath)) {
        machOFiles.push(fullPath);
      }
    }
  }

  walk(root);

  const rootMacOsDir = path.join(root, 'Contents', 'MacOS');
  const filteredMachO = machOFiles.filter(
    (file) => path.dirname(file) !== rootMacOsDir && !bundleMainExecutables.has(file),
  );

  const depth = (p) => p.split(path.sep).length;
  const combined = [
    ...filteredMachO.map((target) => ({ target, kind: 'file' })),
    ...nestedBundles.map((target) => ({ target, kind: 'bundle' })),
  ];

  combined.sort((a, b) => {
    const depthDiff = depth(b.target) - depth(a.target);
    if (depthDiff !== 0) {
      return depthDiff;
    }
    if (a.kind !== b.kind) {
      return a.kind === 'file' ? -1 : 1;
    }
    return a.target.localeCompare(b.target);
  });

  return {
    machOFiles: filteredMachO,
    nestedBundles,
    orderedTargets: combined.map((entry) => entry.target),
  };
}

function hasCommercialSigningIdentity(env = process.env) {
  return Boolean(
    (env.CSC_LINK && env.CSC_LINK.trim()) ||
      (env.CSC_NAME && env.CSC_NAME.trim()) ||
      (env.APPLE_DEVELOPER_IDENTITY && env.APPLE_DEVELOPER_IDENTITY.trim()),
  );
}

function runCommand(cmd, args) {
  execFileSync(cmd, args, { stdio: 'inherit' });
}

function adHocSignAppBundle(appPath, options = {}) {
  const exec = options.exec ?? runCommand;
  const bundleIdentifier = options.bundleIdentifier ?? 'com.codexflow.desktop';
  const resolvedApp = path.resolve(appPath);

  if (!fs.existsSync(resolvedApp)) {
    throw new Error(`macOS app bundle not found for ad-hoc signing: ${resolvedApp}`);
  }

  // Strip extended attributes (quarantine / FinderInfo / provenance) that invalidate codesign.
  try {
    exec('xattr', ['-cr', resolvedApp]);
  } catch {
    // Non-fatal if xattr is absent or nothing to strip
  }

  const { orderedTargets } = collectInsideOutSignTargets(resolvedApp);

  // 1. Sign every inner Mach-O file and nested bundle strictly inside-out.
  for (const target of orderedTargets) {
    exec('codesign', ['--force', '--sign', '-', target]);
  }

  // 2. Sign the top-level .app bundle last so Info.plist and sealed resources are bound cleanly.
  exec('codesign', ['--force', '--sign', '-', '--identifier', bundleIdentifier, resolvedApp]);

  // 3. Verify signature integrity strictly so broken bundles never ship.
  exec('codesign', ['--verify', '--deep', '--strict', '--verbose=2', resolvedApp]);
}

async function afterPack(context) {
  if (context.electronPlatformName !== 'darwin') {
    return;
  }

  if (hasCommercialSigningIdentity(process.env)) {
    console.log('[macos-sign] Commercial signing identity detected; delegating to electron-builder.');
    return;
  }

  const productFilename = context.packager.appInfo.productFilename;
  const appId = context.packager.appInfo.id || 'com.codexflow.desktop';
  const appPath = path.join(context.appOutDir, `${productFilename}.app`);

  console.log(`[macos-sign] Applying inside-out ad-hoc signature to ${appPath}`);
  adHocSignAppBundle(appPath, { bundleIdentifier: appId });
  console.log('[macos-sign] Ad-hoc signature verified successfully.');
}

module.exports = afterPack;
module.exports.default = afterPack;
module.exports.isMachOFile = isMachOFile;
module.exports.collectInsideOutSignTargets = collectInsideOutSignTargets;
module.exports.hasCommercialSigningIdentity = hasCommercialSigningIdentity;
module.exports.adHocSignAppBundle = adHocSignAppBundle;

if (require.main === module) {
  const target = process.argv[2];
  if (!target) {
    console.error('Usage: node scripts/macos-sign.cjs <path-to-.app>');
    process.exit(1);
  }
  adHocSignAppBundle(target);
}
