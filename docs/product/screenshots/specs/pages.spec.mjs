import { test, captureAnimation, capturePage, openDashboard } from '../fixtures.mjs';

// Screenshots for the documentation pages: each illustrates one thing a page explains.

// A chart or panel, by its title.
const panel = (page, title) => page.locator('section').filter({ has: page.getByText(title, { exact: true }) }).last();
const container = (host, name, view) => `/?host=${host}&container=${host}-${name}&view=${view}`;

test('Host overview: limits, other and free', async ({ page }) => {
  await openDashboard(page, '/?host=demo-edge');
  await capturePage(page, 'host-overview.png');
});

test('Chart help', async ({ page }) => {
  await openDashboard(page, container('demo-edge', 'api-1', 'cpu'));
  // Focus opens the tooltip and, unlike hovering, keeps it open when the capture parks the mouse.
  await panel(page, 'CPU throttling').getByRole('button', { name: 'What this shows' }).focus();
  await page.getByRole('tooltip').waitFor();
  await capturePage(page, 'chart-help.png');
});

test('Hovering a chart follows every chart', async ({ page }) => {
  await openDashboard(page, container('demo-edge', 'api-1', 'cpu'));
  const pressure = panel(page, 'CPU pressure');
  await pressure.locator('.recharts-area-curve').first().waitFor();
  // The plot itself: right of the y-axis labels, inside the chart's wrapper.
  const chart = await pressure.locator('.recharts-wrapper').boundingBox();
  const left = chart.x + 70, width = chart.width - 80;
  await captureAnimation(page, 'chart-hover.gif', {
    frames: 40,
    fps: 10,
    beforeFrame: async (frame, frames) => {
      await page.mouse.move(left + width * (0.05 + 0.9 * frame / (frames - 1)), chart.y + chart.height / 2);
    },
  });
});

test('Disk space: folders, files and deleted files', async ({ page }) => {
  await openDashboard(page, container('demo-db', 'postgres-1', 'disk'));
  const folders = panel(page, 'Largest folders');
  await folders.waitFor();
  await folders.evaluate(element => element.scrollIntoView({ block: 'start' }));
  await capturePage(page, 'container-disk-space.png');
});

test('Host events and output', async ({ page }) => {
  await openDashboard(page, '/?host=demo-edge&view=events');
  await panel(page, 'Events').waitFor();
  await capturePage(page, 'host-events.png');
});

test('Packet capture, opened', async ({ page }) => {
  await openDashboard(page, container('demo-edge', 'api-1', 'network'));
  const capture = panel(page, 'Packet capture');
  await capture.scrollIntoViewIfNeeded();
  await capture.getByRole('button', { name: 'Open' }).click();
  await capture.locator('tr', { hasText: 'GET /api/orders HTTP/1.1' }).click();
  await capture.getByTitle('Use the whole page').click();
  await page.locator('.panel-maximized').waitFor();
  await capturePage(page, 'packet-capture.png');
});
