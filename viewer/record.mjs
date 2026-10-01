// Renders viewer/index.html frame by frame with headless Chromium and writes JPEGs to frames/.
// Usage: node record.mjs [frameList|all] ; node record.mjs cards
import { chromium } from 'playwright';
import http from 'http'; import fs from 'fs'; import path from 'path';
const dir = path.dirname(new URL(import.meta.url).pathname);
const types = { '.html': 'text/html', '.js': 'text/javascript', '.json': 'application/json' };
const server = http.createServer((q, r) => {
  const rel = decodeURIComponent(q.url.split('?')[0]);
  const f = path.join(dir, rel === '/' ? 'index.html' : rel);
  fs.readFile(f, (e, d) => { if (e) { r.writeHead(404); r.end(); return; } r.writeHead(200, { 'Content-Type': types[path.extname(f)] || 'application/octet-stream' }); r.end(d); });
}).listen(8793);
const browser = await chromium.launch({ args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
const errors = []; page.on('pageerror', e => errors.push(String(e))); page.on('console', m => m.type() === 'error' && errors.push(m.text()));
await page.goto('http://127.0.0.1:8793/');
await page.waitForFunction(() => window.viewerReady === true, null, { timeout: 120000 });
fs.mkdirSync(path.join(dir, 'frames'), { recursive: true });
const arg = process.argv[2] || 'all';
if (arg === 'cards') {
  await page.evaluate(() => window.renderFrame(0));
  for (const id of ['intro', 'outro']) {
    await page.evaluate(id => { document.querySelectorAll('.card').forEach(c => c.style.display = 'none'); window.showCard(id); }, id);
    await page.locator('#stage').screenshot({ path: path.join(dir, 'frames', `${id}.png`) });
  }
} else {
  const n = await page.evaluate(() => window.frameCount);
  const list = arg === 'all' ? [...Array(n).keys()] : arg.split(',').map(Number);
  const t0 = Date.now();
  for (const i of list) {
    await page.evaluate(i => window.renderFrame(i), i);
    await page.locator('#stage').screenshot({ path: path.join(dir, 'frames', `f${String(i).padStart(5, '0')}.jpg`), type: 'jpeg', quality: 90 });
    if (i % 200 === 0) console.log(`frame ${i}/${n} ${((Date.now() - t0) / 1000).toFixed(0)}s`);
  }
}
console.log('errors:', errors.length ? errors : 'none');
await browser.close(); server.close();
