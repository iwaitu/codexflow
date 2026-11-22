import { useEffect } from 'react';
import { Capacitor } from '@capacitor/core';
import { Bell, LogOut, ShieldCheck, SlidersHorizontal, Unplug } from 'lucide-react';
import { useMobileStore } from '@/store/useMobileStore';

const PERMISSION_MODE_OPTIONS = [
  { value: 'Ask', label: '每次询问 (Ask)' },
  { value: 'AutoInWorkspace', label: '工作区内自动执行 (AutoInWorkspace)' },
  { value: 'FullAuto', label: '完全自动 (FullAuto)' },
  { value: 'Deny', label: '全部拒绝 (Deny)' },
] as const;

export function SettingsView() {
  const pairingState = useMobileStore((s) => s.pairingState);
  const executionSettings = useMobileStore((s) => s.executionSettings);
  const desktopOnline = useMobileStore((s) => s.desktopOnline);
  const loadExecutionSettings = useMobileStore((s) => s.loadExecutionSettings);
  const updatePermissionMode = useMobileStore((s) => s.updatePermissionMode);
  const toggleApnsPush = useMobileStore((s) => s.toggleApnsPush);
  const logoutAndUnpair = useMobileStore((s) => s.logoutAndUnpair);

  useEffect(() => {
    if (pairingState && executionSettings === null) {
      void loadExecutionSettings();
    }
  }, [pairingState, executionSettings, loadExecutionSettings]);

  const platform = Capacitor.getPlatform();
  const isIosPlatform = platform === 'ios';

  return (
    <div className="flex flex-1 flex-col overflow-y-auto px-4 py-4">
      <div className="mx-auto w-full max-w-2xl space-y-4">
        <section className="space-y-3 rounded-2xl border border-slate-800 bg-slate-900/80 p-4">
          <div className="flex items-center gap-2 text-sm font-semibold text-slate-100">
            <ShieldCheck className="h-4 w-4 text-emerald-400" />
            <span>已配对桌面端与设备身份</span>
          </div>

          <dl className="grid grid-cols-1 gap-2 text-xs sm:grid-cols-2">
            <div className="rounded-xl bg-slate-950/70 p-2.5">
              <dt className="text-slate-400">桌面工作站名称</dt>
              <dd className="mt-0.5 font-medium text-slate-100">
                {pairingState?.desktopName || '桌面端'}（{desktopOnline === false ? '离线' : '已绑定'}）
              </dd>
            </div>
            <div className="rounded-xl bg-slate-950/70 p-2.5">
              <dt className="text-slate-400">设备 ID 与绑定代次</dt>
              <dd className="mt-0.5 font-mono text-slate-200">
                {pairingState?.deviceId?.slice(0, 12) ?? '-'} · bv={pairingState?.bindingVersion ?? '-'}
              </dd>
            </div>
            <div className="rounded-xl bg-slate-950/70 p-2.5 sm:col-span-2">
              <dt className="text-slate-400">桌面 SPKI 指纹（SHA-256）</dt>
              <dd className="mt-0.5 break-all font-mono text-[11px] text-emerald-300">
                {pairingState?.desktopSpkiSha256 ?? '-'}
              </dd>
            </div>
            <div className="rounded-xl bg-slate-950/70 p-2.5 sm:col-span-2">
              <dt className="text-slate-400">中继 Portal 地址</dt>
              <dd className="mt-0.5 break-all font-mono text-[11px] text-slate-300">
                {pairingState?.portalUrl ?? '-'}
              </dd>
            </div>
          </dl>

          <div className="text-xs text-slate-400">
            生物识别签批能力：
            <span className={pairingState?.biometricEnrolled ? 'text-emerald-400' : 'text-amber-300'}>
              {pairingState?.biometricEnrolled ? ' 已启用 P-256 硬件私钥' : ' 未启用（高危审批受限）'}
            </span>
          </div>
        </section>

        <section className="space-y-3 rounded-2xl border border-slate-800 bg-slate-900/80 p-4">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2 text-sm font-semibold text-slate-100">
              <SlidersHorizontal className="h-4 w-4 text-sky-400" />
              <span>桌面执行权限策略</span>
            </div>
            <span className="text-[11px] text-slate-400">
              后端：{executionSettings?.backend ?? '加载中'}
            </span>
          </div>

          <label className="block space-y-1.5">
            <span className="text-xs text-slate-400">默认命令执行审批模式</span>
            <select
              aria-label="桌面执行权限模式"
              value={executionSettings?.permissionMode ?? 'Ask'}
              disabled={!executionSettings}
              onChange={(e) => void updatePermissionMode(e.target.value)}
              className="min-h-[44px] w-full rounded-xl border border-slate-700 bg-slate-950 px-3 text-sm text-slate-100 focus:border-sky-500 focus:outline-none disabled:opacity-50"
            >
              {PERMISSION_MODE_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
          </label>
        </section>

        <section className="space-y-3 rounded-2xl border border-slate-800 bg-slate-900/80 p-4">
          <div className="flex items-center gap-2 text-sm font-semibold text-slate-100">
            <Bell className="h-4 w-4 text-amber-400" />
            <span>通知与待审批提醒</span>
          </div>

          {isIosPlatform ? (
            <div className="flex items-center justify-between gap-3 rounded-xl bg-slate-950/70 p-3">
              <div>
                <div className="text-xs font-medium text-slate-200">iOS APNs 静默唤醒提醒</div>
                <div className="mt-0.5 text-[11px] text-slate-400">
                  仅通过 Portal 传递“有待处理审批”唤醒信号，绝不发送对话正文或工作区内容。
                </div>
              </div>
              <button
                type="button"
                role="switch"
                aria-checked={Boolean(pairingState?.apnsPushEnabled)}
                aria-label="开启或关闭 iOS APNs 推送"
                onClick={() => void toggleApnsPush(!pairingState?.apnsPushEnabled)}
                className={`relative h-7 w-12 shrink-0 rounded-full transition-colors ${
                  pairingState?.apnsPushEnabled ? 'bg-emerald-500' : 'bg-slate-700'
                }`}
              >
                <span
                  className={`block h-5 w-5 translate-y-1 rounded-full bg-white transition-transform ${
                    pairingState?.apnsPushEnabled ? 'translate-x-6' : 'translate-x-1'
                  }`}
                />
              </button>
            </div>
          ) : (
            <div className="space-y-2 rounded-xl bg-slate-950/70 p-3 text-xs text-slate-300">
              <div className="font-medium text-slate-200">
                Android 无依赖推送说明（不集成 GMS / FCM）
              </div>
              <p className="leading-relaxed text-slate-400">
                Android 端默认保持零后台第三方推送 SDK，打开或切换回前台时自动通过增量事件游标拉取待审批列表。如需离线即时触达，可在桌面端「设置 → Webhook 通知」配置企业微信、飞书、钉钉或 Bark 通知通道。
              </p>
              {platform === 'web' ? (
                <div className="flex items-center justify-between border-t border-slate-800 pt-2">
                  <span className="text-[11px] text-slate-400">iOS 原生设备 APNs 开关预览：</span>
                  <button
                    type="button"
                    role="switch"
                    aria-checked={Boolean(pairingState?.apnsPushEnabled)}
                    aria-label="开启或关闭 APNs 推送"
                    onClick={() => void toggleApnsPush(!pairingState?.apnsPushEnabled)}
                    className={`rounded-lg px-2.5 py-1 text-[11px] font-medium ${
                      pairingState?.apnsPushEnabled
                        ? 'bg-emerald-500/20 text-emerald-300'
                        : 'bg-slate-800 text-slate-300'
                    }`}
                  >
                    {pairingState?.apnsPushEnabled ? 'APNs 已开启' : 'APNs 已关闭'}
                  </button>
                </div>
              ) : null}
            </div>
          )}
        </section>

        <section className="rounded-2xl border border-rose-500/30 bg-rose-950/20 p-4">
          <div className="mb-2 flex items-center gap-2 text-sm font-semibold text-rose-200">
            <Unplug className="h-4 w-4 text-rose-400" />
            <span>设备安全解绑</span>
          </div>
          <p className="mb-3 text-xs leading-relaxed text-rose-200/80">
            退出后将通知中继服务端吊销当前配对、清除 APNs Token，并彻底销毁手机 Keychain / Keystore 中的 P-256 私钥。
          </p>
          <button
            type="button"
            onClick={() => void logoutAndUnpair()}
            className="flex min-h-[44px] w-full items-center justify-center gap-2 rounded-xl bg-rose-600 px-4 text-sm font-semibold text-white hover:bg-rose-500"
          >
            <LogOut className="h-4 w-4" />
            <span>退出并解绑设备</span>
          </button>
        </section>
      </div>
    </div>
  );
}
