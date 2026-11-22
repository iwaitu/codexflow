import { useState } from 'react';
import { Camera, KeyRound, QrCode, ShieldAlert, ShieldCheck } from 'lucide-react';
import { useMobileStore } from '@/store/useMobileStore';

export function PairView() {
  const capabilities = useMobileStore((s) => s.capabilities);
  const portalUrlDraft = useMobileStore((s) => s.portalUrlDraft);
  const setPortalUrlDraft = useMobileStore((s) => s.setPortalUrlDraft);
  const pairingStage = useMobileStore((s) => s.pairingStage);
  const pairingPreview = useMobileStore((s) => s.pairingPreview);
  const scanWithNativeCamera = useMobileStore((s) => s.scanWithNativeCamera);
  const startQrPairing = useMobileStore((s) => s.startQrPairing);
  const setError = useMobileStore((s) => s.setError);

  const [manualUri, setManualUri] = useState('');
  const [deviceName, setDeviceName] = useState('我的手机');
  const [showPortalConfig, setShowPortalConfig] = useState(false);

  const busy = pairingStage !== null;

  const handleScanClick = async () => {
    try {
      const rawUri = await scanWithNativeCamera();
      await startQrPairing({ rawQrUri: rawUri, mobileDeviceName: deviceName });
    } catch (err) {
      setError(err);
    }
  };

  const handleSubmitManual = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!manualUri.trim()) {
      return;
    }
    await startQrPairing({
      rawQrUri: manualUri.trim(),
      mobileDeviceName: deviceName,
    });
  };

  return (
    <div className="flex flex-1 flex-col overflow-y-auto px-4 py-6">
      <div className="mx-auto w-full max-w-md space-y-5">
        <div className="space-y-2 text-center">
          <div className="mx-auto flex h-14 w-14 items-center justify-center rounded-2xl border border-sky-500/30 bg-sky-500/10 text-sky-400">
            <QrCode className="h-7 w-7" />
          </div>
          <h1 className="text-xl font-bold tracking-tight text-slate-100">
            配对 CodexFlow 桌面端
          </h1>
          <p className="text-xs leading-relaxed text-slate-400">
            打开桌面端「设置 → 远程控制」生成二维码，端到端 P-256 硬件密钥与桌面锁定绑定，不会经云端落盘敏感正文。
          </p>
        </div>

        {capabilities && !capabilities.biometricAvailable ? (
          <div className="flex items-start gap-2 rounded-xl border border-amber-500/40 bg-amber-950/40 p-3 text-xs text-amber-200">
            <ShieldAlert className="mt-0.5 h-4 w-4 shrink-0 text-amber-400" />
            <span>
              当前设备未开启硬件生物识别（Face ID / 指纹）。仍可配对用于查看会话与对话，但高危审批、计划批准和紧急停止需在开启生物识别后重配。
            </span>
          </div>
        ) : null}

        {pairingStage ? (
          <div className="space-y-4 rounded-2xl border border-sky-500/40 bg-slate-900/90 p-5">
            <div className="flex items-center gap-2 text-sm font-semibold text-sky-300">
              <KeyRound className="h-4 w-4 animate-pulse" />
              <span>
                {pairingStage === 'scanning_qr'
                  ? '正在读取扫码 URI...'
                  : pairingStage === 'activating_ticket'
                    ? '正在生成硬件密钥并向桌面发起认证...'
                    : pairingStage === 'awaiting_desktop_sas'
                      ? '请在桌面端核对 6 位验证码'
                      : '正在完成正式令牌兑换与安全落库...'}
              </span>
            </div>

            {pairingPreview ? (
              <div className="space-y-3 rounded-xl border border-slate-700 bg-slate-950 p-4 text-center">
                <div className="text-xs text-slate-400">本地独立计算的 6 位 SAS 安全核对码</div>
                <div
                  data-testid="pairing-sas6"
                  className="font-mono text-4xl font-bold tracking-[0.25em] text-emerald-400"
                >
                  {pairingPreview.sas6}
                </div>
                <div className="flex items-center justify-center gap-1.5 text-[11px] text-slate-400">
                  <ShieldCheck className="h-3.5 w-3.5 text-emerald-400" />
                  <span>公钥指纹已锁定：{pairingPreview.desktopSpkiSha256.slice(0, 16)}</span>
                </div>
                <p className="text-xs text-amber-300">
                  请确认桌面端弹窗显示完全一致的 6 位数字后，在桌面点击「同意配对」。
                </p>
              </div>
            ) : null}
          </div>
        ) : (
          <div className="space-y-4 rounded-2xl border border-slate-800 bg-slate-900/70 p-4">
            <div className="space-y-1.5">
              <label htmlFor="mobile-device-name" className="block text-xs font-medium text-slate-300">
                本机显示名称
              </label>
              <input
                id="mobile-device-name"
                type="text"
                value={deviceName}
                onChange={(e) => setDeviceName(e.target.value)}
                maxLength={80}
                className="min-h-[44px] w-full rounded-xl border border-slate-700 bg-slate-950 px-3 text-sm text-slate-100 outline-none focus:border-sky-500"
              />
            </div>

            <button
              type="button"
              onClick={() => void handleScanClick()}
              disabled={busy}
              className="flex min-h-[48px] w-full items-center justify-center gap-2 rounded-xl bg-sky-500 px-4 text-sm font-semibold text-slate-950 transition hover:bg-sky-400 disabled:opacity-50"
            >
              <Camera className="h-4 w-4" />
              <span>相机扫码配对</span>
            </button>

            <form onSubmit={(e) => void handleSubmitManual(e)} className="space-y-2.5 border-t border-slate-800 pt-3">
              <label htmlFor="manual-qr-uri" className="block text-xs font-medium text-slate-300">
                或手动粘贴桌面端复制的配对 URI（codexflow://pair?...）
              </label>
              <textarea
                id="manual-qr-uri"
                rows={3}
                value={manualUri}
                onChange={(e) => setManualUri(e.target.value)}
                placeholder="codexflow://pair?v=1&i=...&t=...&d=...&bv=1&k=...&s=..."
                className="w-full rounded-xl border border-slate-700 bg-slate-950 p-3 font-mono text-xs text-slate-100 outline-none focus:border-sky-500"
              />
              <button
                type="submit"
                disabled={busy || !manualUri.trim()}
                className="min-h-[44px] w-full rounded-xl border border-slate-700 bg-slate-800 px-4 text-sm font-medium text-slate-100 hover:bg-slate-700 disabled:opacity-50"
              >
                验证并开始配对
              </button>
            </form>

            <div className="border-t border-slate-800 pt-2">
              <button
                type="button"
                onClick={() => setShowPortalConfig((v) => !v)}
                className="text-xs text-sky-400 underline underline-offset-2"
              >
                {showPortalConfig ? '收起自建 Portal 服务器设置' : '使用自建 Portal 地址'}
              </button>
              {showPortalConfig ? (
                <div className="mt-2 space-y-1.5">
                  <label htmlFor="portal-url-input" className="block text-[11px] text-slate-400">
                    Portal 服务 Origin（默认官方网关，二维码无权覆盖此地址）
                  </label>
                  <input
                    id="portal-url-input"
                    type="url"
                    value={portalUrlDraft}
                    onChange={(e) => setPortalUrlDraft(e.target.value)}
                    className="min-h-[44px] w-full rounded-xl border border-slate-700 bg-slate-950 px-3 font-mono text-xs text-slate-200 outline-none focus:border-sky-500"
                  />
                </div>
              ) : (
                <div className="mt-1 font-mono text-[11px] text-slate-500">
                  Portal: {portalUrlDraft}
                </div>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
