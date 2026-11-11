import { test, captureLiveAnimation, openDashboard } from '../fixtures.mjs';

test('README overview', async ({ page }) => {
  await openDashboard(page);
  await page.locator('.recharts-area-curve').first().waitFor();
  await captureLiveAnimation(page, 'overview.gif', { seconds: 6, fps: 25 });
});
