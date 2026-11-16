import { defineConfig } from '@playwright/test';
import config from './playwright.config.mjs';

export default defineConfig(config, {
  testMatch: '**/standalone-demo.spec.mjs',
  testIgnore: [],
  metadata: { standaloneDemo: true },
});
