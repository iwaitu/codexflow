import type { CapacitorConfig } from '@capacitor/cli';

const config: CapacitorConfig = {
  appId: 'dev.codexflow.mobile',
  appName: 'CodexFlow',
  webDir: 'dist',
  server: {
    hostname: 'localhost',
    iosScheme: 'capacitor',
    androidScheme: 'https',
    cleartext: false,
  },
  android: {
    includePlugins: [
      '@capacitor/app',
      '@capacitor/haptics',
      '@capacitor/keyboard',
      '@capacitor/network',
      '@capacitor/status-bar',
    ],
  },
  plugins: {
    CapacitorHttp: {
      enabled: false,
    },
    PushNotifications: {
      presentationOptions: ['badge', 'sound', 'alert'],
    },
  },
};

export default config;
