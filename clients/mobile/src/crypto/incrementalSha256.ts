import { bytesToHexLower, utf8Encode } from '@codexflow-shared/remoteProtocol';

const K = new Uint32Array([
  0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
  0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
  0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
  0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
  0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
  0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
  0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
  0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
]);

function rotr(x: number, n: number): number {
  return (x >>> n) | (x << (32 - n));
}

export class IncrementalSha256 {
  private readonly state = new Uint32Array([
    0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
    0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
  ]);
  private readonly block = new Uint8Array(64);
  private readonly w = new Uint32Array(64);
  private blockLen = 0;
  private totalBytesLo = 0;
  private totalBytesHi = 0;
  private finalized = false;

  public update(chunk: Uint8Array): this {
    if (this.finalized) {
      throw new Error('Cannot update finalized SHA-256 hasher.');
    }
    const len = chunk.byteLength;
    if (len === 0) {
      return this;
    }

    const nextLo = (this.totalBytesLo + len) >>> 0;
    if (nextLo < this.totalBytesLo) {
      this.totalBytesHi = (this.totalBytesHi + 1) >>> 0;
    }
    this.totalBytesLo = nextLo;

    let offset = 0;
    if (this.blockLen > 0) {
      const needed = 64 - this.blockLen;
      if (len < needed) {
        this.block.set(chunk, this.blockLen);
        this.blockLen += len;
        return this;
      }
      this.block.set(chunk.subarray(0, needed), this.blockLen);
      this.compressBlock(this.block, 0);
      this.blockLen = 0;
      offset = needed;
    }

    while (offset + 64 <= len) {
      this.compressBlock(chunk, offset);
      offset += 64;
    }

    if (offset < len) {
      this.block.set(chunk.subarray(offset), 0);
      this.blockLen = len - offset;
    }
    return this;
  }

  public digestBytes(): Uint8Array {
    if (!this.finalized) {
      this.finalized = true;
      const tailLen = this.blockLen;
      this.block[tailLen] = 0x80;
      let cursor = tailLen + 1;

      if (cursor > 56) {
        this.block.fill(0, cursor, 64);
        this.compressBlock(this.block, 0);
        cursor = 0;
      }
      this.block.fill(0, cursor, 56);

      const bitsLo = (this.totalBytesLo << 3) >>> 0;
      const bitsHi = ((this.totalBytesHi << 3) | (this.totalBytesLo >>> 29)) >>> 0;
      const view = new DataView(this.block.buffer, this.block.byteOffset, 64);
      view.setUint32(56, bitsHi, false);
      view.setUint32(60, bitsLo, false);
      this.compressBlock(this.block, 0);
    }

    const out = new Uint8Array(32);
    const outView = new DataView(out.buffer);
    for (let i = 0; i < 8; i += 1) {
      outView.setUint32(i * 4, this.state[i]!, false);
    }
    return out;
  }

  public digestHexLower(): string {
    return bytesToHexLower(this.digestBytes());
  }

  private compressBlock(buf: Uint8Array, offset: number): void {
    const w = this.w;
    for (let i = 0; i < 16; i += 1) {
      const idx = offset + i * 4;
      w[i] =
        ((buf[idx]! << 24) |
          (buf[idx + 1]! << 16) |
          (buf[idx + 2]! << 8) |
          buf[idx + 3]!) >>>
        0;
    }
    for (let i = 16; i < 64; i += 1) {
      const s0 = (rotr(w[i - 15]!, 7) ^ rotr(w[i - 15]!, 18) ^ (w[i - 15]! >>> 3)) >>> 0;
      const s1 = (rotr(w[i - 2]!, 17) ^ rotr(w[i - 2]!, 19) ^ (w[i - 2]! >>> 10)) >>> 0;
      w[i] = (w[i - 16]! + s0 + w[i - 7]! + s1) >>> 0;
    }

    let a = this.state[0]!;
    let b = this.state[1]!;
    let c = this.state[2]!;
    let d = this.state[3]!;
    let e = this.state[4]!;
    let f = this.state[5]!;
    let g = this.state[6]!;
    let h = this.state[7]!;

    for (let i = 0; i < 64; i += 1) {
      const S1 = (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) >>> 0;
      const ch = ((e & f) ^ (~e & g)) >>> 0;
      const temp1 = (h + S1 + ch + K[i]! + w[i]!) >>> 0;
      const S0 = (rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) >>> 0;
      const maj = ((a & b) ^ (a & c) ^ (b & c)) >>> 0;
      const temp2 = (S0 + maj) >>> 0;

      h = g;
      g = f;
      f = e;
      e = (d + temp1) >>> 0;
      d = c;
      c = b;
      b = a;
      a = (temp1 + temp2) >>> 0;
    }

    this.state[0] = (this.state[0]! + a) >>> 0;
    this.state[1] = (this.state[1]! + b) >>> 0;
    this.state[2] = (this.state[2]! + c) >>> 0;
    this.state[3] = (this.state[3]! + d) >>> 0;
    this.state[4] = (this.state[4]! + e) >>> 0;
    this.state[5] = (this.state[5]! + f) >>> 0;
    this.state[6] = (this.state[6]! + g) >>> 0;
    this.state[7] = (this.state[7]! + h) >>> 0;
  }
}

export function sha256Bytes(input: string | Uint8Array): Uint8Array {
  const bytes = typeof input === 'string' ? utf8Encode(input) : input;
  return new IncrementalSha256().update(bytes).digestBytes();
}

export function sha256HexLower(input: string | Uint8Array): string {
  return bytesToHexLower(sha256Bytes(input));
}

export function hmacSha256Bytes(key: Uint8Array, data: Uint8Array): Uint8Array {
  const normalizedKey = key.byteLength > 64 ? sha256Bytes(key) : key;
  const iKeyPad = new Uint8Array(64).fill(0x36);
  const oKeyPad = new Uint8Array(64).fill(0x5c);
  for (let i = 0; i < normalizedKey.byteLength; i += 1) {
    iKeyPad[i] = (normalizedKey[i]! ^ 0x36) & 0xff;
    oKeyPad[i] = (normalizedKey[i]! ^ 0x5c) & 0xff;
  }

  const innerHash = new IncrementalSha256().update(iKeyPad).update(data).digestBytes();
  return new IncrementalSha256().update(oKeyPad).update(innerHash).digestBytes();
}

export function hmacSha256HexLower(key: Uint8Array, data: Uint8Array): string {
  return bytesToHexLower(hmacSha256Bytes(key, data));
}

async function readBlobSliceBytes(slice: Blob): Promise<Uint8Array> {
  if (typeof slice.arrayBuffer === 'function') {
    const ab = await slice.arrayBuffer();
    return new Uint8Array(ab);
  }
  if (typeof FileReader !== 'undefined') {
    return new Promise<Uint8Array>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => {
        const res = reader.result;
        if (res instanceof ArrayBuffer) {
          resolve(new Uint8Array(res));
        } else {
          reject(new Error('FileReader did not return an ArrayBuffer.'));
        }
      };
      reader.onerror = () => reject(reader.error ?? new Error('Failed to read blob slice.'));
      reader.readAsArrayBuffer(slice);
    });
  }
  const ab = await new Response(slice).arrayBuffer();
  return new Uint8Array(ab);
}

export async function sha256BlobIncremental(
  blob: Blob,
  chunkBytes = 256 * 1024,
): Promise<string> {
  const hasher = new IncrementalSha256();
  for (let offset = 0; offset < blob.size; offset += chunkBytes) {
    const end = Math.min(blob.size, offset + chunkBytes);
    const chunk = await readBlobSliceBytes(blob.slice(offset, end));
    hasher.update(chunk);
  }
  return hasher.digestHexLower();
}

export async function sha256MultipartStream(
  head: Uint8Array,
  fileBlob: Blob,
  tail: Uint8Array,
  chunkBytes = 256 * 1024,
): Promise<{ fileSha256: string; bodySha256: string }> {
  const fileHasher = new IncrementalSha256();
  const bodyHasher = new IncrementalSha256();
  bodyHasher.update(head);

  for (let offset = 0; offset < fileBlob.size; offset += chunkBytes) {
    const end = Math.min(fileBlob.size, offset + chunkBytes);
    const chunk = await readBlobSliceBytes(fileBlob.slice(offset, end));
    fileHasher.update(chunk);
    bodyHasher.update(chunk);
  }

  bodyHasher.update(tail);
  return {
    fileSha256: fileHasher.digestHexLower(),
    bodySha256: bodyHasher.digestHexLower(),
  };
}
