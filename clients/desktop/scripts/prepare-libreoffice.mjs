import { execFileSync } from 'node:child_process';
import { cpSync, createWriteStream, existsSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const desktopRoot = resolve(__dirname, '..');
const targetRoot = resolve(desktopRoot, 'resources', 'libreoffice');

const DEFAULT_LIBREOFFICE_VERSION = process.env.LIBREOFFICE_VERSION || '25.8.7';

async function resolveLibreOfficeVersion(explicitVersion) {
  if (explicitVersion) {
    return explicitVersion;
  }
  try {
    const res = await fetch('https://download.documentfoundation.org/libreoffice/stable/', { redirect: 'follow' });
    if (res.ok) {
      const html = await res.text();
      const versions = Array.from(html.matchAll(/href="(\d+\.\d+\.\d+)\/"/g), (m) => m[1]);
      if (versions.length > 0) {
        versions.sort((a, b) => {
          const pa = a.split('.').map(Number);
          const pb = b.split('.').map(Number);
          for (let i = 0; i < 3; i++) {
            if ((pa[i] ?? 0) !== (pb[i] ?? 0)) return (pb[i] ?? 0) - (pa[i] ?? 0);
          }
          return 0;
        });
        return versions[0];
      }
    }
  } catch {
    // Fall back to default version when mirror index cannot be queried.
  }
  return DEFAULT_LIBREOFFICE_VERSION;
}

function parseArgs(argv) {
  const out = {
    platform: process.platform === 'win32' ? 'win' : process.platform === 'darwin' ? 'mac' : 'linux',
    arch: process.arch === 'arm64' ? 'arm64' : 'x64',
    version: process.env.LIBREOFFICE_VERSION || '',
    sourceDir: process.env.CODEXFLOW_LIBREOFFICE_SOURCE_DIR || '',
    skipDownload: false,
  };

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--platform' && argv[i + 1]) out.platform = argv[++i];
    else if (arg === '--arch' && argv[i + 1]) out.arch = argv[++i];
    else if (arg === '--version' && argv[i + 1]) out.version = argv[++i];
    else if (arg === '--source-dir' && argv[i + 1]) out.sourceDir = argv[++i];
    else if (arg === '--skip-download') out.skipDownload = true;
  }
  return out;
}

function findSofficeInTree(rootDir) {
  const candidates = [
    join(rootDir, 'program', 'soffice.exe'),
    join(rootDir, 'program', 'soffice'),
    join(rootDir, 'App', 'libreoffice', 'program', 'soffice.exe'),
    join(rootDir, 'LibreOffice.app', 'Contents', 'MacOS', 'soffice'),
    join(rootDir, 'soffice.exe'),
    join(rootDir, 'soffice'),
  ];
  return candidates.find((p) => existsSync(p)) ?? null;
}

function getOfficialDownloadSpec(platform, arch, version) {
  if (platform === 'win') {
    const msiName = `LibreOffice_${version}_Win_x86-64.msi`;
    return {
      fileName: msiName,
      url:
        process.env.LIBREOFFICE_ARCHIVE_URL ||
        `https://download.documentfoundation.org/libreoffice/stable/${version}/win/x86_64/${msiName}`,
    };
  }
  if (platform === 'mac') {
    const macArch = arch === 'arm64' ? 'aarch64' : 'x86_64';
    const dmgName = `LibreOffice_${version}_MacOS_${macArch}.dmg`;
    return {
      fileName: dmgName,
      url:
        process.env.LIBREOFFICE_ARCHIVE_URL ||
        `https://download.documentfoundation.org/libreoffice/stable/${version}/mac/${macArch}/${dmgName}`,
    };
  }
  const tarName = `LibreOffice_${version}_Linux_x86-64_deb.tar.gz`;
  return {
    fileName: tarName,
    url:
      process.env.LIBREOFFICE_ARCHIVE_URL ||
      `https://download.documentfoundation.org/libreoffice/stable/${version}/deb/x86_64/${tarName}`,
  };
}

async function downloadFile(url, destPath) {
  console.log(`Downloading LibreOffice from ${url}...`);
  const response = await fetch(url, { redirect: 'follow' });
  if (!response.ok || !response.body) {
    throw new Error(`Failed to download ${url}: HTTP ${response.status}`);
  }
  await pipeline(Readable.fromWeb(response.body), createWriteStream(destPath));
}

function pruneUnneededExtras(libreOfficeDir) {
  // Keep core Writer/Calc/Impress conversion binaries; remove large optional help/dict/gallery/template folders.
  for (const sub of [
    'help',
    'readmes',
    'CREDITS.fodt',
    join('share', 'extensions'),
    join('share', 'gallery'),
    join('share', 'template'),
    join('share', 'wizards'),
  ]) {
    const candidate = join(libreOfficeDir, sub);
    if (existsSync(candidate)) {
      rmSync(candidate, { recursive: true, force: true });
    }
  }
}

async function main() {
  const opts = parseArgs(process.argv.slice(2));

  if (opts.sourceDir) {
    const resolvedSource = resolve(opts.sourceDir);
    if (!existsSync(resolvedSource) || !findSofficeInTree(resolvedSource)) {
      throw new Error(`--source-dir "${resolvedSource}" does not contain soffice executable.`);
    }
    rmSync(targetRoot, { recursive: true, force: true });
    mkdirSync(targetRoot, { recursive: true });
    cpSync(resolvedSource, targetRoot, { recursive: true });
    pruneUnneededExtras(targetRoot);
    console.log(`Copied LibreOffice from ${resolvedSource} to ${targetRoot}`);
    return;
  }

  if (findSofficeInTree(targetRoot)) {
    console.log(`Bundled LibreOffice already present at ${targetRoot}`);
    return;
  }

  if (opts.skipDownload) {
    mkdirSync(targetRoot, { recursive: true });
    console.log(`Skipped LibreOffice download; ensured empty directory ${targetRoot}`);
    return;
  }

  const tempDir = resolve(desktopRoot, '.tmp-libreoffice-stage');
  rmSync(tempDir, { recursive: true, force: true });
  mkdirSync(tempDir, { recursive: true });

  try {
    const resolvedVersion = await resolveLibreOfficeVersion(opts.version);
    const spec = getOfficialDownloadSpec(opts.platform, opts.arch, resolvedVersion);
    const archivePath = join(tempDir, spec.fileName);
    await downloadFile(spec.url, archivePath);

    rmSync(targetRoot, { recursive: true, force: true });
    mkdirSync(targetRoot, { recursive: true });

    if (opts.platform === 'win') {
      const adminExtract = join(tempDir, 'msi-extract');
      mkdirSync(adminExtract, { recursive: true });
      execFileSync('msiexec.exe', ['/a', archivePath, '/qn', `TARGETDIR=${adminExtract}`], {
        stdio: 'inherit',
      });
      const candidateProgram = [
        join(adminExtract, 'LibreOffice'),
        join(adminExtract, 'Program Files', 'LibreOffice'),
        join(adminExtract, 'PFiles64', 'LibreOffice'),
        adminExtract,
      ].find((dir) => existsSync(join(dir, 'program', 'soffice.exe')));

      if (!candidateProgram) {
        throw new Error(`Could not locate program/soffice.exe inside extracted MSI at ${adminExtract}`);
      }
      cpSync(candidateProgram, targetRoot, { recursive: true });
      pruneUnneededExtras(targetRoot);
    } else if (opts.platform === 'mac') {
      const mountPoint = join(tempDir, 'dmg-mount');
      mkdirSync(mountPoint, { recursive: true });
      execFileSync('hdiutil', ['attach', archivePath, '-nobrowse', '-quiet', '-mountpoint', mountPoint], {
        stdio: 'inherit',
      });
      try {
        const appSrc = join(mountPoint, 'LibreOffice.app');
        if (!existsSync(appSrc)) {
          throw new Error(`LibreOffice.app not found in ${mountPoint}`);
        }
        cpSync(appSrc, join(targetRoot, 'LibreOffice.app'), { recursive: true });
      } finally {
        execFileSync('hdiutil', ['detach', mountPoint, '-quiet'], { stdio: 'inherit' });
      }
    } else {
      const debExtract = join(tempDir, 'deb-extract');
      const rootfs = join(tempDir, 'rootfs');
      mkdirSync(debExtract, { recursive: true });
      mkdirSync(rootfs, { recursive: true });
      execFileSync('tar', ['-xzf', archivePath, '-C', debExtract], { stdio: 'inherit' });
      execFileSync(
        'bash',
        [
          '-lc',
          `find "${debExtract}" -name '*.deb' -print0 | xargs -0 -n1 -I{} dpkg-deb -x "{}" "${rootfs}"`,
        ],
        { stdio: 'inherit' },
      );
      const optDir = join(rootfs, 'opt');
      const loFolder = execFileSync('bash', ['-lc', `ls -1 "${optDir}" | grep -i '^libreoffice' | head -n1`], {
        encoding: 'utf-8',
      }).trim();
      if (!loFolder) {
        throw new Error(`Could not find extracted /opt/libreoffice* folder under ${rootfs}`);
      }
      cpSync(join(optDir, loFolder), targetRoot, { recursive: true });
      pruneUnneededExtras(targetRoot);
    }

    const verified = findSofficeInTree(targetRoot);
    if (!verified) {
      throw new Error(`LibreOffice preparation completed but soffice binary was not found under ${targetRoot}`);
    }
    console.log(`Prepared bundled LibreOffice at ${verified}`);
  } finally {
    rmSync(tempDir, { recursive: true, force: true });
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
