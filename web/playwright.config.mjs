import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: './tests', workers: 1, reporter: 'list',
  outputDir: '../artifacts/browser-tests',
  use: { channel: 'msedge', headless: true, viewport: { width: 1000, height: 600 } }
});
