import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { pruneUnneededExtras } from '../../scripts/prepare-libreoffice.mjs';

function touch(filePath: string): void {
  mkdirSync(join(filePath, '..'), { recursive: true });
  writeFileSync(filePath, 'stub', 'utf-8');
}

describe('prepare-libreoffice pruneUnneededExtras', () => {
  it('prunes redundant Windows/Linux LibreOffice assets while preserving headless conversion essentials', () => {
    const root = mkdtempSync(join(tmpdir(), 'cf-lo-win-'));
    try {
      // Essentials to keep
      touch(join(root, 'program', 'soffice.exe'));
      touch(join(root, 'program', 'mergedlo.dll'));
      touch(join(root, 'program', 'resource', 'common', 'services.rdb'));
      touch(join(root, 'program', 'resource', 'zh_CN', 'messages.mo'));
      touch(join(root, 'program', 'resource', 'zh_TW', 'messages.mo'));
      touch(join(root, 'share', 'registry', 'main.xcd'));
      touch(join(root, 'share', 'registry', 'res', 'fcfg_langpack_en-US.xcd'));
      touch(join(root, 'share', 'registry', 'res', 'fcfg_langpack_zh-CN.xcd'));
      touch(join(root, 'share', 'registry', 'res', 'registry_zh-CN.xcd'));
      touch(join(root, 'share', 'config', 'soffice.cfg', 'modules', 'swriter.xml'));
      touch(join(root, 'share', 'filter', 'signature.xml'));

      // Bloat to remove
      touch(join(root, 'LibreOffice_26.8.3_Win_x86-64.msi'));
      touch(join(root, 'System', 'msvcp.dll'));
      touch(join(root, 'System64', 'vcruntime.dll'));
      touch(join(root, 'Fonts', 'LinLibertine.ttf'));
      touch(join(root, 'help', 'en-US', 'default.html'));
      touch(join(root, 'program', 'resource', 'fr', 'messages.mo'));
      touch(join(root, 'program', 'resource', 'ja', 'messages.mo'));
      touch(join(root, 'share', 'config', 'images_karasa_jaga_svg.zip'));
      touch(join(root, 'share', 'config', 'images_colibre.zip'));
      touch(join(root, 'share', 'registry', 'res', 'fcfg_langpack_fr.xcd'));
      touch(join(root, 'share', 'registry', 'res', 'registry_ja.xcd'));
      touch(join(root, 'share', 'gallery', 'bullets.sdg'));

      pruneUnneededExtras(root);
      // Verify idempotency on second run
      pruneUnneededExtras(root);

      expect(existsSync(join(root, 'program', 'soffice.exe'))).toBe(true);
      expect(existsSync(join(root, 'program', 'resource', 'common', 'services.rdb'))).toBe(true);
      expect(existsSync(join(root, 'program', 'resource', 'zh_CN', 'messages.mo'))).toBe(true);
      expect(existsSync(join(root, 'program', 'resource', 'zh_TW', 'messages.mo'))).toBe(true);
      expect(existsSync(join(root, 'share', 'registry', 'main.xcd'))).toBe(true);
      expect(existsSync(join(root, 'share', 'registry', 'res', 'fcfg_langpack_en-US.xcd'))).toBe(true);
      expect(existsSync(join(root, 'share', 'registry', 'res', 'fcfg_langpack_zh-CN.xcd'))).toBe(true);
      expect(existsSync(join(root, 'share', 'registry', 'res', 'registry_zh-CN.xcd'))).toBe(true);
      expect(existsSync(join(root, 'share', 'config', 'soffice.cfg', 'modules', 'swriter.xml'))).toBe(true);
      expect(existsSync(join(root, 'share', 'filter', 'signature.xml'))).toBe(true);

      expect(existsSync(join(root, 'LibreOffice_26.8.3_Win_x86-64.msi'))).toBe(false);
      expect(existsSync(join(root, 'System'))).toBe(false);
      expect(existsSync(join(root, 'System64'))).toBe(false);
      expect(existsSync(join(root, 'Fonts'))).toBe(false);
      expect(existsSync(join(root, 'help'))).toBe(false);
      expect(existsSync(join(root, 'program', 'resource', 'fr'))).toBe(false);
      expect(existsSync(join(root, 'program', 'resource', 'ja'))).toBe(false);
      expect(existsSync(join(root, 'share', 'config', 'images_karasa_jaga_svg.zip'))).toBe(false);
      expect(existsSync(join(root, 'share', 'config', 'images_colibre.zip'))).toBe(false);
      expect(existsSync(join(root, 'share', 'registry', 'res', 'fcfg_langpack_fr.xcd'))).toBe(false);
      expect(existsSync(join(root, 'share', 'registry', 'res', 'registry_ja.xcd'))).toBe(false);
      expect(existsSync(join(root, 'share', 'gallery'))).toBe(false);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });

  it('prunes redundant macOS LibreOffice.app bundle resources', () => {
    const root = mkdtempSync(join(tmpdir(), 'cf-lo-mac-'));
    try {
      const macApp = join(root, 'LibreOffice.app', 'Contents');
      touch(join(macApp, 'MacOS', 'soffice'));
      touch(join(macApp, 'Resources', 'resource', 'zh_CN', 'messages.mo'));
      touch(join(macApp, 'Resources', 'resource', 'de', 'messages.mo'));
      touch(join(macApp, 'Resources', 'config', 'images_sukapura.zip'));
      touch(join(macApp, 'Resources', 'config', 'soffice.cfg', 'start.xml'));
      touch(join(macApp, 'Resources', 'registry', 'res', 'fcfg_langpack_en-US.xcd'));
      touch(join(macApp, 'Resources', 'registry', 'res', 'fcfg_langpack_de.xcd'));
      touch(join(macApp, 'Resources', 'fonts', 'Noto.ttf'));

      pruneUnneededExtras(root);

      expect(existsSync(join(macApp, 'MacOS', 'soffice'))).toBe(true);
      expect(existsSync(join(macApp, 'Resources', 'resource', 'zh_CN', 'messages.mo'))).toBe(true);
      expect(existsSync(join(macApp, 'Resources', 'config', 'soffice.cfg', 'start.xml'))).toBe(true);
      expect(existsSync(join(macApp, 'Resources', 'registry', 'res', 'fcfg_langpack_en-US.xcd'))).toBe(true);

      expect(existsSync(join(macApp, 'Resources', 'resource', 'de'))).toBe(false);
      expect(existsSync(join(macApp, 'Resources', 'config', 'images_sukapura.zip'))).toBe(false);
      expect(existsSync(join(macApp, 'Resources', 'registry', 'res', 'fcfg_langpack_de.xcd'))).toBe(false);
      expect(existsSync(join(macApp, 'Resources', 'fonts'))).toBe(false);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  });
});
