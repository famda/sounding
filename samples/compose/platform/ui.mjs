// The shop's web front end: renders the catalog server-side from the catalog API.
import http from 'node:http';

const catalog = process.env.CATALOG_URL ?? 'http://catalog-api:8080';

http.createServer(async (req, res) => {
  if (req.url !== '/') {
    res.writeHead(404).end();
    return;
  }
  try {
    const reply = await fetch(`${catalog}/api/catalog/items`, { signal: AbortSignal.timeout(5000) });
    if (!reply.ok) throw new Error(`catalog answered ${reply.status}`);
    const items = await reply.json();
    const rows = items.map(i => `<li>${i.name}: ${i.price.toFixed(2)} €</li>`).join('');
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' })
      .end(`<!doctype html><title>Shop</title><h1>Shop</h1><ul>${rows}</ul>`);
  } catch (error) {
    res.writeHead(502, { 'content-type': 'text/plain' }).end(String(error));
  }
}).listen(3000);
