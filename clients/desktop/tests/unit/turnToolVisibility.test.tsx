import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { ChatView } from '@renderer/components/ChatView';
import type { UiToolCall } from '@renderer/lib/sessionRecovery';
import { useAppStore } from '@renderer/stores/useAppStore';

function createSampleToolCalls(): UiToolCall[] {
  return [
    {
      key: 'call-1',
      callId: 'call-1',
      name: 'ivilson_read',
      status: 'completed',
      outputStdout: '',
      outputStderr: '',
      outputTruncated: false,
      suppressedLines: 0,
      result: 'ok',
    },
    {
      key: 'call-2',
      callId: 'call-2',
      name: 'run_shell',
      status: 'completed',
      outputStdout: 'Python 3.12.10',
      outputStderr: '',
      outputTruncated: false,
      suppressedLines: 0,
      result: '0',
    },
  ];
}

describe('turn tool visibility lifecycle', () => {
  beforeEach(() => {
    useAppStore.getState().setActiveSession('sess-tool-test');
  });

  it('tracks tool calls while turn is running and clears them on SSE done', () => {
    const store = useAppStore.getState();

    store.applySseEvent({
      id: 1,
      event: 'tool_call',
      data: { callId: 'c1', name: 'ivilson_read', arguments: { path: 'a.py' } },
    });
    expect(useAppStore.getState().isTurnRunning).toBe(true);
    expect(useAppStore.getState().toolCalls).toHaveLength(1);

    store.applySseEvent({
      id: 2,
      event: 'tool_started',
      data: { callId: 'c1', name: 'ivilson_read' },
    });
    expect(useAppStore.getState().toolCalls[0]?.status).toBe('running');

    store.applySseEvent({
      id: 3,
      event: 'task_status',
      data: { callId: 'c1', name: 'ivilson_read', success: true, result: 'done' },
    });
    expect(useAppStore.getState().toolCalls[0]?.status).toBe('completed');

    store.applySseEvent({
      id: 4,
      event: 'content',
      data: { content: '实测完成' },
    });
    expect(useAppStore.getState().toolCalls).toHaveLength(1);

    store.applySseEvent({
      id: 5,
      event: 'done',
      data: { source: 'UserChat' },
    });

    const afterDone = useAppStore.getState();
    expect(afterDone.isTurnRunning).toBe(false);
    expect(afterDone.toolCalls).toEqual([]);
    expect(afterDone.messages.some((m) => m.role === 'assistant' && m.content === '实测完成')).toBe(true);
  });

  it('clears tool calls when SSE error terminates a turn', () => {
    const store = useAppStore.getState();

    store.applySseEvent({
      id: 10,
      event: 'tool_started',
      data: { callId: 'c-err', name: 'run_shell' },
    });
    expect(useAppStore.getState().isTurnRunning).toBe(true);
    expect(useAppStore.getState().toolCalls).toHaveLength(1);

    store.applySseEvent({
      id: 11,
      event: 'error',
      data: { message: '模型调用失败' },
    });

    const afterError = useAppStore.getState();
    expect(afterError.isTurnRunning).toBe(false);
    expect(afterError.toolCalls).toEqual([]);
  });

  it('renders ToolCallCard only while isTurnRunning is true and hides stale snapshot tool calls after turn finishes', () => {
    const toolCalls = createSampleToolCalls();
    const baseProps = {
      sessionId: 'sess-tool-test',
      conversationId: 'conv-1',
      messages: [
        {
          id: 'm1',
          role: 'assistant' as const,
          content: '已完成实测',
        },
      ],
      messagesTruncated: false,
      streamingContent: '',
      streamingThinking: '',
      isThinkingStreaming: false,
      toolCalls,
      pendingApprovals: [],
      currentPlan: null,
      jobs: [],
      verificationReceipts: {},
      workerBar: { status: 'idle' as const, message: null },
      executionBackend: 'Local',
      controlPlaneAvailable: true,
      onApprovalResolved: vi.fn(),
      onPlanActionSubmitted: vi.fn(),
      onVerifyJob: vi.fn(),
      onSwitchManaged: vi.fn(),
    };

    const { rerender } = render(<ChatView {...baseProps} isTurnRunning={true} />);
    expect(screen.getByText('ivilson_read')).toBeInTheDocument();
    expect(screen.getByText('run_shell')).toBeInTheDocument();

    rerender(<ChatView {...baseProps} isTurnRunning={false} />);
    expect(screen.queryByText('ivilson_read')).not.toBeInTheDocument();
    expect(screen.queryByText('run_shell')).not.toBeInTheDocument();
  });
});
