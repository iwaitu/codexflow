import { describe, expect, it, vi } from 'vitest';
import type { SessionSnapshot } from '../../src/renderer/lib/apiClient';
import { apiClient } from '../../src/renderer/lib/apiClient';
import { parseSseFrames, SessionSseClient, SseStreamParser } from '../../src/renderer/lib/sseClient';
import { useAppStore } from '../../src/renderer/stores/useAppStore';

function makeSseStream(chunks: string[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) {
        controller.enqueue(encoder.encode(chunk));
      }
      controller.close();
    },
  });
}

function makeBaseSnapshot(overrides: Partial<SessionSnapshot> = {}): SessionSnapshot {
  return {
    sessionId: 'sess-1',
    asOfEventId: 42,
    channelEpoch: 'epoch-2',
    activeConversationId: 'conv-1',
    activeTurn: {
      isRunning: false,
      messageId: null,
      conversationId: 'conv-1',
      partialContent: null,
      partialThinking: null,
      contentTruncated: false,
    },
    session: {
      sessionId: 'sess-1',
      name: '默认工作区',
      workspacePath: 'D:/repo',
      currentStage: 1,
      isArchived: false,
      isExternalWorkspace: false,
      createdUtc: '2026-11-16T00:00:00Z',
      currentPlanArtifactId: null,
      activeTaskId: null,
    },
    messages: [
      {
        id: 'm1',
        role: 'assistant',
        content: '已恢复的消息内容',
        thinkingContent: null,
        messageType: null,
        notificationMeta: null,
        toolCallResult: null,
        truncated: true,
        createdUtc: '2026-11-16T00:00:01Z',
      },
    ],
    pendingApprovals: [
      {
        approvalId: 'appr-rec-1',
        requestDigest: 'digest-rec-1',
        sessionId: 'sess-1',
        tool: 'run_command',
        executable: 'C:/Program Files/dotnet/dotnet.exe',
        interpreterChain: [],
        argv: ['dotnet', 'test'],
        cwd: 'D:/repo',
        stdinSha256: null,
        codeSha256: null,
        scriptSha256: null,
        inputsManifestSha256: null,
        backend: 'Local',
        network: 'Host',
        filesystem: 'unrestricted',
        reason: 'Ask mode requires approval',
        createdAtUtc: '2026-11-16T00:00:00Z',
        expiresAtUtc: '2026-11-16T00:10:00Z',
        allowedDecisions: ['allow_once', 'deny'],
        executesWorkspaceCode: true,
        workspaceCodeWarning: '会执行项目代码',
      },
    ],
    currentPlan: {
      planArtifactId: 'plan-1',
      version: '3',
      markdownHash: 'hash-plan-3',
      status: 'ReadyForApproval',
      goal: 'Fix bug',
      markdown: '# 修复方案\n- 修改计算逻辑',
      tasks: [
        {
          id: 'T1',
          title: 'Fix bug',
          description: 'Fix it',
          taskType: 'Forge',
          status: 'Pending',
          dependencies: [],
          resultNotes: null,
        },
      ],
      hostRefusal: null,
      updatedAtUtc: '2026-11-16T00:00:02Z',
    },
    taskListJson: null,
    activeToolResults: [
      {
        callId: 'call-1',
        toolName: 'exec_cmd',
        success: true,
        result: 'truncated result',
        truncated: true,
      },
    ],
    activeToolOutputs: [
      {
        tool: 'exec_cmd',
        commandTaskId: 'cmd-1',
        stdout: 'line1\n',
        stderr: null,
        throttled: true,
        suppressedLines: 12,
      },
    ],
    jobs: [],
    controlPlaneAvailable: true,
    selfApprovalProtection: true,
    toolResultsTruncated: true,
    toolOutputsTruncated: true,
    ...overrides,
  };
}

describe('SSE parser and recovery scenarios', () => {
  it('parses chunked SSE frames and tolerates unknown events/fields', () => {
    const raw =
      'id: 1\nevent: future_unknown_event\ndata: {"extraField":true}\n\n' +
      'id: 2\nevent: content\ndata: {"type":"content","content":"hi","text":"hi","extra":1}\n\n' +
      'id: 3\nevent: content\ndata: {"type"';
    const { frames, rest } = parseSseFrames(raw);
    expect(frames).toHaveLength(2);
    expect(frames[0]?.event).toBe('future_unknown_event');
    expect(frames[1]?.event).toBe('content');
    expect(rest).toBe('id: 3\nevent: content\ndata: {"type"');
  });

  it('deduplicates replayed events within replay buffer on short disconnect', async () => {
    const seen: Array<{ id?: number; event: string }> = [];
    const reconcileReasons: string[] = [];
    let callCount = 0;

    const fakeFetch: typeof fetch = async (input, init) => {
      callCount += 1;
      const url = String(input);
      if (callCount === 1) {
        return new Response(
          makeSseStream([
            'id: 1\nevent: content\ndata: {"type":"content","content":"A","text":"A"}\n\n',
            'id: 2\nevent: content\ndata: {"type":"content","content":"B","text":"B"}\n\n',
          ]),
          { status: 200 },
        );
      }
      expect(url).toContain('lastEventId=2');
      expect((init?.headers as Record<string, string>)['Last-Event-ID']).toBe('2');
      return new Response(
        makeSseStream([
          // Server replays id 2 + sends new id 3
          'id: 2\nevent: content\ndata: {"type":"content","content":"B","text":"B"}\n\n',
          'id: 3\nevent: content\ndata: {"type":"content","content":"C","text":"C"}\n\n',
        ]),
        { status: 200 },
      );
    };

    const client = new SessionSseClient({
      baseUrl: 'http://127.0.0.1:9999',
      accessToken: 'tok',
      sessionId: 's1',
      fetchImpl: fakeFetch,
      onEvent: (evt) => {
        seen.push({ id: evt.id, event: evt.event });
        if (evt.id === 3) {
          client.stop();
        }
      },
      onReconcileRequired: (reason) => reconcileReasons.push(reason),
    });

    client.start();
    await vi.waitFor(() => expect(seen.some((s) => s.id === 3)).toBe(true), { timeout: 4000 });
    client.stop();

    expect(seen.map((s) => s.id)).toEqual([1, 2, 3]);
    expect(reconcileReasons).toContain('reconnected');
  });

  it('triggers reconcile on replay_gap (buffer_evicted & subscriber_overflow) and epoch change', async () => {
    const reasons: string[] = [];
    let clientRef: SessionSseClient | null = null;

    const fakeFetch: typeof fetch = async () =>
      new Response(
        makeSseStream([
          'id: 10\nevent: heartbeat\ndata: {"sessionId":"s1","status":"connected","subscriberCount":1,"epoch":"epoch-1","pendingApprovals":0}\n\n',
          'id: 11\nevent: replay_gap\ndata: {"reason":"buffer_evicted","epoch":"epoch-1"}\n\n',
          'id: 12\nevent: replay_gap\ndata: {"reason":"subscriber_overflow","epoch":"epoch-1"}\n\n',
          'id: 1\nevent: heartbeat\ndata: {"sessionId":"s1","status":"connected","subscriberCount":1,"epoch":"epoch-2","pendingApprovals":1}\n\n',
        ]),
        { status: 200 },
      );

    const client = new SessionSseClient({
      baseUrl: 'http://127.0.0.1:9999',
      accessToken: 'tok',
      sessionId: 's1',
      fetchImpl: fakeFetch,
      onEvent: (evt) => {
        if (evt.id === 1 && (evt.data as { epoch?: string }).epoch === 'epoch-2') {
          clientRef?.stop();
        }
      },
      onReconcileRequired: (reason) => reasons.push(reason),
    });
    clientRef = client;

    client.start();
    await vi.waitFor(() => expect(reasons).toContain('epoch_changed'), { timeout: 4000 });
    client.stop();

    expect(reasons.filter((r) => r === 'replay_gap')).toHaveLength(2);
    expect(reasons).toContain('epoch_changed');
  });

  it('restores messages, approvals, plan, tool results/outputs and truncated flags from snapshot', () => {
    const snapshot = makeBaseSnapshot();
    useAppStore.getState().setActiveSession('sess-1');
    useAppStore.getState().applySnapshot(snapshot);

    const state = useAppStore.getState();
    expect(state.asOfEventId).toBe(42);
    expect(state.channelEpoch).toBe('epoch-2');
    expect(state.messages).toHaveLength(1);
    expect(state.messages[0]?.truncated).toBe(true);
    expect(state.pendingApprovals).toHaveLength(1);
    expect(state.pendingApprovals[0]?.approvalId).toBe('appr-rec-1');
    expect(state.currentPlan?.planArtifactId).toBe('plan-1');
    expect(state.currentPlan?.version).toBe('3');
    expect(state.toolCalls).toHaveLength(2);
    expect(state.toolCalls[0]?.outputTruncated).toBe(true);
    expect(state.toolCalls[1]?.suppressedLines).toBe(12);
    expect(state.toolCalls[1]?.outputTruncated).toBe(true);
  });

  it('B9: handles CRLF split cleanly across 1-byte boundaries without losing frame id or fields', () => {
    const raw =
      'id: 7\r\nevent: content\r\ndata: {"content":"A"}\r\n\r\n' +
      'id: 8\r\nevent: done\r\nretry: 1200\r\ndata: {"source":"UserChat"}\r\n\r\n';
    const parser = new SseStreamParser();
    const collected: ReturnType<SseStreamParser['push']> = [];
    for (let i = 0; i < raw.length; i += 1) {
      collected.push(...parser.push(raw.slice(i, i + 1)));
    }
    collected.push(...parser.flush());

    expect(collected).toHaveLength(2);
    expect(collected[0]).toEqual({ id: 7, retry: undefined, event: 'content', data: '{"content":"A"}' });
    expect(collected[1]).toEqual({ id: 8, retry: 1200, event: 'done', data: '{"source":"UserChat"}' });

    // R-B9 extension: standalone retry frame & immediate CR-only dispatch without waiting for EOF/next char
    const crParser = new SseStreamParser();
    expect(crParser.push('retry: 2500\r\r')).toEqual([]);
    expect(crParser.consumeRetryMs()).toBe(2500);
    const immediateFrames = crParser.push('event: ping\rdata: {"ok":true}\r\r');
    expect(immediateFrames).toEqual([{ id: undefined, retry: undefined, event: 'ping', data: '{"ok":true}' }]);
  });

  it('R1: switching standalone backend endpoints clears session state and generation even on same host with different ports', () => {
    const store = useAppStore.getState();
    store.setConnection({
      mode: 'standalone',
      status: 'ready',
      baseUrl: 'http://127.0.0.1:61000',
      targetBaseUrl: 'http://127.0.0.1:5001',
      accessToken: 'tok-1',
      instanceId: 'standalone:http://127.0.0.1:5001',
      controlPlaneAvailable: false,
      selfApprovalProtection: true,
      dataDirectory: null,
      lastError: null,
      restartCount: 0,
    });
    store.setActiveSession('sess-port-5001');
    const prevGen = useAppStore.getState().sessionGeneration;
    store.appendOptimisticUserMessage('旧实例消息');
    expect(useAppStore.getState().activeSessionId).toBe('sess-port-5001');

    // Switch to standalone on port 5002
    store.setConnection({
      mode: 'standalone',
      status: 'ready',
      baseUrl: 'http://127.0.0.1:61000',
      targetBaseUrl: 'http://127.0.0.1:5002',
      accessToken: 'tok-2',
      instanceId: 'standalone:http://127.0.0.1:5002',
      controlPlaneAvailable: false,
      selfApprovalProtection: true,
      dataDirectory: null,
      lastError: null,
      restartCount: 0,
    });

    const next = useAppStore.getState();
    expect(next.activeSessionId).toBeNull();
    expect(next.messages).toHaveLength(0);
    expect(next.sessionGeneration).toBeGreaterThan(prevGen);
  });

  it('R2: overlapping reconcileSession inherits buffered SSE events when A is aborted by B and B later fails', async () => {
    const spy = vi.spyOn(apiClient, 'getSnapshot');
    const cwSpy = vi.spyOn(apiClient, 'getContextWindow').mockRejectedValue(new Error('skip'));

    let rejectA!: (err: Error) => void;
    let rejectB!: (err: Error) => void;
    spy.mockImplementationOnce(
      () =>
        new Promise((_res, rej) => {
          rejectA = rej;
        }),
    );
    spy.mockImplementationOnce(
      () =>
        new Promise((_res, rej) => {
          rejectB = rej;
        }),
    );

    try {
      const store = useAppStore.getState();
      store.setConnection({
        mode: 'managed',
        status: 'ready',
        baseUrl: 'http://127.0.0.1:61000',
        targetBaseUrl: 'http://127.0.0.1:5001',
        accessToken: 'tok',
        instanceId: 'inst-r2',
        controlPlaneAvailable: true,
        selfApprovalProtection: true,
        dataDirectory: null,
        lastError: null,
        restartCount: 0,
      });
      store.setActiveSession('sess-r2');

      const promiseA = store.reconcileSession('A');
      store.applySseEvent({ id: 11, event: 'content', data: { content: '关键事件11' } } as never);

      // Start overlapping B before A finishes -> must NOT drop event 11
      const promiseB = store.reconcileSession('B');
      rejectA(new Error('aborted'));
      await promiseA;
      rejectB(new Error('network down'));
      await promiseB;

      expect(useAppStore.getState().streamingContent).toBe('关键事件11');
      expect(useAppStore.getState().asOfEventId).toBe(11);
      expect(useAppStore.getState().isReconcilingSnapshot).toBe(false);
    } finally {
      spy.mockRestore();
      cwSpy.mockRestore();
    }
  });

  it('B6: buffers SSE events during snapshot reconcile, replays > asOfEventId, and drops stale session switches', async () => {
    let resolveSnapA!: (snap: SessionSnapshot) => void;
    const snapAPromise = new Promise<SessionSnapshot>((res) => {
      resolveSnapA = res;
    });

    const spy = vi.spyOn(apiClient, 'getSnapshot').mockImplementation((_auth, sid) => {
      if (sid === 'sess-A') return snapAPromise;
      return Promise.resolve(
        makeBaseSnapshot({
          sessionId: sid,
          asOfEventId: 100,
          messages: [
            {
              id: 'mB',
              role: 'assistant',
              content: '会话B消息',
              thinkingContent: null,
              messageType: null,
              notificationMeta: null,
              toolCallResult: null,
              truncated: false,
              createdUtc: '2026-11-16T00:00:00Z',
            },
          ],
        }),
      );
    });
    const cwSpy = vi.spyOn(apiClient, 'getContextWindow').mockRejectedValue(new Error('skip'));

    try {
      const store = useAppStore.getState();
      store.setConnection({
        mode: 'managed',
        status: 'ready',
        baseUrl: 'http://127.0.0.1:5000',
        targetBaseUrl: 'http://127.0.0.1:5000',
        accessToken: 'tok',
        instanceId: 'inst-b6',
        controlPlaneAvailable: true,
        selfApprovalProtection: true,
        dataDirectory: null,
        lastError: null,
        restartCount: 0,
      });

      // Case 1: Reconcile sess-A while SSE emits id:10 (stale) and id:11 (newer than snapshot asOfEventId=10)
      store.setActiveSession('sess-A');
      const recA = store.reconcileSession('test');

      store.applySseEvent({
        id: 10,
        event: 'content',
        data: { content: 'OLD_10' },
      } as never);
      store.applySseEvent({
        id: 11,
        event: 'content',
        data: { content: 'NEW_11' },
      } as never);

      resolveSnapA(
        makeBaseSnapshot({
          sessionId: 'sess-A',
          asOfEventId: 10,
          channelEpoch: 'epoch-2',
          messages: [
            {
              id: 'mA',
              role: 'assistant',
              content: '快照A内容',
              thinkingContent: null,
              messageType: null,
              notificationMeta: null,
              toolCallResult: null,
              truncated: false,
              createdUtc: '2026-11-16T00:00:00Z',
            },
          ],
        }),
      );
      await recA;

      expect(useAppStore.getState().asOfEventId).toBe(11);
      expect(useAppStore.getState().streamingContent).toBe('NEW_11');
      expect(useAppStore.getState().messages[0]?.content).toBe('快照A内容');

      // Case 2: Switch from sess-A2 to sess-B while A2 snapshot is still in flight -> delayed A2 must NOT overwrite B
      let resolveSlowA2!: (snap: SessionSnapshot) => void;
      spy.mockImplementationOnce(
        () =>
          new Promise<SessionSnapshot>((res) => {
            resolveSlowA2 = res;
          }),
      );
      spy.mockImplementationOnce(() =>
        Promise.resolve(
          makeBaseSnapshot({
            sessionId: 'sess-B',
            asOfEventId: 50,
            messages: [
              {
                id: 'mB',
                role: 'assistant',
                content: '会话B最新快照',
                thinkingContent: null,
                messageType: null,
                notificationMeta: null,
                toolCallResult: null,
                truncated: false,
                createdUtc: '2026-11-16T00:00:00Z',
              },
            ],
          }),
        ),
      );

      useAppStore.getState().setActiveSession('sess-A2');
      const slowA2Promise = useAppStore.getState().reconcileSession('slowA2');

      useAppStore.getState().setActiveSession('sess-B');
      await useAppStore.getState().reconcileSession('selectB');
      expect(useAppStore.getState().messages[0]?.content).toBe('会话B最新快照');

      resolveSlowA2(
        makeBaseSnapshot({
          sessionId: 'sess-A2',
          asOfEventId: 99,
          messages: [
            {
              id: 'mBad',
              role: 'assistant',
              content: '过时的A2污染内容',
              thinkingContent: null,
              messageType: null,
              notificationMeta: null,
              toolCallResult: null,
              truncated: false,
              createdUtc: '2026-11-16T00:00:00Z',
            },
          ],
        }),
      );
      await slowA2Promise;

      expect(useAppStore.getState().activeSessionId).toBe('sess-B');
      expect(useAppStore.getState().messages[0]?.content).toBe('会话B最新快照');
      expect(useAppStore.getState().asOfEventId).toBe(50);
    } finally {
      spy.mockRestore();
      cwSpy.mockRestore();
    }
  });

  it('F1: cross-epoch backend restart drops old epoch buffered events and accepts new lower event IDs', async () => {
    const spy = vi.spyOn(apiClient, 'getSnapshot');
    const cwSpy = vi.spyOn(apiClient, 'getContextWindow').mockRejectedValue(new Error('skip'));

    let resolveOldA!: (snap: SessionSnapshot) => void;
    spy.mockImplementationOnce(
      () =>
        new Promise<SessionSnapshot>((res) => {
          resolveOldA = res;
        }),
    );
    spy.mockImplementationOnce(() =>
      Promise.resolve(
        makeBaseSnapshot({
          sessionId: 'sess-f1',
          asOfEventId: 5,
          channelEpoch: 'epoch2',
          messages: [],
        }),
      ),
    );

    try {
      const store = useAppStore.getState();
      store.setConnection({
        mode: 'managed',
        status: 'ready',
        baseUrl: 'http://127.0.0.1:61000',
        targetBaseUrl: 'http://127.0.0.1:5010',
        accessToken: 'tok-old',
        instanceId: 'inst-epoch-1',
        controlPlaneAvailable: true,
        selfApprovalProtection: true,
        dataDirectory: 'D:/data',
        lastError: null,
        restartCount: 0,
      });
      store.setActiveSession('sess-f1');
      store.applySnapshot(makeBaseSnapshot({ sessionId: 'sess-f1', asOfEventId: 100, channelEpoch: 'epoch1', messages: [] }));

      // Start reconcile A on epoch1 and buffer an epoch1 content frame id=101 (no payload.epoch field)
      const recOldA = store.reconcileSession('A');
      useAppStore.getState().applySseEvent(
        { id: 101, event: 'content', data: { content: 'OLD_EPOCH' } } as never,
        { streamEpoch: 'epoch1' },
      );

      // Simulate managed backend restart -> new instanceId/restartCount -> new epoch2 snapshot asOf=5
      useAppStore.getState().setConnection({
        mode: 'managed',
        status: 'ready',
        baseUrl: 'http://127.0.0.1:61000',
        targetBaseUrl: 'http://127.0.0.1:5011',
        accessToken: 'tok-new',
        instanceId: 'inst-epoch-2',
        controlPlaneAvailable: true,
        selfApprovalProtection: true,
        dataDirectory: 'D:/data',
        lastError: null,
        restartCount: 1,
      });

      const recNewB = useAppStore.getState().reconcileSession('backend_restarted');
      resolveOldA(makeBaseSnapshot({ sessionId: 'sess-f1', asOfEventId: 100, channelEpoch: 'epoch1', messages: [] }));
      await recOldA;
      await recNewB;

      // Apply legitimate new stream event id=6 on epoch2
      useAppStore.getState().applySseEvent(
        { id: 6, event: 'content', data: { content: 'VALID_NEW_EVENT' } } as never,
        { streamEpoch: 'epoch2' },
      );

      const finalState = useAppStore.getState();
      expect(finalState.channelEpoch).toBe('epoch2');
      expect(finalState.asOfEventId).toBe(6);
      expect(finalState.streamingContent).toBe('VALID_NEW_EVENT');
    } finally {
      spy.mockRestore();
      cwSpy.mockRestore();
    }
  });

  it('F1 failure path: retains new epoch buffered events when epoch2 snapshot reconcile fails after epoch2 heartbeat', async () => {
    const spy = vi.spyOn(apiClient, 'getSnapshot').mockRejectedValueOnce(new Error('snapshot timeout'));
    const cwSpy = vi.spyOn(apiClient, 'getContextWindow').mockRejectedValue(new Error('skip'));

    try {
      const store = useAppStore.getState();
      store.setConnection({
        mode: 'managed',
        status: 'ready',
        baseUrl: 'http://127.0.0.1:61000',
        targetBaseUrl: 'http://127.0.0.1:5012',
        accessToken: 'tok-e2',
        instanceId: 'inst-e2',
        controlPlaneAvailable: true,
        selfApprovalProtection: true,
        dataDirectory: 'D:/data',
        lastError: null,
        restartCount: 0,
      });
      store.setActiveSession('sess-f1-fail');
      store.applySnapshot(makeBaseSnapshot({ sessionId: 'sess-f1-fail', asOfEventId: 100, channelEpoch: 'epoch1', messages: [] }));

      // Start reconcile triggered by epoch_changed -> epoch2, then receive epoch2 heartbeat & content while pending
      const recPromise = store.reconcileSession('epoch_changed', { expectedEpoch: 'epoch2' });
      useAppStore.getState().applySseEvent(
        { id: 1, event: 'heartbeat', data: { epoch: 'epoch2', pendingApprovals: 0 } } as never,
        { streamEpoch: 'epoch2' },
      );
      useAppStore.getState().applySseEvent(
        { id: 6, event: 'content', data: { content: 'EPOCH2_AFTER_FAIL' } } as never,
        { streamEpoch: 'epoch2' },
      );

      await recPromise;

      const finalState = useAppStore.getState();
      expect(finalState.isReconcilingSnapshot).toBe(false);
      expect(finalState.channelEpoch).toBe('epoch2');
      expect(finalState.asOfEventId).toBe(6);
      expect(finalState.streamingContent).toBe('EPOCH2_AFTER_FAIL');
    } finally {
      spy.mockRestore();
      cwSpy.mockRestore();
    }
  });

  it('handles updateconversationtitle SSE event, resets new-thread mode on setconversationid, and clears state on startNewConversation', () => {
    const store = useAppStore.getState();
    store.setActiveSession('sess-title');
    store.setConversationMode('new-thread');
    store.appendOptimisticUserMessage('请总结下这个方案');

    useAppStore.getState().applySseEvent({
      id: 10,
      event: 'setconversationid',
      data: { conversationId: 'conv-auto-1' },
    } as never);

    expect(useAppStore.getState().activeConversationId).toBe('conv-auto-1');
    expect(useAppStore.getState().conversationMode).toBe('continue-current');

    useAppStore.getState().applySseEvent({
      id: 11,
      event: 'updateconversationtitle',
      data: { conversationId: 'conv-auto-1', title: '方案总结分析', sessionId: 'sess-title' },
    } as never);

    expect(useAppStore.getState().latestTitleUpdate).toEqual({
      conversationId: 'conv-auto-1',
      title: '方案总结分析',
      sessionId: 'sess-title',
      seq: 1,
    });

    useAppStore.getState().startNewConversation('conv-fresh-2');
    const afterNew = useAppStore.getState();
    expect(afterNew.activeConversationId).toBe('conv-fresh-2');
    expect(afterNew.conversationMode).toBe('continue-current');
    expect(afterNew.messages).toEqual([]);
    expect(afterNew.streamingContent).toBe('');
    expect(afterNew.toolCalls).toEqual([]);
  });
});
