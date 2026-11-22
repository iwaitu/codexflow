import { createHash } from 'node:crypto';
import { constants as fsConstants, existsSync, mkdirSync, readdirSync, unlinkSync } from 'node:fs';
import { open, statfs } from 'node:fs/promises';
import { join } from 'node:path';
import { buildSingleFileMultipart, parseSingleFileMultipart } from '@codexflow-shared/multipartBuilder';
import { isLowerHex } from '@codexflow-shared/remoteProtocol';
import { randomHex32 } from './remoteCrypto';

export const MAX_GLOBAL_STAGING_BYTES = 128 * 1024 * 1024;
export const MAX_SINGLE_FILE_BYTES = 20 * 1024 * 1024;
export const MIN_REQUIRED_FREE_DISK_BYTES = 500 * 1024 * 1024;

export interface StagedUploadForwardPayload {
  readonly contentType: string;
  readonly bodyBytes: Uint8Array;
  readonly fileSha256: string;
  readonly fileSizeBytes: number;
}

export type StageUploadResult =
  | { readonly ok: true; readonly forward: StagedUploadForwardPayload }
  | { readonly ok: false; readonly statusCode: number; readonly code: string; readonly message: string };

export interface RemoteUploadStagingOptions {
  readonly userDataDir: string;
  readonly maxGlobalBytes?: number;
  readonly maxSingleFileBytes?: number;
  readonly minFreeDiskBytes?: number;
  readonly getFreeDiskBytes?: (dirPath: string) => Promise<number>;
}

async function defaultFreeDiskBytes(dirPath: string): Promise<number> {
  const stats = await statfs(dirPath);
  return Number(stats.bavail) * Number(stats.bsize);
}

export class RemoteUploadStaging {
  private readonly stagingDir: string;
  private readonly maxGlobalBytes: number;
  private readonly maxSingleFileBytes: number;
  private readonly minFreeDiskBytes: number;
  private readonly getFreeDiskBytes: (dirPath: string) => Promise<number>;
  private activeStagedBytes = 0;

  constructor(options: RemoteUploadStagingOptions) {
    this.stagingDir = join(options.userDataDir, 'remote-upload-staging');
    this.maxGlobalBytes = options.maxGlobalBytes ?? MAX_GLOBAL_STAGING_BYTES;
    this.maxSingleFileBytes = options.maxSingleFileBytes ?? MAX_SINGLE_FILE_BYTES;
    this.minFreeDiskBytes = options.minFreeDiskBytes ?? MIN_REQUIRED_FREE_DISK_BYTES;
    this.getFreeDiskBytes = options.getFreeDiskBytes ?? defaultFreeDiskBytes;
    mkdirSync(this.stagingDir, { recursive: true, mode: 0o700 });
  }

  public getStagingDir(): string {
    return this.stagingDir;
  }

  public getActiveStagedBytes(): number {
    return this.activeStagedBytes;
  }

  public cleanupOrphansOnStartup(): number {
    if (!existsSync(this.stagingDir)) {
      return 0;
    }
    let removed = 0;
    for (const name of readdirSync(this.stagingDir)) {
      if (name.startsWith('.stg_') && name.endsWith('.tmp')) {
        try {
          unlinkSync(join(this.stagingDir, name));
          removed += 1;
        } catch {
          // Best-effort startup orphan cleanup
        }
      }
    }
    return removed;
  }

  public async stageAndVerifyMultipartUpload(params: {
    pairingId: string;
    uploadId: string;
    declaredContentLength: number;
    declaredBodySha256: string;
    declaredFileSha256: string;
    contentTypeHeader: string;
    chunks: AsyncIterable<Uint8Array>;
    signal?: AbortSignal;
  }): Promise<StageUploadResult> {
    if (!/^[A-Za-z0-9_-]{1,128}$/.test(params.uploadId)) {
      return {
        ok: false,
        statusCode: 400,
        code: 'invalid_upload_id',
        message: 'X-CF-Upload-Id must be 1..128 safe characters.',
      };
    }

    if (!isLowerHex(params.declaredBodySha256, 64) || !isLowerHex(params.declaredFileSha256, 64)) {
      return {
        ok: false,
        statusCode: 400,
        code: 'invalid_digest_header',
        message: 'X-CF-Body-Sha256 and X-CF-File-Sha256 must be 64 lowercase hex characters.',
      };
    }

    const maxBodyAllowed = this.maxSingleFileBytes + 64 * 1024;
    if (params.declaredContentLength <= 0 || params.declaredContentLength > maxBodyAllowed) {
      return {
        ok: false,
        statusCode: 413,
        code: 'payload_too_large',
        message: `Upload body exceeds limit of ${maxBodyAllowed} bytes.`,
      };
    }

    mkdirSync(this.stagingDir, { recursive: true, mode: 0o700 });
    const freeBytes = await this.getFreeDiskBytes(this.stagingDir);
    if (freeBytes < this.minFreeDiskBytes) {
      return {
        ok: false,
        statusCode: 507,
        code: 'insufficient_storage',
        message: 'Desktop disk free space is below 500 MiB safety threshold for remote upload staging.',
      };
    }

    if (this.activeStagedBytes + params.declaredContentLength > this.maxGlobalBytes) {
      return {
        ok: false,
        statusCode: 507,
        code: 'staging_quota_exceeded',
        message: 'Remote upload global 128 MiB staging quota exceeded; retry later.',
      };
    }

    const pairingHash8 = createHash('sha256')
      .update(params.pairingId, 'utf8')
      .digest('hex')
      .toLowerCase()
      .slice(0, 8);
    const tmpFileName = `.stg_${pairingHash8}_${params.uploadId}_${randomHex32().slice(0, 8)}.tmp`;
    const tmpPath = join(this.stagingDir, tmpFileName);

    let reservedBytes = 0;
    const handle = await open(
      tmpPath,
      fsConstants.O_WRONLY | fsConstants.O_CREAT | fsConstants.O_EXCL,
      0o600,
    );

    try {
      const bodyHasher = createHash('sha256');
      let totalWritten = 0;

      for await (const chunk of params.chunks) {
        if (params.signal?.aborted) {
          throw new Error('aborted');
        }
        if (chunk.byteLength === 0) {
          continue;
        }
        if (totalWritten + chunk.byteLength > params.declaredContentLength) {
          return {
            ok: false,
            statusCode: 400,
            code: 'content_length_mismatch',
            message: 'Uploaded stream exceeded declared Content-Length.',
          };
        }
        if (this.activeStagedBytes + chunk.byteLength > this.maxGlobalBytes) {
          return {
            ok: false,
            statusCode: 507,
            code: 'staging_quota_exceeded',
            message: 'Remote upload global staging quota exceeded.',
          };
        }
        this.activeStagedBytes += chunk.byteLength;
        reservedBytes += chunk.byteLength;
        totalWritten += chunk.byteLength;

        bodyHasher.update(chunk);
        await handle.write(chunk, 0, chunk.byteLength, null);
      }

      if (totalWritten !== params.declaredContentLength) {
        return {
          ok: false,
          statusCode: 400,
          code: 'content_length_mismatch',
          message: 'Uploaded stream truncated before declared Content-Length.',
        };
      }

      const actualBodySha256 = bodyHasher.digest('hex').toLowerCase();
      if (actualBodySha256 !== params.declaredBodySha256) {
        return {
          ok: false,
          statusCode: 400,
          code: 'body_sha256_mismatch',
          message: 'Uploaded body SHA-256 does not match X-CF-Body-Sha256.',
        };
      }

      await handle.sync();
      await handle.close();

      // Re-read verified body from private exclusive temp file & parse single file part
      const readHandle = await open(tmpPath, fsConstants.O_RDONLY);
      let fullBody: Buffer;
      try {
        fullBody = await readHandle.readFile();
      } finally {
        await readHandle.close();
      }

      let parsedPart: ReturnType<typeof parseSingleFileMultipart>;
      try {
        parsedPart = parseSingleFileMultipart(params.contentTypeHeader, fullBody);
      } catch (err) {
        return {
          ok: false,
          statusCode: 400,
          code: 'invalid_multipart_payload',
          message: err instanceof Error ? err.message : 'Invalid single-file multipart upload.',
        };
      }

      if (parsedPart.fileBytes.byteLength <= 0 || parsedPart.fileBytes.byteLength > this.maxSingleFileBytes) {
        return {
          ok: false,
          statusCode: 413,
          code: 'file_size_out_of_range',
          message: `Uploaded file part must be 1..${this.maxSingleFileBytes} bytes.`,
        };
      }

      const actualFileSha256 = createHash('sha256')
        .update(parsedPart.fileBytes)
        .digest('hex')
        .toLowerCase();
      if (actualFileSha256 !== params.declaredFileSha256) {
        return {
          ok: false,
          statusCode: 400,
          code: 'file_sha256_mismatch',
          message: 'Extracted file part SHA-256 does not match X-CF-File-Sha256.',
        };
      }

      const rebuilt = buildSingleFileMultipart({
        fieldName: 'file',
        fileName: parsedPart.fileName,
        contentType: parsedPart.contentType,
        fileBytes: parsedPart.fileBytes,
        boundary: `cf-remote-${randomHex32().slice(0, 16)}`,
      });

      return {
        ok: true,
        forward: {
          contentType: rebuilt.contentType,
          bodyBytes: rebuilt.bodyBytes,
          fileSha256: actualFileSha256,
          fileSizeBytes: parsedPart.fileBytes.byteLength,
        },
      };
    } catch (err) {
      if (params.signal?.aborted || (err instanceof Error && err.message === 'aborted')) {
        return {
          ok: false,
          statusCode: 499,
          code: 'upload_aborted',
          message: 'Upload staging aborted.',
        };
      }
      return {
        ok: false,
        statusCode: 500,
        code: 'staging_io_error',
        message: err instanceof Error ? err.message : 'Staging I/O failed.',
      };
    } finally {
      this.activeStagedBytes = Math.max(0, this.activeStagedBytes - reservedBytes);
      try {
        await handle.close();
      } catch {
        // already closed
      }
      try {
        unlinkSync(tmpPath);
      } catch {
        // best-effort temp cleanup
      }
    }
  }
}
