import React from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ChatView } from '../../src/renderer/components/ChatView';
import { findSessionSearchMatches, splitHighlightSegments } from '../../src/renderer/lib/chatSearch';
import type { UiMessage } from '../../src/renderer/lib/sessionRecovery';

vi.mock('@tanstack/react-virtual', () => ({
  useVirtualizer: ({ count }: { count: number }) => ({
    getTotalSize: () => count * 120,
    getVirtualItems: () =>
      Array.from({ length: count }, (_, index) => ({
        index,
        key: index,
        start: index * 120,
        size: 120,
      })),
    measureElement: () => {},
    scrollToIndex: vi.fn(),
  }),
}));

describe('In-conversation search (Ctrl+F)', () => {
  const sampleMessages: UiMessage[] = [
    {
      id: 'm1',
      role: 'user',
      content: '请帮我检查 Compact 接口的实现',
    },
    {
      id: 'm2',
      role: 'assistant',
      content: '好的，已经定位到 compact 接口和压缩逻辑。',
      thinkingContent: '先检查 Compact 在 CodexSessionManager 中的调用链',
    },
    {
      id: 'm3',
      role: 'system',
      content: '系统提示：上下文压缩已完成',
    },
  ];

  it('findSessionSearchMatches finds case-insensitive hits across user, assistant, thinking, and system messages', () => {
    const matches = findSessionSearchMatches(sampleMessages, 'compact');
    expect(matches).toEqual([
      { messageIndex: 0, messageId: 'm1', field: 'content', occurrenceInField: 0 },
      { messageIndex: 1, messageId: 'm2', field: 'thinking', occurrenceInField: 0 },
      { messageIndex: 1, messageId: 'm2', field: 'content', occurrenceInField: 0 },
    ]);

    expect(findSessionSearchMatches(sampleMessages, '   ')).toEqual([]);
  });

  it('splitHighlightSegments splits matched and unmatched spans accurately', () => {
    const segments = splitHighlightSegments('Alpha beta ALPHA', 'alpha');
    expect(segments).toEqual([
      { text: 'Alpha', matchIndex: 0 },
      { text: ' beta ', matchIndex: -1 },
      { text: 'ALPHA', matchIndex: 1 },
    ]);
  });

  it('ChatView search bar supports match counting, Enter/Shift+Enter cycling, thinking auto-expand, and Escape close', () => {
    const onCloseSearch = vi.fn();

    render(
      <ChatView
        sessionId="s-1"
        conversationId="c-1"
        messages={sampleMessages}
        streamingContent=""
        streamingThinking=""
        isThinkingStreaming={false}
        isTurnRunning={false}
        toolCalls={[]}
        pendingApprovals={[]}
        currentPlan={null}
        jobs={[]}
        verificationReceipts={{}}
        workerBar={{ status: 'idle', message: '' }}
        executionBackend="Local"
        controlPlaneAvailable
        onApprovalResolved={vi.fn()}
        onPlanActionSubmitted={vi.fn()}
        onVerifyJob={vi.fn().mockResolvedValue(undefined)}
        onSwitchManaged={vi.fn()}
        searchOpen
        searchFocusNonce={1}
        onCloseSearch={onCloseSearch}
      />,
    );

    const searchInput = screen.getByRole('textbox', { name: '在当前会话中搜索' });
    expect(searchInput).toBeInTheDocument();

    fireEvent.change(searchInput, { target: { value: 'compact' } });
    expect(screen.getByTestId('chat-search-count')).toHaveTextContent('1 / 3');

    // Next match hits assistant thinkingContent -> automatically expands thinking panel
    fireEvent.keyDown(searchInput, { key: 'Enter' });
    expect(screen.getByTestId('chat-search-count')).toHaveTextContent('2 / 3');
    expect(screen.getByText(/CodexSessionManager 中的调用链/)).toBeInTheDocument();

    // Next match -> 3 / 3
    fireEvent.keyDown(searchInput, { key: 'Enter' });
    expect(screen.getByTestId('chat-search-count')).toHaveTextContent('3 / 3');

    // Shift+Enter goes back -> 2 / 3
    fireEvent.keyDown(searchInput, { key: 'Enter', shiftKey: true });
    expect(screen.getByTestId('chat-search-count')).toHaveTextContent('2 / 3');

    // Non-matching query displays 0 / 0
    fireEvent.change(searchInput, { target: { value: '不存在的关键词' } });
    expect(screen.getByTestId('chat-search-count')).toHaveTextContent('0 / 0');

    // Escape closes search bar
    fireEvent.keyDown(searchInput, { key: 'Escape' });
    expect(onCloseSearch).toHaveBeenCalledTimes(1);
  });
});
