'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { spawn } = require('node:child_process');
const assert = require('node:assert/strict');
const app = path.resolve(__dirname, '..');
const output = path.join(app, 'review-ui-update');
const fixture = path.join(output, 'fixture-' + Date.now());
fs.mkdirSync(fixture, { recursive: true });
for (const file of ['ai-worker.cjs', 'api-provider.cjs', '提示词优化专用指令.txt', 'prompt.ico']) fs.copyFileSync(path.join(app, file), path.join(fixture, file));
const requests = []; let canceled = 0;
function event(res, payload) { if (!res.destroyed) res.write('data: ' + (payload === '[DONE]' ? payload : JSON.stringify(payload)) + '\n\n'); }
function delta(res, content) { event(res, { choices: [{ index: 0, delta: { content }, finish_reason: null }] }); }
function finish(res) { if (res.destroyed) return; event(res, { choices: [{ index: 0, delta: {}, finish_reason: 'stop' }] }); event(res, '[DONE]'); res.end(); }
const server = http.createServer(async (req, res) => {
  let raw = ''; for await (const chunk of req) raw += chunk;
  let body; try { body = raw ? JSON.parse(raw) : null; } catch (_) {}
  requests.push({ path: req.url, model: body?.model, stream: body?.stream, auth: req.headers.authorization });
  req.on('aborted', () => canceled++);
  res.on('close', () => { if (!res.writableEnded) canceled++; });
  if (req.url.startsWith('/slow/')) {
    setTimeout(() => {
      if (res.destroyed) return;
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(req.url.endsWith('/models') ? { data: [{ id: 'ui-test' }] } : { choices: [{ finish_reason: 'stop', message: { content: 'OK' } }] }));
    }, 2500);
    return;
  }
  if (req.url.endsWith('/models')) { res.writeHead(200, { 'Content-Type': 'application/json' }); return res.end(JSON.stringify({ data: [{ id: 'ui-test' }] })); }
  res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' }); res.flushHeaders();
  const source = body?.messages?.[1]?.content || '';
  if (source.includes('慢请求')) {
    delta(res, '慢预览 ');
    setTimeout(() => delta(res, '旧片段 不应出现'), 1200);
    setTimeout(() => finish(res), 2400);
  } else if (source.includes('风险样例')) {
    delta(res, '浓度4 mM，');
    setTimeout(() => delta(res, '温度0.5 °C，加热。'), 180);
    setTimeout(() => finish(res), 400);
  } else {
    delta(res, '请保留');
    setTimeout(() => delta(res, '0.5 mM，不要加热。'), 180);
    setTimeout(() => finish(res), 400);
  }
});
function run(executable, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(executable, args, { cwd: output, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let detail = ''; child.stderr.on('data', data => { detail += data; });
    const timer = setTimeout(() => { child.kill(); reject(new Error('Main UI smoke timed out')); }, 60000);
    child.on('error', error => { clearTimeout(timer); reject(error); });
    child.on('exit', code => { clearTimeout(timer); code === 0 ? resolve() : reject(new Error('Main UI smoke failed (' + code + '): ' + detail)); });
  });
}
(async () => {
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const reportPath = path.join(fixture, 'report.json');
    await run(path.join(output, 'MainUiReviewSmoke.exe'), [fixture, 'http://127.0.0.1:' + server.address().port, reportPath, path.join(app, 'tests', 'EditorFixture.exe')]);
    const report = JSON.parse(fs.readFileSync(reportPath, 'utf8'));
    assert.equal(report.ok, true);
    assert.ok(canceled >= 4, 'Expected canceled HTTP connections from main and provider dialogs');
    assert.ok(requests.filter(r => r.model).every(r => r.model === 'ui-test'), 'Wrong model reached mock API');
    assert.ok(requests.every(r => r.auth === 'Bearer synthetic-ui-key'), 'Unexpected credential reached mock API');
    const summary = { ...report, fixture, requests: requests.length, canceledConnections: canceled, actualApiRouting: true };
    fs.writeFileSync(path.join(app, 'main-ui-review-smoke.json'), JSON.stringify(summary, null, 2));
    process.stdout.write(JSON.stringify({ ...summary, screenshot: path.join(fixture, 'main-review.png') }));
  } catch (error) {
    const report = path.join(fixture, 'report.json'); if (fs.existsSync(report)) process.stderr.write(fs.readFileSync(report, 'utf8') + '\n');
    process.stderr.write(error.stack + '\n'); process.exitCode = 1;
  } finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
})();
