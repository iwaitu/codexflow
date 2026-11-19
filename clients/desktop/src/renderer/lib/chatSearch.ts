import { parseMessageWithAttachments } from './attachments';
import type { UiMessage } from './sessionRecovery';

export interface ChatSearchMatch {
  messageIndex: number;
  messageId: string;
  field: 'content' | 'thinking';
  occurrenceInField: number;
}

export interface HighlightSegment {
  text: string;
  /** Non-negative occurrence index within the field when matched; -1 for plain text. */
  matchIndex: number;
}

const MATCH_HIGHLIGHT_KEY = 'chat-search-match';
const ACTIVE_HIGHLIGHT_KEY = 'chat-search-active';

function getSearchableContent(message: UiMessage): string {
  if (message.role === 'user') {
    return parseMessageWithAttachments(message.content).text;
  }
  return message.content ?? '';
}

function collectOccurrences(source: string, lowerQuery: string): number[] {
  if (!source || !lowerQuery) return [];
  const lowerSource = source.toLowerCase();
  const positions: number[] = [];
  let cursor = 0;
  const step = lowerQuery.length;
  while (cursor <= lowerSource.length - step) {
    const idx = lowerSource.indexOf(lowerQuery, cursor);
    if (idx === -1) break;
    positions.push(idx);
    cursor = idx + step;
  }
  return positions;
}

export function findSessionSearchMatches(messages: UiMessage[], rawQuery: string): ChatSearchMatch[] {
  const trimmed = rawQuery.trim();
  if (!trimmed) return [];
  const lowerQuery = trimmed.toLowerCase();
  const matches: ChatSearchMatch[] = [];

  for (let i = 0; i < messages.length; i += 1) {
    const msg = messages[i]!;
    if (msg.role !== 'user' && msg.thinkingContent) {
      const thinkingHits = collectOccurrences(msg.thinkingContent, lowerQuery);
      for (let occ = 0; occ < thinkingHits.length; occ += 1) {
        matches.push({
          messageIndex: i,
          messageId: msg.id,
          field: 'thinking',
          occurrenceInField: occ,
        });
      }
    }

    const contentText = getSearchableContent(msg);
    const contentHits = collectOccurrences(contentText, lowerQuery);
    for (let occ = 0; occ < contentHits.length; occ += 1) {
      matches.push({
        messageIndex: i,
        messageId: msg.id,
        field: 'content',
        occurrenceInField: occ,
      });
    }
  }

  return matches;
}

export function splitHighlightSegments(text: string, rawQuery: string): HighlightSegment[] {
  const trimmed = rawQuery.trim();
  if (!text || !trimmed) {
    return text ? [{ text, matchIndex: -1 }] : [];
  }

  const lowerQuery = trimmed.toLowerCase();
  const positions = collectOccurrences(text, lowerQuery);
  if (positions.length === 0) {
    return [{ text, matchIndex: -1 }];
  }

  const segments: HighlightSegment[] = [];
  let cursor = 0;
  const len = trimmed.length;
  for (let occ = 0; occ < positions.length; occ += 1) {
    const start = positions[occ]!;
    if (start > cursor) {
      segments.push({ text: text.slice(cursor, start), matchIndex: -1 });
    }
    segments.push({ text: text.slice(start, start + len), matchIndex: occ });
    cursor = start + len;
  }
  if (cursor < text.length) {
    segments.push({ text: text.slice(cursor), matchIndex: -1 });
  }
  return segments;
}

type CssHighlightRegistryLike = {
  set: (name: string, highlight: unknown) => void;
  delete: (name: string) => void;
};

function getHighlightRegistry(): CssHighlightRegistryLike | null {
  if (typeof globalThis === 'undefined') return null;
  const cssObj = (globalThis as { CSS?: { highlights?: CssHighlightRegistryLike } }).CSS;
  const HighlightCtor = (globalThis as { Highlight?: new (...ranges: Range[]) => unknown }).Highlight;
  if (!cssObj?.highlights || typeof HighlightCtor !== 'function') {
    return null;
  }
  return cssObj.highlights;
}

export function clearCssHighlightsForSearch(): void {
  const registry = getHighlightRegistry();
  if (!registry) return;
  registry.delete(MATCH_HIGHLIGHT_KEY);
  registry.delete(ACTIVE_HIGHLIGHT_KEY);
}

/**
 * Uses the Chromium CSS Custom Highlight API to highlight matching text inside rendered
 * Markdown/Shiki subtrees without mutating React-managed DOM nodes.
 */
export function applyCssHighlightsForSearch(
  container: HTMLElement | null,
  rawQuery: string,
  activeMessageId: string | null,
  activeField: 'content' | 'thinking' | null,
  activeOccurrenceInField: number,
): void {
  const registry = getHighlightRegistry();
  if (!registry) return;

  const trimmed = rawQuery.trim();
  if (!container || !trimmed) {
    clearCssHighlightsForSearch();
    return;
  }

  const HighlightCtor = (globalThis as unknown as { Highlight: new (...ranges: Range[]) => unknown }).Highlight;
  const lowerQuery = trimmed.toLowerCase();
  const qLen = lowerQuery.length;

  const matchRanges: Range[] = [];
  const activeRanges: Range[] = [];

  const markdownHosts = container.querySelectorAll<HTMLElement>('[data-search-markdown-id]');
  for (const host of Array.from(markdownHosts)) {
    const msgId = host.getAttribute('data-search-markdown-id');
    const isActiveHost = Boolean(activeMessageId && msgId === activeMessageId && activeField === 'content');

    const walker = document.createTreeWalker(host, NodeFilter.SHOW_TEXT);
    const textNodes: Text[] = [];
    let current = walker.nextNode();
    while (current) {
      if (current.nodeType === Node.TEXT_NODE && current.nodeValue) {
        textNodes.push(current as Text);
      }
      current = walker.nextNode();
    }

    let occurrenceCounter = 0;
    for (const node of textNodes) {
      const value = node.nodeValue ?? '';
      const lower = value.toLowerCase();
      let cursor = 0;
      while (cursor <= lower.length - qLen) {
        const idx = lower.indexOf(lowerQuery, cursor);
        if (idx === -1) break;
        try {
          const range = document.createRange();
          range.setStart(node, idx);
          range.setEnd(node, idx + qLen);
          if (isActiveHost && occurrenceCounter === activeOccurrenceInField) {
            activeRanges.push(range);
          } else {
            matchRanges.push(range);
          }
        } catch {
          // Ignore transient range errors if DOM changed mid-frame.
        }
        occurrenceCounter += 1;
        cursor = idx + qLen;
      }
    }
  }

  if (matchRanges.length > 0) {
    registry.set(MATCH_HIGHLIGHT_KEY, new HighlightCtor(...matchRanges));
  } else {
    registry.delete(MATCH_HIGHLIGHT_KEY);
  }

  if (activeRanges.length > 0) {
    registry.set(ACTIVE_HIGHLIGHT_KEY, new HighlightCtor(...activeRanges));
  } else {
    registry.delete(ACTIVE_HIGHLIGHT_KEY);
  }
}
