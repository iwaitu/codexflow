import { utf8Decode, utf8Encode } from './remoteProtocol';

export interface SingleFileMultipartOptions {
  readonly fieldName?: string;
  readonly fileName: string;
  readonly contentType?: string;
  readonly fileBytes: Uint8Array;
  readonly boundary: string;
}

export interface SingleFileMultipartResult {
  readonly contentType: string;
  readonly bodyBytes: Uint8Array;
}

export interface ParsedSingleFileMultipart {
  readonly boundary: string;
  readonly fileName: string;
  readonly contentType: string;
  readonly fileBytes: Uint8Array;
}

export function sanitizeMultipartHeaderValue(raw: string, fallback: string): string {
  const cleaned = raw.replace(/[\r\n\0"\\]/g, '_').trim();
  return cleaned.length > 0 ? cleaned : fallback;
}

export function sanitizeMultipartFileName(raw: string): string {
  const base = raw.replace(/^.*[/\\]/, '');
  return sanitizeMultipartHeaderValue(base, 'attachment.bin');
}

export function buildSingleFileMultipart(
  options: SingleFileMultipartOptions,
): SingleFileMultipartResult {
  const fieldName = sanitizeMultipartHeaderValue(options.fieldName ?? 'file', 'file');
  const fileName = sanitizeMultipartFileName(options.fileName);
  const partContentType = sanitizeMultipartHeaderValue(
    options.contentType || 'application/octet-stream',
    'application/octet-stream',
  );
  const boundary = options.boundary.trim();
  if (!boundary || /[\r\n\0]/.test(boundary)) {
    throw new Error('Invalid multipart boundary.');
  }

  const head = utf8Encode(
    `--${boundary}\r\n` +
      `Content-Disposition: form-data; name="${fieldName}"; filename="${fileName}"\r\n` +
      `Content-Type: ${partContentType}\r\n` +
      `\r\n`,
  );
  const tail = utf8Encode(`\r\n--${boundary}--\r\n`);

  const bodyBytes = new Uint8Array(head.byteLength + options.fileBytes.byteLength + tail.byteLength);
  bodyBytes.set(head, 0);
  bodyBytes.set(options.fileBytes, head.byteLength);
  bodyBytes.set(tail, head.byteLength + options.fileBytes.byteLength);

  return {
    contentType: `multipart/form-data; boundary=${boundary}`,
    bodyBytes,
  };
}

function indexOfBytes(haystack: Uint8Array, needle: Uint8Array, startAt = 0): number {
  if (needle.byteLength === 0 || haystack.byteLength - startAt < needle.byteLength) {
    return -1;
  }
  outer: for (let i = startAt; i <= haystack.byteLength - needle.byteLength; i += 1) {
    for (let j = 0; j < needle.byteLength; j += 1) {
      if (haystack[i + j] !== needle[j]) {
        continue outer;
      }
    }
    return i;
  }
  return -1;
}

export function extractMultipartBoundary(contentTypeHeader: string): string {
  const match = /(?:^|;)\s*boundary=(?:"([^"]+)"|([^\s;]+))/i.exec(contentTypeHeader);
  const boundary = (match?.[1] ?? match?.[2] ?? '').trim();
  if (!boundary || boundary.length > 128 || /[\r\n\0]/.test(boundary)) {
    throw new Error('Missing or invalid multipart boundary.');
  }
  return boundary;
}

export function parseSingleFileMultipart(
  contentTypeHeader: string,
  bodyBytes: Uint8Array,
): ParsedSingleFileMultipart {
  if (!/^\s*multipart\/form-data\b/i.test(contentTypeHeader)) {
    throw new Error('Content-Type must be multipart/form-data.');
  }
  const boundary = extractMultipartBoundary(contentTypeHeader);
  const delim = utf8Encode(`--${boundary}`);
  const crlf = utf8Encode('\r\n');
  const headerEndMarker = utf8Encode('\r\n\r\n');

  const firstIdx = indexOfBytes(bodyBytes, delim, 0);
  if (firstIdx !== 0) {
    throw new Error('Multipart body does not start with expected boundary.');
  }

  let cursor = firstIdx + delim.byteLength;
  if (bodyBytes[cursor] !== crlf[0] || bodyBytes[cursor + 1] !== crlf[1]) {
    throw new Error('Missing CRLF after opening multipart boundary.');
  }
  cursor += 2;

  const headerEnd = indexOfBytes(bodyBytes, headerEndMarker, cursor);
  if (headerEnd < 0) {
    throw new Error('Missing header terminator in multipart part.');
  }

  const headerText = utf8Decode(bodyBytes.subarray(cursor, headerEnd));
  const lines = headerText.split('\r\n');
  let fieldName = '';
  let fileName = '';
  let partContentType = 'application/octet-stream';

  for (const line of lines) {
    const colon = line.indexOf(':');
    if (colon <= 0) {
      continue;
    }
    const key = line.slice(0, colon).trim().toLowerCase();
    const val = line.slice(colon + 1).trim();
    if (key === 'content-disposition') {
      const nameMatch = /\bname="([^"]*)"/i.exec(val);
      const fileMatch = /\bfilename="([^"]*)"/i.exec(val);
      fieldName = nameMatch?.[1] ?? '';
      fileName = fileMatch?.[1] ?? '';
    } else if (key === 'content-type') {
      partContentType = val || 'application/octet-stream';
    }
  }

  if (fieldName !== 'file') {
    throw new Error('Multipart upload must contain a single field named "file".');
  }
  if (!fileName) {
    throw new Error('Multipart file part is missing filename.');
  }

  const dataStart = headerEnd + headerEndMarker.byteLength;
  const closingBoundary = utf8Encode(`\r\n--${boundary}--`);
  const nextPartBoundary = utf8Encode(`\r\n--${boundary}\r\n`);

  if (indexOfBytes(bodyBytes, nextPartBoundary, dataStart) >= 0) {
    throw new Error('Multiple multipart parts are forbidden.');
  }

  const closeIdx = indexOfBytes(bodyBytes, closingBoundary, dataStart);
  if (closeIdx < 0) {
    throw new Error('Missing closing multipart boundary.');
  }

  // Only optional trailing CRLF allowed after closing boundary
  const trailingStart = closeIdx + closingBoundary.byteLength;
  const trailing = bodyBytes.subarray(trailingStart);
  if (
    trailing.byteLength !== 0 &&
    !(trailing.byteLength === 2 && trailing[0] === 0x0d && trailing[1] === 0x0a)
  ) {
    throw new Error('Unexpected trailing bytes after multipart closing boundary.');
  }

  return {
    boundary,
    fileName: sanitizeMultipartFileName(fileName),
    contentType: sanitizeMultipartHeaderValue(partContentType, 'application/octet-stream'),
    fileBytes: bodyBytes.slice(dataStart, closeIdx),
  };
}
