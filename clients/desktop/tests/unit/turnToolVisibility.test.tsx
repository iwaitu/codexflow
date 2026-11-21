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
    const answer = afterDone.messages.find((m) => m.role === 'assistant' && m.content === '实测完成');
    expect(answer).toBeDefined();
    expect(answer).not.toHaveProperty('toolHistory');
  });

  it('keeps the turn error notice without creating a tool history message', () => {
    const store = useAppStore.getState();

    store.applySseEvent({
      id: 9,
      event: 'tool_started',
      data: { callId: 'c-err', name: 'run_shell' },
    });
    store.applySseEvent({
      id: 10,
      event: 'task_status',
      data: { type: 'tool_result', callId: 'c-err', name: 'run_shell', success: false, result: 'process failed' },
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
    expect(afterError.messages).toEqual([]);
    expect(afterError.turnNotice).toBe('模型调用失败');
  });

  it('clears cancelled tools without creating a tool history message', () => {
    const store = useAppStore.getState();
    store.applySseEvent({
      id: 20, event: 'tool_started', data: { callId: 'c-cancel', name: 'computer_act' },
    });
    store.applySseEvent({ id: 21, event: 'done', data: { source: 'cancelled' } });
    const state = useAppStore.getState();
    expect(state.toolCalls).toEqual([]);
    expect(state.messages).toEqual([]);
    expect(state.turnNotice).toBe('本回合已取消。');
  });

  it('renders only pending and running tools and removes them as soon as they finish', () => {
    const toolCalls: UiToolCall[] = [
      ...createSampleToolCalls(),
      { ...createSampleToolCalls()[0]!, key: 'in-flight', name: 'computer_act', status: 'running' },
      { ...createSampleToolCalls()[0]!, key: 'asked', name: 'computer_screen_info', status: 'called' },
      { ...createSampleToolCalls()[0]!, key: 'failed', name: 'computer_screenshot', status: 'failed', outputStderr: 'capture failed' },
    ];
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
    expect(screen.queryByText('ivilson_read')).not.toBeInTheDocument();
    expect(screen.queryByText('run_shell')).not.toBeInTheDocument();
    expect(screen.getByText('computer_act')).toBeVisible();
    expect(screen.getByText('computer_screen_info')).toBeVisible();
    expect(screen.queryByText('computer_screenshot')).not.toBeInTheDocument();
    expect(screen.queryByText('capture failed')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /工具历史/ })).not.toBeInTheDocument();

    const finished = toolCalls.map((tool) => ({ ...tool, status: 'completed' as const }));
    rerender(<ChatView {...baseProps} toolCalls={finished} isTurnRunning={true} />);
    expect(screen.queryByText('computer_act')).not.toBeInTheDocument();
    expect(screen.queryByText('computer_screen_info')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /工具历史/ })).not.toBeInTheDocument();

    rerender(<ChatView {...baseProps} isTurnRunning={false} />);
    expect(screen.queryByText('ivilson_read')).not.toBeInTheDocument();
    expect(screen.queryByText('computer_act')).not.toBeInTheDocument();
  });
});
