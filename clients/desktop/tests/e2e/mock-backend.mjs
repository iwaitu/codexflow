import { spawn } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { dirname } from 'node:path';

let stdinData = '';
process.stdin.setEncoding('utf-8');
process.stdin.on('data', (chunk) => {
  stdinData += chunk;
});

process.stdin.on('end', () => {
  const firstLine = stdinData.trim().split('\n')[0] || '{}';
  const creds = JSON.parse(firstLine);
  const accessToken = creds.accessToken;
  const controlToken = creds.controlToken;

  if (!accessToken || !controlToken || accessToken === controlToken) {
    process.stderr.write('Invalid pipe credentials\n');
    process.exit(1);
  }

  const pidFile = process.env.MOCK_PID_FILE;
  if (pidFile) {
    mkdirSync(dirname(pidFile), { recursive: true });
    appendFileSync(pidFile, `${process.pid}\n`, 'utf-8');
  }

  // Spawn a child worker process to verify process-tree termination (B8) when host stops this process.
  let grandchildPid = null;
  const childPidFile = process.env.MOCK_CHILD_PID_FILE;
  if (childPidFile) {
    mkdirSync(dirname(childPidFile), { recursive: true });
    const grandchild = spawn(process.execPath, ['-e', 'setInterval(() => {}, 60000)'], {
      env: { ...process.env, ELECTRON_RUN_AS_NODE: '1' },
      stdio: 'ignore',
      windowsHide: true,
    });
    if (grandchild.pid) {
      grandchildPid = grandchild.pid;
      appendFileSync(childPidFile, `${grandchildPid}\n`, 'utf-8');
    }
  }

  const stateFile = process.env.MOCK_STATE_FILE;
  const state = {
    epoch: `epoch-${process.pid}`,
    seq: 1,
    messages: [],
    conversations: [],
    pendingApprovals: [],
    jobs: [],
    rules: [
      {
        id: 'rule-e2e-1',
        sessionAgnosticDigest: 'digest-rule-1',
        toolName: 'run_command',
        executable: 'dotnet.exe',
        argv: ['dotnet', '--info'],
        createdAtUtc: '2026-11-16T00:00:00Z',
        note: 'e2e seeded rule',
      },
    ],
    sseClients: new Set(),
  };

  if (stateFile) {
    try {
      mkdirSync(dirname(stateFile), { recursive: true });
      if (existsSync(stateFile)) {
        const disk = JSON.parse(readFileSync(stateFile, 'utf-8'));
        state.messages = disk.messages || [];
        state.jobs = disk.jobs || [];
        state.rules = disk.rules || state.rules;
        state.seq = Number(disk.seq || 10) + 10;
        // Inject a fresh server-side snapshot message only present after crash recovery!
        if (!state.messages.some((m) => m.id === 'snap-recovered-marker')) {
          state.messages.push({
            id: 'snap-recovered-marker',
            role: 'system',
            content: '快照恢复新增消息-epoch2',
            truncated: false,
            createdUtc: new Date().toISOString(),
          });
        }
      }
    } catch {
      // ignore
    }
  }

  function broadcastSse(event, data) {
    const id = state.seq++;
    const frame = `id: ${id}\nevent: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
    for (const res of state.sseClients) {
      res.write(frame);
    }
  }

  function persistState() {
    if (stateFile) {
      writeFileSync(
        stateFile,
        JSON.stringify({
          seq: state.seq,
          messages: state.messages,
          jobs: state.jobs,
          rules: state.rules,
        }),
        'utf-8',
      );
    }
  }

  const server = createServer((req, res) => {
    const url = new URL(req.url || '/', 'http://127.0.0.1');
    const auth = req.headers.authorization || '';
    const bearer = auth.startsWith('Bearer ') ? auth.slice(7) : '';

    const origin = req.headers.origin;
    if (origin) {
      res.setHeader('Access-Control-Allow-Origin', origin);
      res.setHeader('Access-Control-Allow-Headers', 'Authorization, Content-Type, Last-Event-ID');
      res.setHeader('Access-Control-Allow-Methods', 'GET, POST, PUT, PATCH, DELETE, OPTIONS');
    }
    if (req.method === 'OPTIONS') {
      res.writeHead(204);
      res.end();
      return;
    }

    if (bearer !== accessToken && bearer !== controlToken) {
      res.writeHead(401, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ code: 'unauthorized' }));
      return;
    }

    const isControl = bearer === controlToken;
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => {
      const rawBody = Buffer.concat(chunks).toString('utf-8');
      const body = rawBody ? JSON.parse(rawBody) : {};

      const sendJson = (status, payload) => {
        res.writeHead(status, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify(payload));
      };

      if (req.method === 'GET' && url.pathname === '/api/system/status') {
        return sendJson(200, {
          version: '0.1.2.0',
          executionBackend: 'Local',
          permissionMode: 'Ask',
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
          tokenDeliveryMode: 'Pipe',
          backendAvailable: true,
          backendAvailabilityReason: null,
          dockerAvailable: false,
          dockerAvailabilityReason: 'docker not installed',
          modelConfigured: true,
          modelReachable: true,
          modelProbeStatus: 'Reachable',
          preflightMode: 'Report',
          preflightIssues: [],
        });
      }

      if (req.method === 'GET' && url.pathname === '/api/settings/execution') {
        return sendJson(200, {
          backend: 'Local',
          permissionMode: 'Ask',
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
          tokenDeliveryMode: 'Pipe',
        });
      }

      if (req.method === 'GET' && url.pathname === '/api/settings/model') {
        return sendJson(200, {
          apiUrl: 'http://10.0.30.247:8001/v1/{1}',
          apiMode: 'ChatCompletions',
          model: 'qwen3.8-27b-fp8',
          maxTokensLength: 128000,
          temperature: null,
          topP: null,
          apiKeySet: true,
          apiKeyLast4: '1234',
          supportsMultimodal: false,
        });
      }

      if (req.method === 'GET' && url.pathname === '/api/settings/permissions') {
        if (!isControl) {
          return sendJson(403, { code: 'forbidden' });
        }
        return sendJson(200, { count: state.rules.length, rules: state.rules });
      }

      if (req.method === 'DELETE' && url.pathname.startsWith('/api/settings/permissions/')) {
        if (!isControl) {
          return sendJson(403, { code: 'forbidden' });
        }
        const targetId = decodeURIComponent(url.pathname.slice('/api/settings/permissions/'.length));
        const before = state.rules.length;
        state.rules = state.rules.filter((r) => r.id !== targetId);
        if (state.rules.length === before) {
          return sendJson(404, { code: 'rule_not_found', message: `Rule ${targetId} not found` });
        }
        persistState();
        return sendJson(200, { removed: true, ruleId: targetId });
      }

      if (req.method === 'POST' && url.pathname === '/api/gateway/session/bootstrap') {
        return sendJson(200, {
          sessionId: 'sess-mock-1',
          workspaceName: '默认工作区',
          created: false,
          workspacePath: process.cwd(),
          isDefault: true,
        });
      }

      if (req.method === 'GET' && url.pathname === '/api/gateway/sessions') {
        return sendJson(200, {
          items: [
            {
              sessionId: 'sess-mock-1',
              name: '默认工作区',
              workspacePath: process.cwd(),
              currentStage: 1,
              isArchived: false,
              isExternalWorkspace: false,
              createdUtc: '2026-11-16T00:00:00Z',
              currentPlanArtifactId: null,
              activeTaskId: null,
              workMode: 'Development',
              modeVersion: 1,
              planModeActive: false,
              planModeVersion: 1,
            },
          ],
          total: 1,
          page: 1,
          pageSize: 50,
        });
      }

      if (req.method === 'GET' && url.pathname === '/api/gateway/session/sess-mock-1/conversations') {
        return sendJson(200, {
          items: state.conversations,
          total: state.conversations.length,
          page: 1,
          pageSize: 50,
        });
      }

      if (req.method === 'POST' && url.pathname === '/api/gateway/session/sess-mock-1/conversations') {
        const created = {
          conversationId: `conv-mock-${Date.now()}`,
          sessionId: 'sess-mock-1',
          name: body.name && String(body.name).trim() ? String(body.name).trim() : '新会话',
          threadType: 'Chat',
          isArchived: false,
          taskId: null,
          createdUtc: new Date().toISOString(),
          updatedAtUtc: new Date().toISOString(),
        };
        state.conversations.unshift(created);
        return sendJson(201, created);
      }

      if (req.method === 'GET' && url.pathname === '/api/gateway/session/sess-mock-1/context-window') {
        return sendJson(200, {
          sessionId: 'sess-mock-1',
          estimatedTokens: 1200,
          estimatedChars: 4800,
          recentTurns: 1,
          summaryChars: 0,
          totalPromptTokens: 1200,
          totalCachedPromptTokens: 0,
          totalCompletionTokens: 100,
          totalSessionTokens: 1300,
          modelMaxTokens: 128000,
          compressionTriggerTokens: 108800,
          usageRatio: 0.01,
          triggerRatio: 0.01,
        });
      }

      if (req.method === 'GET' && url.pathname === '/api/jobs/session/sess-mock-1') {
        return sendJson(200, state.jobs);
      }

      if (req.method === 'GET' && url.pathname === '/api/gateway/session/sess-mock-1/files') {
        return sendJson(200, {
          sessionId: 'sess-mock-1',
          path: '',
          entries: [
            {
              name: 'README.md',
              path: 'README.md',
              isDirectory: false,
              sizeBytes: 32,
              lastModifiedUtc: '2026-11-17T00:00:00Z',
            },
          ],
          truncated: false,
        });
      }

      if (req.method === 'GET' && url.pathname === '/api/gateway/session/sess-mock-1/files/content') {
        return sendJson(200, {
          sessionId: 'sess-mock-1',
          path: 'README.md',
          content: '# CodexFlow Mock Workspace',
          sizeBytes: 26,
          truncated: false,
          isBinary: false,
          encoding: 'utf-8',
        });
      }

      if (req.method === 'GET' && url.pathname === '/api/gateway/session/sess-mock-1/changes') {
        return sendJson(200, {
          sessionId: 'sess-mock-1',
          hasGitRepository: true,
          baseRef: 'HEAD',
          files: [{ filePath: 'README.md', status: 'Modified', additions: 1, deletions: 0 }],
          unifiedDiff: 'diff --git a/README.md b/README.md\n--- a/README.md\n+++ b/README.md\n@@ -1 +1,2 @@\n # Docs\n+# CodexFlow',
          truncated: false,
        });
      }

      if (req.method === 'GET' && url.pathname === '/api/gateway/session/sess-mock-1/approvals') {
        return sendJson(200, {
          sessionId: 'sess-mock-1',
          count: state.pendingApprovals.length,
          items: state.pendingApprovals,
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
        });
      }

      if (req.method === 'GET' && url.pathname === '/api/gateway/session/sess-mock-1/snapshot') {
        return sendJson(200, {
          sessionId: 'sess-mock-1',
          asOfEventId: Math.max(0, state.seq - 1),
          channelEpoch: state.epoch,
          activeTurn: { isRunning: false, messageId: null, conversationId: 'conv-1', partialContent: null, partialThinking: null, contentTruncated: false },
          session: {
            sessionId: 'sess-mock-1',
            name: '默认工作区',
            workspacePath: process.cwd(),
            currentStage: 1,
            isArchived: false,
            isExternalWorkspace: false,
            createdUtc: '2026-11-16T00:00:00Z',
            currentPlanArtifactId: null,
            activeTaskId: null,
            workMode: 'Development',
            modeVersion: 1,
            planModeActive: false,
            planModeVersion: 1,
          },
          activeConversationId: 'conv-1',
          messages: state.messages,
          pendingApprovals: state.pendingApprovals,
          currentPlan: null,
          taskListJson: null,
          activeToolResults: [],
          activeToolOutputs: [],
          jobs: state.jobs,
          controlPlaneAvailable: true,
          selfApprovalProtection: true,
        });
      }

      if (req.method === 'GET' && url.pathname === '/api/gateway/sse') {
        res.writeHead(200, {
          'Content-Type': 'text/event-stream',
          'Cache-Control': 'no-cache',
          Connection: 'keep-alive',
        });
        state.sseClients.add(res);
        broadcastSse('heartbeat', {
          sessionId: 'sess-mock-1',
          status: 'connected',
          subscriberCount: 1,
          epoch: state.epoch,
          intervalSeconds: 15,
          pendingApprovals: state.pendingApprovals.length,
        });
        res.on('close', () => state.sseClients.delete(res));
        return;
      }

      if (req.method === 'POST' && url.pathname === '/api/gateway/chat') {
        const userText = String(body.message || '');
        state.messages.push({
          id: `u-${Date.now()}`,
          role: 'user',
          content: userText,
          truncated: false,
          createdUtc: new Date().toISOString(),
        });
        persistState();

        sendJson(202, { messageId: 'msg-1', sessionId: 'sess-mock-1' });

        setTimeout(() => {
          if (!userText.includes('dotnet')) {
            const reply = `收到重启后请求: ${userText}`;
            state.messages.push({
              id: `a-${Date.now()}`,
              role: 'assistant',
              content: reply,
              truncated: false,
              createdUtc: new Date().toISOString(),
            });
            persistState();
            broadcastSse('content', { type: 'content', content: reply, text: reply });
            broadcastSse('done', { type: 'done', messageId: `msg-${Date.now()}`, source: 'UserChat' });
            const targetConv = body.conversationId
              ? state.conversations.find((c) => c.conversationId === body.conversationId)
              : null;
            if (targetConv && targetConv.name === '新会话') {
              targetConv.name = '会话标题已总结';
              targetConv.updatedAtUtc = new Date().toISOString();
              broadcastSse('updateconversationtitle', {
                conversationId: targetConv.conversationId,
                title: targetConv.name,
                sessionId: 'sess-mock-1',
              });
            }
            return;
          }
          broadcastSse('content', { type: 'content', content: '正在请求执行命令...', text: '正在请求执行命令...' });
          const approval = {
            approvalId: 'appr-e2e-1',
            requestDigest: 'digest-e2e-1',
            sessionId: 'sess-mock-1',
            jobId: 'job-e2e-1',
            turnId: 'turn-e2e-1',
            tool: 'run_command',
            executable: 'dotnet.exe',
            interpreterChain: [],
            argv: ['dotnet', '--version'],
            cwd: process.cwd(),
            stdinSha256: null,
            codeSha256: null,
            scriptSha256: null,
            inputsManifestSha256: null,
            backend: 'Local',
            network: 'Host',
            filesystem: 'unrestricted',
            reason: 'Ask mode',
            createdAtUtc: new Date().toISOString(),
            expiresAt: new Date(Date.now() + 600_000).toISOString(),
            expiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
            allowedDecisions: ['allow_once', 'deny'],
            executesWorkspaceCode: true,
            trustedOriginVerified: true,
            selfApprovalProtection: true,
          };
          state.pendingApprovals = [approval];
          broadcastSse('tool_approval_request', approval);
        }, 50);
        return;
      }

      if (req.method === 'POST' && url.pathname === '/api/gateway/approvals/appr-e2e-1') {
        if (!isControl) {
          return sendJson(403, { code: 'forbidden', message: 'Control plane credential required' });
        }
        if (body.requestDigest !== 'digest-e2e-1') {
          return sendJson(409, {
            code: 'digest_mismatch',
            message: 'Digest changed',
            expectedRequestDigest: 'digest-e2e-1',
          });
        }
        state.pendingApprovals = [];
        broadcastSse('tool_approval_resolved', {
          approvalId: 'appr-e2e-1',
          requestDigest: body.requestDigest,
          sessionId: 'sess-mock-1',
          decision: body.decision,
          resolvedAt: new Date().toISOString(),
        });
        state.jobs = [
          {
            jobId: 'job-e2e-1',
            taskId: 'T1',
            jobType: 'ForgeWorker',
            status: 'Completed',
            summary: '未在隔离环境中验证 (skipped(requires_docker_backend))',
          },
        ];
        state.messages.push({
          id: `a-${Date.now()}`,
          role: 'assistant',
          content: '命令已批准并在本地执行完成。',
          truncated: false,
          createdUtc: new Date().toISOString(),
        });
        persistState();
        broadcastSse('content', { type: 'content', content: '命令已批准并在本地执行完成。', text: '命令已批准并在本地执行完成。' });
        broadcastSse('done', { type: 'done', messageId: 'msg-1', source: 'UserChat' });
        return sendJson(200, { success: true, approvalId: 'appr-e2e-1', decision: body.decision, message: 'ok' });
      }

      if (req.method === 'GET' && url.pathname === '/api/plugins') {
        return sendJson(200, []);
      }

      if (req.method === 'GET' && url.pathname === '/api/skills') {
        return sendJson(200, []);
      }

      if (req.method === 'POST' && url.pathname === '/api/plugins/maintenance/prune') {
        if (!isControl) {
          return sendJson(403, { code: 'forbidden', message: 'Control plane credential required' });
        }
        return sendJson(200, {
          trimmedStepDiagnosticsCount: 0,
          deletedTerminalRunsCount: 0,
          deletedOrphanSnapshotsCount: 0,
          deletedOperationsCount: 0,
          retainedOutcomeUnknownRunsCount: 0,
          retainedProtectedSnapshotsCount: 0,
          executedAtUtc: new Date().toISOString(),
        });
      }

      return sendJson(404, { code: 'not_found', message: url.pathname });
    });
  });

  server.listen(0, '127.0.0.1', () => {
    const addr = server.address();
    const port = typeof addr === 'object' && addr ? addr.port : 0;
    process.stdout.write(`CODEXFLOW_READY ${JSON.stringify({ port, pid: process.pid, instanceId: `mock-${process.pid}` })}\n`);
  });
});
