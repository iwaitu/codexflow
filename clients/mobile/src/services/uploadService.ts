import { bytesToHexLower, utf8Encode } from '@codexflow-shared/remoteProtocol';
import {
  sanitizeMultipartFileName,
  sanitizeMultipartHeaderValue,
} from '@codexflow-shared/multipartBuilder';
import type {
  RemoteAttachmentUploadStatusResponse,
  UploadedAttachmentDto,
} from '@/api/hostTypes';
import { sha256MultipartStream } from '@/crypto/incrementalSha256';
import { classifyRemoteError, RemoteApiException } from './errorClassifier';
import type { RelayClient } from './relayClient';

export const MAX_MOBILE_UPLOAD_BYTES = 20 * 1024 * 1024;

function randomBoundary(): string {
  const bytes = new Uint8Array(12);
  globalThis.crypto.getRandomValues(bytes);
  return `cfmb_${bytesToHexLower(bytes)}`;
}

function randomUploadId(): string {
  if (typeof globalThis.crypto.randomUUID === 'function') {
    return globalThis.crypto.randomUUID();
  }
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  return bytesToHexLower(bytes);
}

export async function uploadSessionAttachment(params: {
  relayClient: RelayClient;
  sessionId: string;
  file: File;
  signal?: AbortSignal;
  onProgressLabel?: (label: string) => void;
  retryDelayMs?: number;
}): Promise<{ uploadId: string; fileSha256: string; attachment: UploadedAttachmentDto }> {
  const { file, sessionId, relayClient } = params;
  if (file.size <= 0) {
    throw new RemoteApiException(
      classifyRemoteError('empty_attachment', 400, '不能上传空文件。'),
    );
  }
  if (file.size > MAX_MOBILE_UPLOAD_BYTES) {
    throw new RemoteApiException(
      classifyRemoteError('payload_too_large', 413, '移动端单附件大小上限为 20 MiB。'),
    );
  }

  const boundary = randomBoundary();
  const fieldName = sanitizeMultipartHeaderValue('file', 'file');
  const fileName = sanitizeMultipartFileName(file.name);
  const partContentType = sanitizeMultipartHeaderValue(
    file.type || 'application/octet-stream',
    'application/octet-stream',
  );

  const head = utf8Encode(
    `--${boundary}\r\n` +
      `Content-Disposition: form-data; name="${fieldName}"; filename="${fileName}"\r\n` +
      `Content-Type: ${partContentType}\r\n` +
      `\r\n`,
  );
  const tail = utf8Encode(`\r\n--${boundary}--\r\n`);

  params.onProgressLabel?.('正在流式计算 SHA-256 摘要...');
  const { fileSha256, bodySha256 } = await sha256MultipartStream(head, file, tail);

  const headBuf = new Uint8Array(head.byteLength);
  headBuf.set(head);
  const tailBuf = new Uint8Array(tail.byteLength);
  tailBuf.set(tail);
  const multipartBlob = new Blob([headBuf, file, tailBuf], {
    type: `multipart/form-data; boundary=${boundary}`,
  });

  const uploadId = randomUploadId();
  const uploadPath = `/api/gateway/session/${encodeURIComponent(sessionId)}/attachments`;
  const statusPath = `/api/gateway/session/${encodeURIComponent(sessionId)}/attachments/uploads/${encodeURIComponent(uploadId)}`;
  const retryDelay = params.retryDelayMs ?? 500;

  params.onProgressLabel?.('正在通过加密隧道上传附件...');
  let attempt = 0;
  while (attempt < 2) {
    attempt += 1;
    try {
      const uploaded = await relayClient.requestJson<UploadedAttachmentDto>({
        method: 'POST',
        path: uploadPath,
        keyPurpose: 'device_session',
        rawBody: multipartBlob,
        precomputedBodySha256: bodySha256,
        contentType: `multipart/form-data; boundary=${boundary}`,
        uploadId,
        fileSha256,
        signal: params.signal,
      });
      return { uploadId, fileSha256, attachment: uploaded };
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') {
        throw err;
      }
      if (err instanceof RemoteApiException && !err.appError.recoverable && err.appError.httpStatus < 500) {
        throw err;
      }

      // 弱网失败后查询服务端/宿主落盘状态防止重复上传或遗漏
      params.onProgressLabel?.('上传中断，正在查询幂等恢复状态...');
      for (let poll = 0; poll < 3; poll += 1) {
        try {
          const st = await relayClient.requestJson<RemoteAttachmentUploadStatusResponse>({
            method: 'GET',
            path: statusPath,
            queryPairs: [['fileSha256', fileSha256]],
            keyPurpose: 'device_session',
            uploadId,
            signal: params.signal,
          });
          if (st.status.toLowerCase() === 'completed' && st.attachment) {
            return { uploadId, fileSha256, attachment: st.attachment };
          }
          if (st.status.toLowerCase() === 'pending') {
            await new Promise((r) => setTimeout(r, retryDelay));
            continue;
          }
          break;
        } catch (statusErr) {
          if (statusErr instanceof RemoteApiException) {
            if (statusErr.appError.httpStatus === 409) {
              throw statusErr;
            }
            if (statusErr.appError.httpStatus === 404) {
              break;
            }
          }
          throw statusErr;
        }
      }

      if (attempt >= 2) {
        throw err;
      }
      await new Promise((r) => setTimeout(r, retryDelay));
    }
  }

  throw new RemoteApiException(
    classifyRemoteError('upload_failed', 503, '附件上传超时或未完成。'),
  );
}
