import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  AlertTriangle,
  Check,
  Copy,
  FolderPlus,
  KeyRound,
  Pause,
  Play,
  QrCode,
  RefreshCw,
  ShieldAlert,
  ShieldCheck,
  Smartphone,
  Trash2,
  Unlink,
} from 'lucide-react';
import type {
  RemoteProxyMode,
  RemoteQrTicketResult,
  RemoteStatusSnapshot,
  RemoteWorkspaceAuthItem,
} from '@shared/ipc';
import { encodeQrMatrix, qrMatrixToSvgPath } from '../lib/qrSvg';

interface RemoteControlPanelProps {
  active: boolean;
  controlPlaneAvailable: boolean;
}

function formatDialStateLabel(snapshot: RemoteStatusSnapshot): {
  text: string;
  toneClass: string;
} {
  if (!snapshot.config.bound) {
    return {
      text: '未绑定官网账号',
      toneClass: 'border-slate-700 bg-slate-800/80 text-slate-300',
    };
  }
  if (snapshot.config.manualRemotePaused || snapshot.dial.state === 'paused') {
    return {
      text: '已手动暂停',
      toneClass: 'border-amber-500/40 bg-amber-950/40 text-amber-200',
    };
  }
  switch (snapshot.dial.state) {
    case 'connected':
      return {
        text: '已连接并在线',
        toneClass: 'border-emerald-500/40 bg-emerald-950/40 text-emerald-200',
      };
    case 'dialing':
      return {
        text: '正在连接云端中继…',
        toneClass: 'border-indigo-500/40 bg-indigo-950/40 text-indigo-200',
      };
    case 'backoff':
      return {
        text: `重连退避中 (第 ${snapshot.dial.retryAttempt} 次)`,
        toneClass: 'border-sky-500/40 bg-sky-950/40 text-sky-200',
      };
    case 'auth_required_407':
      return {
        text: '代理需要认证 (HTTP 407)',
        toneClass: 'border-rose-500/40 bg-rose-950/40 text-rose-200',
      };
    case 'forbidden_403':
      return {
        text: '云端拒绝连接 (HTTP 403)',
        toneClass: 'border-rose-500/40 bg-rose-950/40 text-rose-200',
      };
    case 'unauthorized_401':
      return {
        text: '设备凭据失效 (HTTP 401)',
        toneClass: 'border-rose-500/40 bg-rose-950/40 text-rose-200',
      };
    case 'rebind_required':
      return {
        text: '需要重新绑定本机',
        toneClass: 'border-rose-500/40 bg-rose-950/40 text-rose-200',
      };
    default:
      return {
        text: snapshot.dial.state,
        toneClass: 'border-slate-700 bg-slate-800 text-slate-300',
      };
  }
}

function normalizeSecondsRemaining(isoUtc: string | null | undefined, nowMs: number): number {
  if (!isoUtc) {
    return 0;
  }
  const parsed = Date.parse(isoUtc);
  if (!Number.isFinite(parsed)) {
    return 0;
  }
  return Math.max(0, Math.floor((parsed - nowMs) / 1000));
}

export const RemoteControlPanel: React.FC<RemoteControlPanelProps> = ({
  active,
  controlPlaneAvailable,
}) => {
  const remoteBridge = window.codexflow?.remote;

  const [snapshot, setSnapshot] = useState<RemoteStatusSnapshot | null>(null);
  const [workspaceAuths, setWorkspaceAuths] = useState<RemoteWorkspaceAuthItem[]>([]);
  const [portalUrlInput, setPortalUrlInput] = useState('');
  const [portalUrlDirty, setPortalUrlDirty] = useState(false);

  const [bindEmail, setBindEmail] = useState('');
  const [bindOtpCode, setBindOtpCode] = useState('');
  const [bindDeviceName, setBindDeviceName] = useState('');
  const [switchConfirmInfo, setSwitchConfirmInfo] = useState<{
    previousAccountEmail: string | null;
    nextUserId: string;
  } | null>(null);

  const [qrTicket, setQrTicket] = useState<RemoteQrTicketResult | null>(null);
  const [qrCopied, setQrCopied] = useState(false);
  const [confirmUnbindOpen, setConfirmUnbindOpen] = useState(false);

  const [proxyMode, setProxyMode] = useState<RemoteProxyMode>('system');
  const [proxyCustomUrl, setProxyCustomUrl] = useState('');
  const [proxyUsername, setProxyUsername] = useState('');
  const [proxyPasswordInput, setProxyPasswordInput] = useState('');
  const [proxyClearPassword, setProxyClearPassword] = useState(false);
  const [proxyFormInitialized, setProxyFormInitialized] = useState(false);

  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [nowMs, setNowMs] = useState(() => Date.now());

  const applySnapshot = useCallback((next: RemoteStatusSnapshot) => {
    setSnapshot(next);
    if (!portalUrlDirty) {
      setPortalUrlInput(next.config.portalUrl);
    }
    if (!proxyFormInitialized) {
      setProxyMode(next.config.proxy.mode);
      setProxyCustomUrl(next.config.proxy.customUrl);
      setProxyUsername(next.config.proxy.username);
      setProxyFormInitialized(true);
    }
  }, [portalUrlDirty, proxyFormInitialized]);

  const loadWorkspaceAuthorizations = useCallback(async () => {
    if (!remoteBridge || !controlPlaneAvailable) {
      return;
    }
    const res = await remoteBridge.listWorkspaceAuthorizations();
    if (res.ok && res.data) {
      setWorkspaceAuths(res.data);
    }
  }, [remoteBridge, controlPlaneAvailable]);

  const refreshStatus = useCallback(async () => {
    if (!remoteBridge) {
      return;
    }
    const res = await remoteBridge.getStatus();
    if (res.ok && res.data) {
      applySnapshot(res.data);
    } else if (!res.ok) {
      setError(res.error || '加载远程控制状态失败。');
    }
  }, [remoteBridge, applySnapshot]);

  useEffect(() => {
    if (!active || !remoteBridge) {
      return;
    }
    void refreshStatus();
    void loadWorkspaceAuthorizations();
    const unsubscribe = remoteBridge.onStatusChanged((next) => {
      applySnapshot(next);
    });
    return () => {
      unsubscribe?.();
    };
  }, [active, remoteBridge, refreshStatus, loadWorkspaceAuthorizations, applySnapshot]);

  useEffect(() => {
    if (!active) {
      return;
    }
    const needCountdown =
      Boolean(qrTicket) || Boolean(snapshot && snapshot.pendingPairRequests.length > 0);
    if (!needCountdown) {
      return;
    }
    const timer = setInterval(() => {
      setNowMs(Date.now());
    }, 1000);
    return () => clearInterval(timer);
  }, [active, qrTicket, snapshot]);

  const qrSvgModel = useMemo(() => {
    if (!qrTicket) {
      return null;
    }
    try {
      const matrix = encodeQrMatrix(qrTicket.qrUri);
      const path = qrMatrixToSvgPath(matrix);
      const quietZone = 4;
      const viewBoxSize = matrix.size + quietZone * 2;
      return { matrixSize: matrix.size, quietZone, viewBoxSize, path };
    } catch {
      return null;
    }
  }, [qrTicket]);

  const qrRemainingSeconds = useMemo(
    () => normalizeSecondsRemaining(qrTicket?.expiresAt, nowMs),
    [qrTicket, nowMs],
  );

  if (!remoteBridge) {
    return (
      <div className="mt-4 rounded-xl border border-slate-800 bg-slate-950 p-4 text-xs text-slate-300">
        当前桌面宿主未启用远程控制桥接能力。
      </div>
    );
  }

  const runAction = async (
    actionKey: string,
    fn: () => Promise<void>,
  ): Promise<void> => {
    setBusyAction(actionKey);
    setError(null);
    setNotice(null);
    try {
      await fn();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusyAction(null);
    }
  };

  const handleSavePortalUrl = async (e: React.FormEvent) => {
    e.preventDefault();
    await runAction('portal-url', async () => {
      const res = await remoteBridge.setPortalUrl(portalUrlInput.trim());
      if (!res.ok || !res.data) {
        setError(res.error || '保存服务器地址失败。');
        return;
      }
      setPortalUrlDirty(false);
      setPortalUrlInput(res.data.config.portalUrl);
      applySnapshot(res.data);
      setNotice('服务器地址已保存。');
    });
  };

  const handleSendOtp = async () => {
    if (!bindEmail.trim()) {
      setError('请先填写官网账号邮箱。');
      return;
    }
    await runAction('send-otp', async () => {
      if (portalUrlInput.trim()) {
        const urlRes = await remoteBridge.setPortalUrl(portalUrlInput.trim());
        if (!urlRes.ok) {
          setError(urlRes.error || '服务器地址格式无效。');
          return;
        }
        if (urlRes.data) {
          setPortalUrlDirty(false);
          applySnapshot(urlRes.data);
        }
      }
      const res = await remoteBridge.sendBindOtp({
        email: bindEmail.trim(),
        deviceName: bindDeviceName.trim() || undefined,
      });
      if (!res.ok || !res.data) {
        setError(res.error || '发送绑定验证码失败。');
        return;
      }
      setSwitchConfirmInfo(null);
      setNotice(res.data.message || '绑定验证码已发送至邮箱，请在有效期内输入。');
    });
  };

  const handleBindSubmit = async (confirmAccountSwitch: boolean) => {
    if (!bindEmail.trim() || !bindOtpCode.trim()) {
      setError('请输入官网邮箱和收到的验证码。');
      return;
    }
    await runAction('verify-bind', async () => {
      const res = await remoteBridge.verifyAndBind({
        email: bindEmail.trim(),
        code: bindOtpCode.trim(),
        deviceName: bindDeviceName.trim() || undefined,
        confirmAccountSwitch,
      });
      if (!res.ok || !res.data) {
        setError(res.error || '设备绑定失败。');
        return;
      }
      if (res.data.status === 'requires_account_switch_confirmation') {
        setSwitchConfirmInfo({
          previousAccountEmail: res.data.previousAccountEmail,
          nextUserId: res.data.nextUserId,
        });
        return;
      }
      setSwitchConfirmInfo(null);
      setBindOtpCode('');
      applySnapshot(res.data.snapshot);
      await loadWorkspaceAuthorizations();
      setNotice('本机已成功绑定到官网账号，并已启用独立隔离数据目录。');
    });
  };

  const handleTogglePaused = async () => {
    if (!snapshot) {
      return;
    }
    const nextPaused = !snapshot.config.manualRemotePaused;
    await runAction('toggle-pause', async () => {
      const res = await remoteBridge.setManualPaused(nextPaused);
      if (!res.ok || !res.data) {
        setError(res.error || '切换远程控制状态失败。');
        return;
      }
      applySnapshot(res.data);
      setNotice(nextPaused ? '已暂停远程控制隧道。' : '已恢复远程控制连接。');
    });
  };

  const handleUnbindConfirm = async () => {
    await runAction('unbind', async () => {
      const res = await remoteBridge.unbind();
      if (!res.ok || !res.data) {
        setError(res.error || '解绑本机失败。');
        return;
      }
      setConfirmUnbindOpen(false);
      setQrTicket(null);
      applySnapshot(res.data);
      setNotice('已解绑本机，本地配对与中继凭据已清除，并切回默认本地数据目录。');
    });
  };

  const handleIssueQrTicket = async () => {
    await runAction('issue-qr', async () => {
      const res = await remoteBridge.issueQrTicket();
      if (!res.ok || !res.data) {
        setError(res.error || '申请扫码配对票据失败。');
        return;
      }
      setQrTicket(res.data);
      setQrCopied(false);
      setNowMs(Date.now());
      setNotice('已生成 120 秒有效的扫码配对二维码。');
    });
  };

  const handleCopyQrUri = async () => {
    if (!qrTicket) {
      return;
    }
    try {
      await navigator.clipboard.writeText(qrTicket.qrUri);
      setQrCopied(true);
    } catch {
      setError('无法写入剪贴板，请手动扫码。');
    }
  };

  const handleRespondPair = async (pairingId: string, approved: boolean) => {
    await runAction(`pair-${pairingId}`, async () => {
      const res = await remoteBridge.respondPairRequest({ pairingId, approved });
      if (!res.ok || !res.data) {
        setError(res.error || '处理配对请求失败。');
        return;
      }
      applySnapshot(res.data);
      if (approved) {
        setQrTicket(null);
      }
      setNotice(approved ? '已签名批准手机配对请求。' : '已拒绝该手机配对请求。');
    });
  };

  const handleRevokePairing = async (pairingId: string) => {
    await runAction(`revoke-${pairingId}`, async () => {
      const res = await remoteBridge.revokePairing(pairingId);
      if (!res.ok || !res.data) {
        setError(res.error || '撤销配对失败。');
        return;
      }
      applySnapshot(res.data);
      setNotice('已在本地立即吊销该手机配对并中断其在途请求；如需清理云端设备清单请在官网控制台撤销。');
    });
  };

  const handleAddWorkspaceAuthorization = async () => {
    const picked = await window.codexflow.pickFolder();
    if (!picked) {
      return;
    }
    await runAction('add-ws-auth', async () => {
      const res = await remoteBridge.setWorkspaceAuthorization({
        workspacePath: picked,
        authorized: true,
      });
      if (!res.ok) {
        setError(res.error || '授权外部工作区失败。');
        return;
      }
      await loadWorkspaceAuthorizations();
      setNotice(`已授权远程访问外部目录：${picked}`);
    });
  };

  const handleRevokeWorkspaceAuthorization = async (workspaceRealPath: string) => {
    await runAction(`rm-ws-${workspaceRealPath}`, async () => {
      const res = await remoteBridge.setWorkspaceAuthorization({
        workspacePath: workspaceRealPath,
        authorized: false,
      });
      if (!res.ok) {
        setError(res.error || '撤销外部工作区授权失败。');
        return;
      }
      await loadWorkspaceAuthorizations();
      setNotice('已撤销该外部工作区的远程访问授权。');
    });
  };

  const handleSaveProxy = async (e: React.FormEvent) => {
    e.preventDefault();
    await runAction('proxy', async () => {
      const res = await remoteBridge.setProxySettings({
        mode: proxyMode,
        customUrl: proxyCustomUrl.trim(),
        username: proxyUsername.trim(),
        password: proxyPasswordInput ? proxyPasswordInput : undefined,
        clearPassword: proxyClearPassword,
      });
      if (!res.ok || !res.data) {
        setError(res.error || '保存网络代理配置失败。');
        return;
      }
      setProxyPasswordInput('');
      setProxyClearPassword(false);
      applySnapshot(res.data);
      setNotice('网络代理配置已保存。');
    });
  };

  const statusBadge = snapshot
    ? formatDialStateLabel(snapshot)
    : { text: '加载中…', toneClass: 'border-slate-700 bg-slate-800 text-slate-300' };
  const isBound = Boolean(snapshot?.config.bound);
  const nowSec = Math.floor(nowMs / 1000);

  return (
    <div className="mt-4 space-y-4 text-xs" data-testid="remote-control-panel">
      {/* 1. 远程连接状态总览与服务器地址 */}
      <div className="rounded-xl border border-slate-800 bg-slate-950 p-4 space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div>
            <div className="flex items-center gap-2">
              <span className="text-sm font-semibold text-slate-100">手机端远程控制状态</span>
              <span className={`rounded-md border px-2 py-0.5 text-[11px] font-medium ${statusBadge.toneClass}`}>
                {statusBadge.text}
              </span>
            </div>
            <p className="mt-1 text-[11px] text-slate-400">
              所有远程指令均需经手机端 P-256 签名与本机二次校验；桌面操作默认不外泄屏幕像素与密钥原文。
            </p>
          </div>
          <button
            type="button"
            onClick={() => void refreshStatus()}
            disabled={busyAction !== null}
            className="flex items-center gap-1 rounded-lg border border-slate-700 px-2.5 py-1 text-[11px] text-slate-300 hover:bg-slate-800 disabled:opacity-40"
          >
            <RefreshCw className="h-3.5 w-3.5" />
            刷新状态
          </button>
        </div>

        {snapshot && !snapshot.safeStorageAvailable && (
          <div className="flex items-start gap-2 rounded-lg border border-amber-500/40 bg-amber-950/30 p-2.5 text-[11px] text-amber-200">
            <ShieldAlert className="mt-0.5 h-4 w-4 shrink-0 text-amber-400" />
            <span>
              当前系统密钥链不可用，远程设备凭据仅以文件权限保护，建议启用系统级加密钥匙串。
            </span>
          </div>
        )}

        {snapshot?.dial.lastError && snapshot.dial.state !== 'connected' && snapshot.dial.state !== 'stopped' && (
          <div className="rounded-lg border border-slate-800 bg-slate-900/70 px-3 py-2 text-[11px] text-slate-300">
            最近连接反馈：<span className="text-slate-100">{snapshot.dial.lastError}</span>
          </div>
        )}

        {isBound && snapshot?.config.account && snapshot.config.device && (
          <div className="grid grid-cols-1 gap-2 rounded-lg border border-slate-800/80 bg-slate-900/50 p-3 sm:grid-cols-2">
            <div>
              <div className="text-[11px] text-slate-400">已绑定账号</div>
              <div className="font-medium text-slate-100">{snapshot.config.account.accountEmail}</div>
            </div>
            <div>
              <div className="text-[11px] text-slate-400">桌面设备指纹</div>
              <div className="font-mono text-[11px] text-slate-200">
                {snapshot.config.device.deviceName} · {snapshot.config.device.desktopSpkiSha256.slice(0, 16)}…
              </div>
            </div>
            <div>
              <div className="text-[11px] text-slate-400">隧道 Epoch / 代理链路</div>
              <div className="text-slate-200">
                Epoch {snapshot.connEpoch ?? '—'} · {snapshot.dial.resolvedProxyDescription}
              </div>
            </div>
            <div className="flex flex-wrap items-center justify-end gap-2">
              <button
                type="button"
                data-testid="remote-pause-toggle"
                onClick={() => void handleTogglePaused()}
                disabled={busyAction !== null}
                className="flex items-center gap-1 rounded-lg border border-slate-700 bg-slate-800 px-3 py-1.5 text-slate-100 hover:bg-slate-700 disabled:opacity-40"
              >
                {snapshot.config.manualRemotePaused ? (
                  <>
                    <Play className="h-3.5 w-3.5 text-emerald-400" />
                    恢复远程控制
                  </>
                ) : (
                  <>
                    <Pause className="h-3.5 w-3.5 text-amber-400" />
                    暂停远程控制
                  </>
                )}
              </button>
              <button
                type="button"
                data-testid="remote-unbind-button"
                onClick={() => setConfirmUnbindOpen((prev) => !prev)}
                disabled={busyAction !== null}
                className="flex items-center gap-1 rounded-lg border border-rose-500/40 px-3 py-1.5 text-rose-300 hover:bg-rose-950/50 disabled:opacity-40"
              >
                <Unlink className="h-3.5 w-3.5" />
                解绑本机
              </button>
            </div>
          </div>
        )}

        {confirmUnbindOpen && (
          <div
            data-testid="remote-unbind-confirm"
            className="rounded-lg border border-rose-500/40 bg-rose-950/30 p-3 text-rose-100"
          >
            <div className="font-medium">确认解绑当前桌面设备？</div>
            <p className="mt-1 text-[11px] text-rose-200/90">
              将立即断开远程隧道、清除本地设备私钥与手机配对缓存，并切回默认本地数据目录。
            </p>
            <div className="mt-2 flex gap-2">
              <button
                type="button"
                data-testid="remote-unbind-confirm-btn"
                onClick={() => void handleUnbindConfirm()}
                disabled={busyAction !== null}
                className="rounded-lg bg-rose-600 px-3 py-1 font-medium text-white hover:bg-rose-500 disabled:opacity-40"
              >
                确认解绑
              </button>
              <button
                type="button"
                onClick={() => setConfirmUnbindOpen(false)}
                className="rounded-lg border border-slate-700 px-3 py-1 text-slate-200 hover:bg-slate-800"
              >
                取消
              </button>
            </div>
          </div>
        )}

        <form onSubmit={(e) => void handleSavePortalUrl(e)} className="flex flex-col gap-2 sm:flex-row sm:items-end">
          <label className="flex-1">
            <span className="mb-1 block text-[11px] text-slate-400">中继服务端点（Portal HTTPS Origin）</span>
            <input
              type="url"
              value={portalUrlInput}
              onChange={(e) => {
                setPortalUrlDirty(true);
                setPortalUrlInput(e.target.value);
              }}
              placeholder="https://portal.codexflow.example"
              className="w-full rounded-lg border border-slate-700 bg-slate-900 px-3 py-1.5 text-slate-100"
            />
          </label>
          <button
            type="submit"
            disabled={busyAction !== null || !portalUrlInput.trim()}
            className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-1.5 font-medium text-slate-200 hover:bg-slate-700 disabled:opacity-40"
          >
            保存地址
          </button>
        </form>
      </div>

      {/* 2. 6 位 SAS 待确认配对审批（最高优先级展示） */}
      {snapshot && snapshot.pendingPairRequests.length > 0 && (
        <div
          data-testid="remote-pending-pair-section"
          className="rounded-xl border border-indigo-500/50 bg-indigo-950/30 p-4 space-y-3"
        >
          <div className="flex items-center gap-2 text-indigo-200 font-semibold">
            <ShieldCheck className="h-4 w-4 text-indigo-400" />
            待确认的手机端扫码配对请求（请核对 6 位 SAS 验证码）
          </div>
          {snapshot.pendingPairRequests.map((req) => {
            const remSec = Math.max(0, req.expiresAtUnix - nowSec);
            return (
              <div
                key={req.pairingId}
                data-testid={`remote-pending-pair-${req.pairingId}`}
                className="rounded-lg border border-indigo-500/40 bg-slate-950/80 p-3 space-y-2.5"
              >
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div>
                    <div className="font-medium text-slate-100">
                      {req.deviceName} ({req.platform.toUpperCase()})
                    </div>
                    <div className="font-mono text-[11px] text-slate-400">
                      Mobile ID: {req.mobileId} · 剩余 {remSec}s
                    </div>
                  </div>
                  <div
                    data-testid={`remote-pair-sas-${req.sas6}`}
                    className="rounded-lg border border-indigo-400/50 bg-indigo-950/60 px-4 py-1.5 font-mono text-lg font-bold tracking-[0.25em] text-indigo-100"
                  >
                    {req.sas6}
                  </div>
                </div>
                <p className="text-[11px] text-slate-300">
                  请确认您的手机屏幕上显示的 6 位数字与上方完全一致后再点击允许，否则请立即拒绝。
                </p>
                <div className="flex justify-end gap-2">
                  <button
                    type="button"
                    data-testid={`remote-pair-deny-${req.pairingId}`}
                    onClick={() => void handleRespondPair(req.pairingId, false)}
                    disabled={busyAction !== null}
                    className="rounded-lg border border-slate-700 px-3 py-1.5 text-slate-200 hover:bg-slate-800 disabled:opacity-40"
                  >
                    拒绝配对
                  </button>
                  <button
                    type="button"
                    data-testid={`remote-pair-approve-${req.pairingId}`}
                    onClick={() => void handleRespondPair(req.pairingId, true)}
                    disabled={busyAction !== null}
                    className="rounded-lg bg-indigo-600 px-3 py-1.5 font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
                  >
                    确认一致并允许配对
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      )}

      {/* 3. 未绑定 / 需要重新绑定时展示官网邮箱 OTP 绑定面板 */}
      {(!isBound || snapshot?.dial.state === 'rebind_required' || snapshot?.dial.state === 'unauthorized_401') && (
        <div className="rounded-xl border border-slate-800 bg-slate-950 p-4 space-y-3" data-testid="remote-bind-section">
          <div className="flex items-center gap-2 font-semibold text-slate-100">
            <KeyRound className="h-4 w-4 text-indigo-400" />
            绑定官网账号（邮箱 OTP 验证）
          </div>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <label className="block">
              <span className="mb-1 block text-slate-300">官网账号邮箱</span>
              <input
                type="email"
                value={bindEmail}
                onChange={(e) => setBindEmail(e.target.value)}
                placeholder="you@example.com"
                className="w-full rounded-lg border border-slate-700 bg-slate-900 px-3 py-2 text-slate-100"
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-slate-300">桌面设备名称（可选）</span>
              <input
                value={bindDeviceName}
                onChange={(e) => setBindDeviceName(e.target.value)}
                placeholder="例如：My Workstation"
                className="w-full rounded-lg border border-slate-700 bg-slate-900 px-3 py-2 text-slate-100"
              />
            </label>
          </div>
          <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
            <label className="flex-1">
              <span className="mb-1 block text-slate-300">邮箱验证码（6 位 OTP）</span>
              <input
                value={bindOtpCode}
                onChange={(e) => setBindOtpCode(e.target.value)}
                placeholder="输入邮件中的 6 位数字验证码"
                className="w-full rounded-lg border border-slate-700 bg-slate-900 px-3 py-2 font-mono text-slate-100"
              />
            </label>
            <button
              type="button"
              data-testid="remote-send-otp-btn"
              onClick={() => void handleSendOtp()}
              disabled={busyAction !== null || !bindEmail.trim()}
              className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-2 font-medium text-slate-200 hover:bg-slate-700 disabled:opacity-40"
            >
              发送验证码
            </button>
            <button
              type="button"
              data-testid="remote-verify-bind-btn"
              onClick={() => void handleBindSubmit(false)}
              disabled={busyAction !== null || !bindEmail.trim() || !bindOtpCode.trim()}
              className="rounded-lg bg-indigo-600 px-4 py-2 font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
            >
              验证并绑定本机
            </button>
          </div>

          {switchConfirmInfo && (
            <div
              data-testid="remote-account-switch-confirm"
              className="rounded-xl border border-amber-500/50 bg-amber-950/30 p-3 text-amber-100 space-y-2"
            >
              <div className="flex items-center gap-2 font-semibold">
                <AlertTriangle className="h-4 w-4 text-amber-400" />
                检测到切换官网账号
              </div>
              <p className="text-[11px] text-amber-200/90">
                当前本机曾绑定账号 {switchConfirmInfo.previousAccountEmail ?? '未知'}。切换到新账号后，宿主进程将自动隔离切换至独立的专属数据与配置目录（<code className="rounded bg-slate-900 px-1">CODEXFLOW_HOME</code>），严格不跨账号读取原会话、模型密钥或浏览器登录态。
              </p>
              <div className="flex gap-2">
                <button
                  type="button"
                  data-testid="remote-confirm-account-switch-btn"
                  onClick={() => void handleBindSubmit(true)}
                  disabled={busyAction !== null}
                  className="rounded-lg bg-amber-600 px-3 py-1 font-medium text-white hover:bg-amber-500 disabled:opacity-40"
                >
                  确认切换隔离目录并绑定
                </button>
                <button
                  type="button"
                  onClick={() => setSwitchConfirmInfo(null)}
                  className="rounded-lg border border-slate-700 px-3 py-1 text-slate-200 hover:bg-slate-800"
                >
                  取消
                </button>
              </div>
            </div>
          )}
        </div>
      )}

      {/* 4. 手机扫码配对与已配对列表（已绑定时展示） */}
      {isBound && (
        <div className="rounded-xl border border-slate-800 bg-slate-950 p-4 space-y-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div className="flex items-center gap-2 font-semibold text-slate-100">
              <QrCode className="h-4 w-4 text-indigo-400" />
              手机扫码配对与已配对设备
            </div>
            <button
              type="button"
              data-testid="remote-issue-qr-btn"
              onClick={() => void handleIssueQrTicket()}
              disabled={busyAction !== null}
              className="rounded-lg bg-indigo-600 px-3 py-1.5 font-medium text-white hover:bg-indigo-500 disabled:opacity-40"
            >
              生成扫码配对码
            </button>
          </div>

          {qrTicket && (
            <div
              data-testid="remote-qr-container"
              className="flex flex-col items-center gap-3 rounded-xl border border-slate-800 bg-slate-900/70 p-4 sm:flex-row sm:items-start"
            >
              {qrSvgModel ? (
                <div className="shrink-0 rounded-xl bg-white p-2.5 shadow">
                  <svg
                    role="img"
                    aria-label="Remote Pairing QR Code"
                    width={168}
                    height={168}
                    viewBox={`-${qrSvgModel.quietZone} -${qrSvgModel.quietZone} ${qrSvgModel.viewBoxSize} ${qrSvgModel.viewBoxSize}`}
                    shapeRendering="crispEdges"
                  >
                    <rect
                      x={-qrSvgModel.quietZone}
                      y={-qrSvgModel.quietZone}
                      width={qrSvgModel.viewBoxSize}
                      height={qrSvgModel.viewBoxSize}
                      fill="#ffffff"
                    />
                    <path d={qrSvgModel.path} fill="#0f172a" />
                  </svg>
                </div>
              ) : (
                <div className="rounded-lg border border-amber-500/40 bg-amber-950/30 p-3 text-amber-200">
                  二维码内容较长，请直接复制下方配对链接。
                </div>
              )}
              <div className="min-w-0 flex-1 space-y-2">
                <div className="text-slate-200 font-medium">
                  请使用 CodexFlow 手机客户端扫描左侧二维码
                </div>
                <div className="text-[11px] text-slate-400">
                  票据 ID：<span className="font-mono text-slate-300">{qrTicket.ticketId}</span> ·{' '}
                  {qrRemainingSeconds > 0 ? `剩余有效时间 ${qrRemainingSeconds}s` : '已过期，请重新生成'}
                </div>
                <div className="flex flex-wrap gap-2">
                  <button
                    type="button"
                    onClick={() => void handleCopyQrUri()}
                    className="flex items-center gap-1 rounded-lg border border-slate-700 px-2.5 py-1 text-[11px] text-slate-200 hover:bg-slate-800"
                  >
                    {qrCopied ? <Check className="h-3.5 w-3.5 text-emerald-400" /> : <Copy className="h-3.5 w-3.5" />}
                    {qrCopied ? '已复制配对链接' : '复制配对链接'}
                  </button>
                </div>
              </div>
            </div>
          )}

          <div className="space-y-2">
            {snapshot && snapshot.config.pairings.length === 0 && (
              <div className="text-[11px] text-slate-400">暂无已配对的手机设备。</div>
            )}
            {snapshot?.config.pairings.map((pairing) => (
              <div
                key={pairing.pairingId}
                className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-slate-800 bg-slate-900/50 px-3 py-2"
              >
                <div className="flex items-center gap-2.5">
                  <Smartphone className="h-4 w-4 text-slate-400" />
                  <div>
                    <div className="font-medium text-slate-100">
                      {pairing.deviceName}{' '}
                      <span
                        className={`ml-1 rounded px-1.5 py-0.5 text-[10px] ${
                          pairing.status === 'active'
                            ? 'bg-emerald-950/60 text-emerald-300'
                            : 'bg-slate-800 text-slate-400'
                        }`}
                      >
                        {pairing.status === 'active' ? '已授权' : '已吊销'}
                      </span>
                    </div>
                    <div className="font-mono text-[10px] text-slate-400">
                      {pairing.platform.toUpperCase()} · {pairing.sessionKeyId.slice(0, 12)}… ·{' '}
                      {pairing.bioKeyId ? '支持生物高危审批' : '仅日常会话签名'}
                    </div>
                  </div>
                </div>
                {pairing.status === 'active' && (
                  <button
                    type="button"
                    onClick={() => void handleRevokePairing(pairing.pairingId)}
                    disabled={busyAction !== null}
                    className="flex items-center gap-1 rounded-lg border border-rose-500/40 px-2.5 py-1 text-[11px] text-rose-300 hover:bg-rose-950/40 disabled:opacity-40"
                  >
                    <Trash2 className="h-3 w-3" />
                    本地吊销
                  </button>
                )}
              </div>
            ))}
          </div>
        </div>
      )}

      {/* 5. 外部工作区远程授权白名单 */}
      <div className="rounded-xl border border-slate-800 bg-slate-950 p-4 space-y-2.5">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div>
            <div className="font-semibold text-slate-100">外部工作区远程访问白名单</div>
            <div className="text-[11px] text-slate-400">
              宿主受管工作区默认可访问；非受管外部目录必须经本机明确加入白名单后才对手机端可见。
            </div>
          </div>
          <button
            type="button"
            onClick={() => void handleAddWorkspaceAuthorization()}
            disabled={busyAction !== null || !controlPlaneAvailable}
            className="flex items-center gap-1 rounded-lg border border-slate-700 bg-slate-800 px-2.5 py-1.5 text-slate-200 hover:bg-slate-700 disabled:opacity-40"
          >
            <FolderPlus className="h-3.5 w-3.5" />
            添加授权目录
          </button>
        </div>

        {workspaceAuths.length === 0 ? (
          <div className="text-[11px] text-slate-500">暂无额外授权的外部工作区目录。</div>
        ) : (
          <div className="space-y-1.5">
            {workspaceAuths.map((item) => (
              <div
                key={item.workspaceRealPath}
                className="flex items-center justify-between gap-2 rounded-lg border border-slate-800 bg-slate-900/50 px-3 py-2"
              >
                <span className="truncate font-mono text-[11px] text-slate-200">
                  {item.workspaceRealPath}
                </span>
                <button
                  type="button"
                  onClick={() => void handleRevokeWorkspaceAuthorization(item.workspaceRealPath)}
                  disabled={busyAction !== null || !controlPlaneAvailable}
                  className="shrink-0 rounded border border-rose-500/40 px-2 py-0.5 text-[11px] text-rose-300 hover:bg-rose-950/40 disabled:opacity-40"
                >
                  撤销
                </button>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* 6. 出站隧道代理配置 */}
      <form onSubmit={(e) => void handleSaveProxy(e)} className="rounded-xl border border-slate-800 bg-slate-950 p-4 space-y-3">
        <div className="font-semibold text-slate-100">中继隧道出站代理配置</div>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
          <label className="block">
            <span className="mb-1 block text-slate-300">代理模式</span>
            <select
              value={proxyMode}
              onChange={(e) => setProxyMode(e.target.value as RemoteProxyMode)}
              className="w-full rounded-lg border border-slate-700 bg-slate-900 px-3 py-1.5 text-slate-100"
            >
              <option value="system">跟随系统代理 / PAC</option>
              <option value="direct">直连（不使用代理）</option>
              <option value="custom">自定义代理</option>
            </select>
          </label>
          {proxyMode === 'custom' && (
            <label className="block sm:col-span-2">
              <span className="mb-1 block text-slate-300">代理服务器地址（http / https / socks5）</span>
              <input
                value={proxyCustomUrl}
                onChange={(e) => setProxyCustomUrl(e.target.value)}
                placeholder="http://127.0.0.1:7890"
                className="w-full rounded-lg border border-slate-700 bg-slate-900 px-3 py-1.5 text-slate-100"
              />
            </label>
          )}
        </div>
        {proxyMode !== 'direct' && (
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <label className="block">
              <span className="mb-1 block text-slate-300">代理用户名（可选）</span>
              <input
                value={proxyUsername}
                onChange={(e) => setProxyUsername(e.target.value)}
                placeholder="留空则无身份认证"
                className="w-full rounded-lg border border-slate-700 bg-slate-900 px-3 py-1.5 text-slate-100"
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-slate-300">
                代理密码（{snapshot?.config.proxy.hasPassword ? '已加密保存' : '未设置'}）
              </span>
              <input
                type="password"
                value={proxyPasswordInput}
                onChange={(e) => setProxyPasswordInput(e.target.value)}
                placeholder="留空保持当前密码不变"
                className="w-full rounded-lg border border-slate-700 bg-slate-900 px-3 py-1.5 text-slate-100"
              />
            </label>
          </div>
        )}
        <div className="flex flex-wrap items-center justify-between gap-2">
          <label className="flex items-center gap-2 text-[11px] text-slate-400">
            <input
              type="checkbox"
              checked={proxyClearPassword}
              onChange={(e) => setProxyClearPassword(e.target.checked)}
              className="h-3.5 w-3.5 rounded border-slate-700 accent-indigo-600"
            />
            清除已保存的代理密码
          </label>
          <button
            type="submit"
            disabled={busyAction !== null}
            className="rounded-lg border border-slate-700 bg-slate-800 px-3 py-1.5 font-medium text-slate-200 hover:bg-slate-700 disabled:opacity-40"
          >
            保存代理配置
          </button>
        </div>
      </form>

      {notice && (
        <div className="rounded-lg border border-emerald-500/40 bg-emerald-950/40 p-2.5 text-emerald-200">
          {notice}
        </div>
      )}
      {error && (
        <div className="rounded-lg border border-rose-500/40 bg-rose-950/40 p-2.5 text-rose-200">
          {error}
        </div>
      )}
    </div>
  );
};
