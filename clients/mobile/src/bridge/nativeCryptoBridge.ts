import { Capacitor, registerPlugin } from '@capacitor/core';
import type { CodexFlowCryptoPlugin } from './nativeCryptoTypes';

export class NativeCryptoUnavailableError extends Error {
  public readonly code = 'native_crypto_unavailable';
  public constructor(message = '原生安全密钥库与签名插件在当前环境下不可用。') {
    super(message);
  }
}

const RegisteredNativeCrypto = registerPlugin<CodexFlowCryptoPlugin>('CodexFlowCrypto');

let testPluginOverride: CodexFlowCryptoPlugin | null = null;
let devFallbackPromise: Promise<CodexFlowCryptoPlugin> | null = null;

export function setTestCryptoPluginOverride(plugin: CodexFlowCryptoPlugin | null): void {
  if (import.meta.env.MODE !== 'test') {
    throw new Error('setTestCryptoPluginOverride is only permitted in test mode.');
  }
  testPluginOverride = plugin;
}

export async function getNativeCryptoPlugin(): Promise<CodexFlowCryptoPlugin> {
  if (import.meta.env.MODE === 'test' && testPluginOverride) {
    return testPluginOverride;
  }

  if (Capacitor.isNativePlatform()) {
    return RegisteredNativeCrypto;
  }

  if (import.meta.env.DEV || import.meta.env.MODE === 'test') {
    if (!devFallbackPromise) {
      devFallbackPromise = import('./webCryptoFallback').then(
        ({ WebCryptoFallbackPlugin }) => new WebCryptoFallbackPlugin(),
      );
    }
    return devFallbackPromise;
  }

  throw new NativeCryptoUnavailableError();
}
