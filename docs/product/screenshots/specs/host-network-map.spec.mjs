import { expect } from '@playwright/test';
import { test, captureAnimation, openDashboard } from '../fixtures.mjs';

test('Host network map', async ({ page }) => {
  await openDashboard(page, '/?host=demo-edge&view=network');
  const map = page.locator('.react-flow');
  await map.scrollIntoViewIfNeeded();
  await map.locator('xpath=ancestor::section').getByTitle('Use the whole page').click();
  await page.locator('.panel-maximized').waitFor();
  await map.locator('.map-packet').first().waitFor();
  await expect(map.getByText('postgres-1', { exact: true })).toBeAttached();
  const response = await page.request.get('/api/network');
  const snapshot = await response.json();
  await page.route('**/api/network', route => route.fulfill({ json: snapshot }));
  await captureAnimation(page, 'host-network-map.gif', {
    frames: 50,
    fps: 25,
    beforeFrame: (frame, frames) => map.locator('.map-packet').evaluateAll((packets, progress) => {
      for (const [index, packet] of packets.entries()) {
        const animation = packet.getAnimations()[0];
        if (!animation) continue;
        animation.pause();
        const duration = animation.effect.getTiming().duration;
        animation.currentTime = ((0.12 + index % 4 * 0.25 + progress / 2) % 1) * duration;
      }
    }, frame / frames),
  });
});
