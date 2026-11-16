import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const desktopRoot = resolve(scriptDir, '..');
const repoRoot = resolve(desktopRoot, '../..');
const defaultPropsPath = resolve(repoRoot, 'Directory.Build.props');
const packageJsonPath = resolve(desktopRoot, 'package.json');

function parseVersionFromProps(propsPath) {
  const xml = readFileSync(propsPath, 'utf-8');
  const cleanXml = xml.replace(/<!--[\s\S]*?-->/g, '');
  const prefixMatch = cleanXml.match(/<VersionPrefix>\s*([^<\s]+)\s*<\/VersionPrefix>/i);
  if (!prefixMatch || !prefixMatch[1]) {
    throw new Error(`Missing <VersionPrefix> in ${propsPath}`);
  }
  const suffixMatch = cleanXml.match(/<VersionSuffix>\s*([^<\s]+)\s*<\/VersionSuffix>/i);
  const prefix = prefixMatch[1].trim();
  const suffix = suffixMatch?.[1]?.trim();
  return suffix ? `${prefix}-${suffix}` : prefix;
}

function validateSemVer(version) {
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(version)) {
    throw new Error(`Invalid SemVer version: "${version}"`);
  }
}

function main() {
  const args = process.argv.slice(2);
  let explicitVersion = null;
  let propsPath = defaultPropsPath;
  let checkOnly = false;
  let printOnly = false;

  for (let i = 0; i < args.length; i += 1) {
    const arg = args[i];
    if (arg === '--version' && args[i + 1]) {
      explicitVersion = args[i + 1].replace(/^v/i, '').trim();
      i += 1;
    } else if (arg === '--from-props' && args[i + 1]) {
      propsPath = resolve(args[i + 1]);
      i += 1;
    } else if (arg === '--check') {
      checkOnly = true;
    } else if (arg === '--print') {
      printOnly = true;
    }
  }

  const targetVersion = explicitVersion || parseVersionFromProps(propsPath);
  validateSemVer(targetVersion);

  if (printOnly) {
    process.stdout.write(`${targetVersion}\n`);
    return;
  }

  const pkgRaw = readFileSync(packageJsonPath, 'utf-8');
  const pkg = JSON.parse(pkgRaw);

  if (checkOnly) {
    if (pkg.version !== targetVersion) {
      throw new Error(
        `Version drift: clients/desktop/package.json has "${pkg.version}", expected "${targetVersion}" from ${propsPath}`,
      );
    }
    process.stdout.write(`Version check OK: ${targetVersion}\n`);
    return;
  }

  pkg.version = targetVersion;
  writeFileSync(packageJsonPath, `${JSON.stringify(pkg, null, 2)}\n`, 'utf-8');
  process.stdout.write(`Updated clients/desktop/package.json version to ${targetVersion}\n`);
}

main();
