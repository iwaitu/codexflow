export type DiffLineKind = 'meta' | 'hunk' | 'add' | 'del' | 'context';

export interface ParsedDiffLine {
  kind: DiffLineKind;
  text: string;
  oldLineNumber: number | null;
  newLineNumber: number | null;
}

export interface ParsedFileDiff {
  filePath: string;
  oldPath: string | null;
  newPath: string | null;
  lines: ParsedDiffLine[];
}

const HUNK_HEADER_RE = /^@@\s+-(\d+)(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@/;
const DIFF_GIT_RE = /^diff --git\s+(?:"?a\/([^"\n]+)"?|a\/(\S+))\s+(?:"?b\/([^"\n]+)"?|b\/(\S+))$/;

function normalizeDiffPath(raw: string): string {
  return raw.replace(/^"+|"+$/g, '').trim();
}

export function parseUnifiedDiff(unifiedDiff: string): ParsedFileDiff[] {
  if (!unifiedDiff || !unifiedDiff.trim()) {
    return [];
  }

  const lines = unifiedDiff.replace(/\r\n/g, '\n').split('\n');
  const sections: ParsedFileDiff[] = [];
  let current: ParsedFileDiff | null = null;
  let oldLine = 0;
  let newLine = 0;

  const ensureCurrent = (fallbackPath = 'patch') => {
    if (!current) {
      current = {
        filePath: fallbackPath,
        oldPath: null,
        newPath: null,
        lines: [],
      };
      sections.push(current);
    }
    return current;
  };

  for (const rawLine of lines) {
    const gitMatch = DIFF_GIT_RE.exec(rawLine);
    if (gitMatch) {
      const aPath = normalizeDiffPath(gitMatch[1] ?? gitMatch[2] ?? '');
      const bPath = normalizeDiffPath(gitMatch[3] ?? gitMatch[4] ?? '');
      const filePath = bPath && bPath !== '/dev/null' ? bPath : aPath || 'patch';
      current = {
        filePath,
        oldPath: aPath || null,
        newPath: bPath || null,
        lines: [{ kind: 'meta', text: rawLine, oldLineNumber: null, newLineNumber: null }],
      };
      sections.push(current);
      oldLine = 0;
      newLine = 0;
      continue;
    }

    if (
      rawLine.startsWith('index ') ||
      rawLine.startsWith('--- ') ||
      rawLine.startsWith('+++ ') ||
      rawLine.startsWith('new file mode ') ||
      rawLine.startsWith('deleted file mode ') ||
      rawLine.startsWith('rename from ') ||
      rawLine.startsWith('rename to ') ||
      rawLine.startsWith('Binary files ')
    ) {
      const target = ensureCurrent();
      if (rawLine.startsWith('--- a/')) {
        target.oldPath = normalizeDiffPath(rawLine.slice(6));
      } else if (rawLine.startsWith('+++ b/')) {
        target.newPath = normalizeDiffPath(rawLine.slice(6));
        if (target.filePath === 'patch' && target.newPath) {
          target.filePath = target.newPath;
        }
      }
      target.lines.push({ kind: 'meta', text: rawLine, oldLineNumber: null, newLineNumber: null });
      continue;
    }

    const hunkMatch = HUNK_HEADER_RE.exec(rawLine);
    if (hunkMatch) {
      const target = ensureCurrent();
      oldLine = Number.parseInt(hunkMatch[1]!, 10);
      newLine = Number.parseInt(hunkMatch[2]!, 10);
      target.lines.push({ kind: 'hunk', text: rawLine, oldLineNumber: null, newLineNumber: null });
      continue;
    }

    if (rawLine.startsWith('+') && !rawLine.startsWith('+++')) {
      const target = ensureCurrent();
      target.lines.push({
        kind: 'add',
        text: rawLine,
        oldLineNumber: null,
        newLineNumber: newLine > 0 ? newLine++ : null,
      });
      continue;
    }

    if (rawLine.startsWith('-') && !rawLine.startsWith('---')) {
      const target = ensureCurrent();
      target.lines.push({
        kind: 'del',
        text: rawLine,
        oldLineNumber: oldLine > 0 ? oldLine++ : null,
        newLineNumber: null,
      });
      continue;
    }

    if (current || rawLine.length > 0) {
      const target = ensureCurrent();
      if (rawLine.startsWith('\\')) {
        target.lines.push({ kind: 'meta', text: rawLine, oldLineNumber: null, newLineNumber: null });
      } else {
        target.lines.push({
          kind: 'context',
          text: rawLine,
          oldLineNumber: oldLine > 0 ? oldLine++ : null,
          newLineNumber: newLine > 0 ? newLine++ : null,
        });
      }
    }
  }

  return sections;
}
