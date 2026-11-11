import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './specs',
  testMatch: '**/*.spec.mjs',
  globalSetup: './setup.mjs',
  outputDir: '/tmp/playwright-results',
  forbidOnly: true,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  reporter: 'list',
  projects: [
    { name: 'dark', use: { colorScheme: 'dark' } },
    { name: 'light', use: { colorScheme: 'light' } },
  ],
  use: {
    baseURL: 'http://dashboard:8080',
    browserName: 'chromium',
    headless: true,
    viewport: { width: 1440, height: 900 },
    reducedMotion: 'reduce',
    locale: 'en-US',
    timezoneId: 'UTC',
  },
});
