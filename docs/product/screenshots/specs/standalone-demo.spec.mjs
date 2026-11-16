import { expect } from '@playwright/test';
import { test, captureLiveAnimation, capturePage, openDashboard } from '../fixtures.mjs';

test.use({ viewport: { width: 1440, height: 650 } });

test('Standalone demo: follow your first request', async ({ page }) => {
  await openDashboard(page, '/?host=sounding-demo&container=sounding-demo-client-1&view=network');
  const historyReady = page.waitForResponse(response => {
    const url = new URL(response.url());
    return url.pathname === '/api/v1/containers/sounding-demo-client-1/history'
      && url.searchParams.get('seconds') === '60' && response.status() === 200;
  });
  await page.getByRole('button', { name: '1m', exact: true }).click();
  await (await historyReady).finished();

  const connections = page.getByRole('table').filter({ has: page.getByText('Destination', { exact: true }) });
  const requests = page.getByRole('table').filter({ has: page.getByText('Talking to', { exact: true }) });
  const request = requests.getByRole('row').filter({ hasText: 'GET /' }).filter({ hasText: 'web' });
  await expect(page.getByRole('banner')).toContainText('demo-host');
  await expect(page.getByRole('banner').getByText('client-1', { exact: true })).toBeVisible();
  await expect(connections.getByRole('row').filter({ hasText: 'web' })).toContainText('GET /');
  await expect(request).toHaveCount(1);
  await expect(request).toContainText('web:80');
  await expect(request).toContainText('0.5/s');
  await capturePage(page, 'standalone-demo-request.png');
});

test('Standalone demo: watch packets move on the network map', async ({ page }) => {
  await openDashboard(page, '/?host=sounding-demo&view=network');
  const map = page.locator('.react-flow');
  await map.scrollIntoViewIfNeeded();
  await map.locator('xpath=ancestor::section').getByTitle('Use the whole page').click();
  await page.locator('.panel-maximized').waitFor();
  await expect(map.getByText('client-1', { exact: true })).toBeVisible();
  await expect(map.getByText('web-1', { exact: true })).toBeVisible();

  const packet = map.locator('.map-packet').first();
  await expect(packet).toBeVisible();
  await expect.poll(() => packet.evaluate(element =>
    element.getAnimations().some(animation =>
      animation.playState === 'running' && animation.playbackRate > 0))).toBe(true);
  const initialPosition = await packet.evaluate(element => getComputedStyle(element).transform);
  await expect.poll(() => packet.evaluate(element => getComputedStyle(element).transform)).not.toBe(initialPosition);
  await captureLiveAnimation(page, 'standalone-demo-map.gif', { seconds: 6, fps: 25 });
});
