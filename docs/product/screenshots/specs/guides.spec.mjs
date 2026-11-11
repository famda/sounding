import { test, capturePage, openDashboard } from '../fixtures.mjs';

test('Choose a host or container', async ({ page }) => {
  await openDashboard(page);
  await page.getByTitle('Switch host or container (/)').click();
  await page.getByRole('dialog', { name: 'Host and container picker' }).waitFor();
  await capturePage(page, 'choose-scope.png');
});

for (const shot of [
  { title: 'Container CPU', host: 'demo-edge', container: 'demo-edge-api-1', view: 'cpu', file: 'container-cpu.png' },
  { title: 'Container memory', host: 'demo-edge', container: 'demo-edge-api-1', view: 'memory', file: 'container-memory.png' },
  { title: 'Host GPU', host: 'demo-gpu', view: 'gpu', file: 'host-gpu.png' },
  { title: 'Container network', host: 'demo-edge', container: 'demo-edge-api-1', view: 'network', file: 'container-network.png' },
  { title: 'Container disk', host: 'demo-db', container: 'demo-db-postgres-1', view: 'disk', file: 'container-disk.png' },
]) {
  test(shot.title, async ({ page }) => {
    const query = new URLSearchParams({ host: shot.host, view: shot.view });
    if (shot.container) query.set('container', shot.container);
    await openDashboard(page, `/?${query}`);
    await page.locator('.recharts-area-curve').first().waitFor();
    await capturePage(page, shot.file);
  });
}
