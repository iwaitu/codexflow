// Zero-dependency ISO/IEC 18004 QR Code encoder (Byte mode, ECC Level L, Versions 1..16).
// Produces deterministic boolean module grids and crisp SVG `<path>` strings with zero external packages.

export interface QrMatrix {
  readonly version: number;
  readonly size: number;
  readonly modules: Uint8Array; // 1 = dark, 0 = light (length = size * size, row-major)
}

interface VersionSpec {
  readonly version: number;
  readonly ecPerBlock: number;
  readonly g1Blocks: number;
  readonly g1DataCw: number;
  readonly g2Blocks: number;
  readonly g2DataCw: number;
  readonly alignments: readonly number[];
}

const VERSION_SPECS: readonly VersionSpec[] = [
  { version: 1, ecPerBlock: 7, g1Blocks: 1, g1DataCw: 19, g2Blocks: 0, g2DataCw: 0, alignments: [] },
  { version: 2, ecPerBlock: 10, g1Blocks: 1, g1DataCw: 34, g2Blocks: 0, g2DataCw: 0, alignments: [6, 18] },
  { version: 3, ecPerBlock: 15, g1Blocks: 1, g1DataCw: 55, g2Blocks: 0, g2DataCw: 0, alignments: [6, 22] },
  { version: 4, ecPerBlock: 20, g1Blocks: 1, g1DataCw: 80, g2Blocks: 0, g2DataCw: 0, alignments: [6, 26] },
  { version: 5, ecPerBlock: 26, g1Blocks: 1, g1DataCw: 108, g2Blocks: 0, g2DataCw: 0, alignments: [6, 30] },
  { version: 6, ecPerBlock: 18, g1Blocks: 2, g1DataCw: 68, g2Blocks: 0, g2DataCw: 0, alignments: [6, 34] },
  { version: 7, ecPerBlock: 20, g1Blocks: 2, g1DataCw: 78, g2Blocks: 0, g2DataCw: 0, alignments: [6, 22, 38] },
  { version: 8, ecPerBlock: 24, g1Blocks: 2, g1DataCw: 97, g2Blocks: 0, g2DataCw: 0, alignments: [6, 24, 42] },
  { version: 9, ecPerBlock: 30, g1Blocks: 2, g1DataCw: 116, g2Blocks: 0, g2DataCw: 0, alignments: [6, 26, 46] },
  { version: 10, ecPerBlock: 18, g1Blocks: 2, g1DataCw: 68, g2Blocks: 2, g2DataCw: 69, alignments: [6, 28, 50] },
  { version: 11, ecPerBlock: 20, g1Blocks: 4, g1DataCw: 81, g2Blocks: 0, g2DataCw: 0, alignments: [6, 30, 54] },
  { version: 12, ecPerBlock: 24, g1Blocks: 2, g1DataCw: 92, g2Blocks: 2, g2DataCw: 93, alignments: [6, 32, 58] },
  { version: 13, ecPerBlock: 26, g1Blocks: 4, g1DataCw: 107, g2Blocks: 0, g2DataCw: 0, alignments: [6, 34, 62] },
  { version: 14, ecPerBlock: 30, g1Blocks: 3, g1DataCw: 115, g2Blocks: 1, g2DataCw: 116, alignments: [6, 26, 46, 66] },
  { version: 15, ecPerBlock: 22, g1Blocks: 5, g1DataCw: 87, g2Blocks: 1, g2DataCw: 88, alignments: [6, 26, 48, 70] },
  { version: 16, ecPerBlock: 24, g1Blocks: 5, g1DataCw: 98, g2Blocks: 1, g2DataCw: 99, alignments: [6, 26, 50, 74] },
];

const GF_EXP = new Uint8Array(512);
const GF_LOG = new Uint8Array(256);
(() => {
  let x = 1;
  for (let i = 0; i < 255; i += 1) {
    GF_EXP[i] = x;
    GF_LOG[x] = i;
    x <<= 1;
    if (x & 0x100) {
      x ^= 0x11d;
    }
  }
  for (let i = 255; i < 512; i += 1) {
    GF_EXP[i] = GF_EXP[i - 255]!;
  }
})();

function gfMul(a: number, b: number): number {
  if (a === 0 || b === 0) {
    return 0;
  }
  return GF_EXP[GF_LOG[a]! + GF_LOG[b]!]!;
}

function buildRsGenerator(degree: number): Uint8Array {
  let gen = new Uint8Array([1]);
  for (let i = 0; i < degree; i += 1) {
    const next = new Uint8Array(gen.length + 1);
    const root = GF_EXP[i]!;
    for (let j = 0; j < gen.length; j += 1) {
      const c = gen[j]!;
      next[j] = (next[j]! ^ c) & 0xff;
      next[j + 1] = (next[j + 1]! ^ gfMul(c, root)) & 0xff;
    }
    gen = next;
  }
  return gen;
}

function computeRsRemainder(data: Uint8Array, ecCount: number): Uint8Array {
  const gen = buildRsGenerator(ecCount);
  const rem = new Uint8Array(ecCount);
  for (let i = 0; i < data.length; i += 1) {
    const factor = (data[i]! ^ rem[0]!) & 0xff;
    rem.copyWithin(0, 1);
    rem[ecCount - 1] = 0;
    for (let j = 0; j < ecCount; j += 1) {
      rem[j] = (rem[j]! ^ gfMul(gen[j + 1]!, factor)) & 0xff;
    }
  }
  return rem;
}

class BitBuffer {
  private readonly bytes: number[] = [];
  private bitLength = 0;

  public pushBits(value: number, count: number): void {
    for (let i = count - 1; i >= 0; i -= 1) {
      this.pushBit(((value >>> i) & 1) === 1);
    }
  }

  public pushBit(bit: boolean): void {
    const idx = this.bitLength >>> 3;
    if (this.bytes.length <= idx) {
      this.bytes.push(0);
    }
    if (bit) {
      this.bytes[idx] = this.bytes[idx]! | (0x80 >>> (this.bitLength & 7));
    }
    this.bitLength += 1;
  }

  public get length(): number {
    return this.bitLength;
  }

  public toCodewords(totalDataCw: number): Uint8Array {
    const out = new Uint8Array(totalDataCw);
    for (let i = 0; i < Math.min(out.length, this.bytes.length); i += 1) {
      out[i] = this.bytes[i]!;
    }
    const padBytes = [0xec, 0x11];
    let idx = Math.ceil(this.bitLength / 8);
    let padIdx = 0;
    while (idx < totalDataCw) {
      out[idx] = padBytes[padIdx % 2]!;
      idx += 1;
      padIdx += 1;
    }
    return out;
  }
}

function chooseVersion(byteLength: number): VersionSpec {
  for (const spec of VERSION_SPECS) {
    const totalDataCw = spec.g1Blocks * spec.g1DataCw + spec.g2Blocks * spec.g2DataCw;
    const countBits = spec.version <= 9 ? 8 : 16;
    const neededBits = 4 + countBits + byteLength * 8;
    if (neededBits <= totalDataCw * 8) {
      return spec;
    }
  }
  throw new Error(`QR payload (${byteLength} bytes) exceeds supported capacity.`);
}

function buildInterleavedCodewords(payloadBytes: Uint8Array, spec: VersionSpec): Uint8Array {
  const totalDataCw = spec.g1Blocks * spec.g1DataCw + spec.g2Blocks * spec.g2DataCw;
  const countBits = spec.version <= 9 ? 8 : 16;
  const bb = new BitBuffer();
  bb.pushBits(0b0100, 4); // Byte mode
  bb.pushBits(payloadBytes.length, countBits);
  for (let i = 0; i < payloadBytes.length; i += 1) {
    bb.pushBits(payloadBytes[i]!, 8);
  }
  const terminator = Math.min(4, totalDataCw * 8 - bb.length);
  bb.pushBits(0, terminator);
  while (bb.length % 8 !== 0) {
    bb.pushBit(false);
  }

  const dataCw = bb.toCodewords(totalDataCw);
  const blocks: Uint8Array[] = [];
  const eccBlocks: Uint8Array[] = [];
  let offset = 0;

  for (let i = 0; i < spec.g1Blocks; i += 1) {
    const blk = dataCw.subarray(offset, offset + spec.g1DataCw);
    offset += spec.g1DataCw;
    blocks.push(blk);
    eccBlocks.push(computeRsRemainder(blk, spec.ecPerBlock));
  }
  for (let i = 0; i < spec.g2Blocks; i += 1) {
    const blk = dataCw.subarray(offset, offset + spec.g2DataCw);
    offset += spec.g2DataCw;
    blocks.push(blk);
    eccBlocks.push(computeRsRemainder(blk, spec.ecPerBlock));
  }

  const maxDataLen = Math.max(spec.g1DataCw, spec.g2DataCw);
  const totalCw = totalDataCw + (spec.g1Blocks + spec.g2Blocks) * spec.ecPerBlock;
  const out = new Uint8Array(totalCw);
  let writeIdx = 0;

  for (let col = 0; col < maxDataLen; col += 1) {
    for (let b = 0; b < blocks.length; b += 1) {
      const blk = blocks[b]!;
      if (col < blk.length) {
        out[writeIdx++] = blk[col]!;
      }
    }
  }
  for (let col = 0; col < spec.ecPerBlock; col += 1) {
    for (let b = 0; b < eccBlocks.length; b += 1) {
      out[writeIdx++] = eccBlocks[b]![col]!;
    }
  }
  return out;
}

function computeFormatBits(maskPattern: number): number {
  const data = (0b01 << 3) | (maskPattern & 0x07); // Level L = 01
  let d = data << 10;
  for (let shift = 14; shift >= 10; shift -= 1) {
    if (((d >>> shift) & 1) !== 0) {
      d ^= 0x537 << (shift - 10);
    }
  }
  return ((data << 10) | d) ^ 0x5412;
}

function computeVersionBits(version: number): number {
  let d = version << 12;
  for (let shift = 17; shift >= 12; shift -= 1) {
    if (((d >>> shift) & 1) !== 0) {
      d ^= 0x1f25 << (shift - 12);
    }
  }
  return (version << 12) | d;
}

function applyMaskCondition(mask: number, r: number, c: number): boolean {
  switch (mask) {
    case 0:
      return (r + c) % 2 === 0;
    case 1:
      return r % 2 === 0;
    case 2:
      return c % 3 === 0;
    case 3:
      return (r + c) % 3 === 0;
    case 4:
      return (Math.floor(r / 2) + Math.floor(c / 3)) % 2 === 0;
    case 5:
      return ((r * c) % 2) + ((r * c) % 3) === 0;
    case 6:
      return (((r * c) % 2) + ((r * c) % 3)) % 2 === 0;
    case 7:
      return (((r + c) % 2) + ((r * c) % 3)) % 2 === 0;
    default:
      return false;
  }
}

function drawFinderPattern(modules: Uint8Array, fnMask: Uint8Array, size: number, topRow: number, leftCol: number): void {
  for (let dr = -1; dr <= 7; dr += 1) {
    const r = topRow + dr;
    if (r < 0 || r >= size) {
      continue;
    }
    for (let dc = -1; dc <= 7; dc += 1) {
      const c = leftCol + dc;
      if (c < 0 || c >= size) {
        continue;
      }
      const inOuter = dr >= 0 && dr <= 6 && dc >= 0 && dc <= 6;
      const onOuterBorder = dr === 0 || dr === 6 || dc === 0 || dc === 6;
      const inInner = dr >= 2 && dr <= 4 && dc >= 2 && dc <= 4;
      const dark = inOuter && (onOuterBorder || inInner);
      const idx = r * size + c;
      modules[idx] = dark ? 1 : 0;
      fnMask[idx] = 1;
    }
  }
}

function drawAlignmentPattern(modules: Uint8Array, fnMask: Uint8Array, size: number, centerR: number, centerC: number): void {
  for (let dr = -2; dr <= 2; dr += 1) {
    for (let dc = -2; dc <= 2; dc += 1) {
      const dark = Math.max(Math.abs(dr), Math.abs(dc)) !== 1;
      const idx = (centerR + dr) * size + (centerC + dc);
      modules[idx] = dark ? 1 : 0;
      fnMask[idx] = 1;
    }
  }
}

function placeFunctionPatterns(spec: VersionSpec): {
  size: number;
  modules: Uint8Array;
  fnMask: Uint8Array;
} {
  const size = 17 + 4 * spec.version;
  const modules = new Uint8Array(size * size);
  const fnMask = new Uint8Array(size * size);

  drawFinderPattern(modules, fnMask, size, 0, 0);
  drawFinderPattern(modules, fnMask, size, 0, size - 7);
  drawFinderPattern(modules, fnMask, size, size - 7, 0);

  // Alignment patterns
  const align = spec.alignments;
  for (let i = 0; i < align.length; i += 1) {
    for (let j = 0; j < align.length; j += 1) {
      const isCornerOverlap =
        (i === 0 && j === 0) ||
        (i === 0 && j === align.length - 1) ||
        (i === align.length - 1 && j === 0);
      if (!isCornerOverlap) {
        drawAlignmentPattern(modules, fnMask, size, align[i]!, align[j]!);
      }
    }
  }

  // Timing patterns
  for (let i = 8; i < size - 8; i += 1) {
    const dark = i % 2 === 0 ? 1 : 0;
    const rIdx = 6 * size + i;
    if (fnMask[rIdx] === 0) {
      modules[rIdx] = dark;
      fnMask[rIdx] = 1;
    }
    const cIdx = i * size + 6;
    if (fnMask[cIdx] === 0) {
      modules[cIdx] = dark;
      fnMask[cIdx] = 1;
    }
  }

  // Reserve format & dark module slots
  for (let i = 0; i < 9; i += 1) {
    if (i !== 6) {
      fnMask[8 * size + i] = 1;
      fnMask[i * size + 8] = 1;
    }
  }
  for (let i = size - 8; i < size; i += 1) {
    fnMask[8 * size + i] = 1;
  }
  for (let i = size - 7; i < size; i += 1) {
    fnMask[i * size + 8] = 1;
  }
  const darkModuleIdx = (4 * spec.version + 9) * size + 8;
  modules[darkModuleIdx] = 1;
  fnMask[darkModuleIdx] = 1;

  // Version info blocks (version >= 7)
  if (spec.version >= 7) {
    const vBits = computeVersionBits(spec.version);
    for (let i = 0; i < 18; i += 1) {
      const dark = ((vBits >>> i) & 1) === 1 ? 1 : 0;
      const row = Math.floor(i / 3);
      const col = size - 11 + (i % 3);
      modules[row * size + col] = dark;
      fnMask[row * size + col] = 1;
      modules[col * size + row] = dark;
      fnMask[col * size + row] = 1;
    }
  }

  return { size, modules, fnMask };
}

function drawFormatInfo(modules: Uint8Array, size: number, mask: number): void {
  const fmt = computeFormatBits(mask);
  for (let i = 0; i < 15; i += 1) {
    const bit = ((fmt >>> i) & 1) === 1 ? 1 : 0;
    // Copy 1 around top-left finder
    if (i < 6) {
      modules[i * size + 8] = bit;
    } else if (i < 8) {
      modules[(i + 1) * size + 8] = bit;
    } else {
      modules[(size - 15 + i) * size + 8] = bit;
    }

    // Copy 2 around bottom-left & top-right finders
    if (i < 8) {
      modules[8 * size + (size - i - 1)] = bit;
    } else if (i < 9) {
      modules[8 * size + 7] = bit;
    } else {
      modules[8 * size + (14 - i)] = bit;
    }
  }
}

function placeCodewords(
  modules: Uint8Array,
  fnMask: Uint8Array,
  size: number,
  codewords: Uint8Array,
): void {
  let bitIdx = 0;
  const totalBits = codewords.length * 8;
  let upward = true;

  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) {
      right = 5;
    }
    for (let step = 0; step < size; step += 1) {
      const r = upward ? size - 1 - step : step;
      for (let colOffset = 0; colOffset < 2; colOffset += 1) {
        const c = right - colOffset;
        const idx = r * size + c;
        if (fnMask[idx] === 1) {
          continue;
        }
        let bit = 0;
        if (bitIdx < totalBits) {
          const byteVal = codewords[bitIdx >>> 3]!;
          bit = (byteVal >>> (7 - (bitIdx & 7))) & 1;
          bitIdx += 1;
        }
        modules[idx] = bit;
      }
    }
    upward = !upward;
  }
}

function scorePenalty(modules: Uint8Array, size: number): number {
  let penalty = 0;

  // Rule 1: runs of 5+ same color in rows and columns
  for (let r = 0; r < size; r += 1) {
    let rowRun = 1;
    let colRun = 1;
    let prevRow = modules[r * size]!;
    let prevCol = modules[r]!;
    for (let i = 1; i < size; i += 1) {
      const curRow = modules[r * size + i]!;
      if (curRow === prevRow) {
        rowRun += 1;
      } else {
        if (rowRun >= 5) {
          penalty += 3 + (rowRun - 5);
        }
        prevRow = curRow;
        rowRun = 1;
      }

      const curCol = modules[i * size + r]!;
      if (curCol === prevCol) {
        colRun += 1;
      } else {
        if (colRun >= 5) {
          penalty += 3 + (colRun - 5);
        }
        prevCol = curCol;
        colRun = 1;
      }
    }
    if (rowRun >= 5) {
      penalty += 3 + (rowRun - 5);
    }
    if (colRun >= 5) {
      penalty += 3 + (colRun - 5);
    }
  }

  // Rule 2: 2x2 blocks of same color
  for (let r = 0; r < size - 1; r += 1) {
    for (let c = 0; c < size - 1; c += 1) {
      const v = modules[r * size + c]!;
      if (
        v === modules[r * size + c + 1] &&
        v === modules[(r + 1) * size + c] &&
        v === modules[(r + 1) * size + c + 1]
      ) {
        penalty += 3;
      }
    }
  }

  // Rule 3: finder-like 1:1:3:1:1 patterns
  for (let r = 0; r < size; r += 1) {
    for (let c = 0; c <= size - 11; c += 1) {
      if (matchesFinderLikeRow(modules, size, r, c) || matchesFinderLikeCol(modules, size, c, r)) {
        penalty += 40;
      }
    }
  }

  // Rule 4: dark ratio balance
  let darkCount = 0;
  for (let i = 0; i < modules.length; i += 1) {
    darkCount += modules[i]!;
  }
  const pct = (darkCount * 100) / modules.length;
  const k = Math.floor(Math.abs(pct - 50) / 5);
  penalty += k * 10;

  return penalty;
}

const FINDER_LIKE_A = [1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0];
const FINDER_LIKE_B = [0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1];

function matchesFinderLikeRow(modules: Uint8Array, size: number, r: number, startC: number): boolean {
  let matchA = true;
  let matchB = true;
  for (let i = 0; i < 11; i += 1) {
    const v = modules[r * size + startC + i]!;
    if (v !== FINDER_LIKE_A[i]) {
      matchA = false;
    }
    if (v !== FINDER_LIKE_B[i]) {
      matchB = false;
    }
  }
  return matchA || matchB;
}

function matchesFinderLikeCol(modules: Uint8Array, size: number, startR: number, c: number): boolean {
  let matchA = true;
  let matchB = true;
  for (let i = 0; i < 11; i += 1) {
    const v = modules[(startR + i) * size + c]!;
    if (v !== FINDER_LIKE_A[i]) {
      matchA = false;
    }
    if (v !== FINDER_LIKE_B[i]) {
      matchB = false;
    }
  }
  return matchA || matchB;
}

export function encodeQrMatrix(text: string): QrMatrix {
  const bytes = new TextEncoder().encode(text);
  const spec = chooseVersion(bytes.length);
  const codewords = buildInterleavedCodewords(bytes, spec);
  const base = placeFunctionPatterns(spec);
  placeCodewords(base.modules, base.fnMask, base.size, codewords);

  let bestPenalty = Number.POSITIVE_INFINITY;
  let bestModules: Uint8Array | null = null;

  for (let mask = 0; mask < 8; mask += 1) {
    const candidate = base.modules.slice();
    for (let r = 0; r < base.size; r += 1) {
      for (let c = 0; c < base.size; c += 1) {
        const idx = r * base.size + c;
        if (base.fnMask[idx] === 0 && applyMaskCondition(mask, r, c)) {
          candidate[idx] = candidate[idx]! ^ 1;
        }
      }
    }
    drawFormatInfo(candidate, base.size, mask);
    const p = scorePenalty(candidate, base.size);
    if (p < bestPenalty) {
      bestPenalty = p;
      bestModules = candidate;
    }
  }

  return {
    version: spec.version,
    size: base.size,
    modules: bestModules ?? base.modules,
  };
}

export function qrMatrixToSvgPath(matrix: QrMatrix): string {
  const parts: string[] = [];
  for (let r = 0; r < matrix.size; r += 1) {
    let c = 0;
    while (c < matrix.size) {
      if (matrix.modules[r * matrix.size + c] === 1) {
        const startC = c;
        while (c < matrix.size && matrix.modules[r * matrix.size + c] === 1) {
          c += 1;
        }
        parts.push(`M${startC},${r}h${c - startC}v1h-${c - startC}z`);
      } else {
        c += 1;
      }
    }
  }
  return parts.join('');
}
