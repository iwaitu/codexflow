import { Capacitor, type PluginListenerHandle } from '@capacitor/core';
import { PushNotifications } from '@capacitor/push-notifications';
import type { RelayClient } from './relayClient';

export class IosPushTokenManager {
  private regListener: PluginListenerHandle | null = null;
  private errListener: PluginListenerHandle | null = null;

  public async setApnsEnabled(params: {
    relayClient: RelayClient;
    enabled: boolean;
  }): Promise<boolean> {
    if (!params.enabled) {
      await this.disposeListeners();
      await params.relayClient.updateApnsPushToken({
        pushPlatform: null,
        pushToken: null,
      });
      return false;
    }

    if (!Capacitor.isNativePlatform() || Capacitor.getPlatform() !== 'ios') {
      throw new Error('仅 iOS 真机设备支持 APNs 静默唤醒推送注册。');
    }

    const perm = await PushNotifications.requestPermissions();
    if (perm.receive !== 'granted') {
      throw new Error('未获得系统推送通知权限。');
    }

    await this.disposeListeners();
    await new Promise<void>((resolve, reject) => {
      const timeout = setTimeout(() => {
        reject(new Error('等待 iOS APNs Token 注册超时。'));
      }, 15_000);

      void PushNotifications.addListener('registration', async (token) => {
        clearTimeout(timeout);
        try {
          await params.relayClient.updateApnsPushToken({
            pushPlatform: 'apns',
            pushToken: token.value,
          });
          resolve();
        } catch (err) {
          reject(err);
        }
      }).then((h) => {
        this.regListener = h;
      });

      void PushNotifications.addListener('registrationError', (err) => {
        clearTimeout(timeout);
        reject(new Error(err.error || 'APNs 注册失败。'));
      }).then((h) => {
        this.errListener = h;
      });

      void PushNotifications.register();
    });

    return true;
  }

  public async disposeListeners(): Promise<void> {
    if (this.regListener) {
      await this.regListener.remove();
      this.regListener = null;
    }
    if (this.errListener) {
      await this.errListener.remove();
      this.errListener = null;
    }
  }
}
