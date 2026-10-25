'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { spawn } = require('node:child_process');
const assert = require('node:assert/strict');
const app = path.resolve(__dirname, '..');
const out = path.join(app, 'provider-update');
const fixture = path.join(out, 'ui-fixture-' + Date.now());
fs.mkdirSync(fixture, { recursive: true });
for (const name of ['ai-worker.cjs', 'api-provider.cjs', '提示词优化专用指令.txt']) fs.copyFileSync(path.join(app, name), path.join(fixture, name));
const requests = [];
function json(res, value, status = 200) { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); }
const server = http.createServer(async (req, res) => {
  let input = ''; for await (const part of req) input += part;
  const body = input ? JSON.parse(input) : null;
  requests.push({ path: req.url, auth: req.headers.authorization, model: body?.model, action: body?.messages?.[1]?.content.includes('source_text') ? 'translate' : body?.messages?.[1]?.content.includes('user_draft') ? 'optimize' : 'test' });
  if (req.headers.authorization === 'Bearer synthetic-wrong') return json(res, { error: { code: 'invalid_api_key' } }, 401);
  if (req.url.startsWith('/manual/') && req.url.endsWith('/models')) return json(res, {}, 404);
  if (req.url.endsWith('/models')) return json(res, { data: [{ id: req.url.startsWith('/a/') ? 'model-a' : 'model-b' }] });
  const text = body.messages[1].content.includes('source_text') ? 'Keep 0.5 mM.' : body.messages[1].content.includes('user_draft') ? '【AI角色】provider-b，保留0.5 mM。' : 'OK';
  json(res, { choices: [{ finish_reason: 'stop', message: { role: 'assistant', content: text } }] });
});
function run(exe, args) {
  return new Promise((resolve, reject) => {
    const process = spawn(exe, args, { cwd: out, windowsHide: true, stdio: 'pipe' });
    let error = ''; process.stderr.on('data', data => { error += data; });
    process.on('error', reject); process.on('exit', code => code === 0 ? resolve() : reject(new Error('UI fixture failed (' + code + '): ' + error)));
  });
}
(async () => {
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const reportPath = path.join(fixture, 'report.json');
    await run(path.join(out, 'ProviderUiSmoke.exe'), [fixture, 'http://127.0.0.1:' + server.address().port, reportPath]);
    const report = JSON.parse(fs.readFileSync(reportPath, 'utf8'));
    assert.equal(report.ok, true);
    const last = requests.filter(r => r.action === 'optimize' || r.action === 'translate');
    assert.equal(last.length, 2);
    for (const item of last) { assert.ok(item.path.startsWith('/b/')); assert.equal(item.model, 'manual-b'); assert.equal(item.auth, 'Bearer synthetic-new-b'); }
    fs.writeFileSync(path.join(app, 'provider-ui-smoke.json'), JSON.stringify({ ...report, fixture, requestCount: requests.length, actualRoutingVerified: true }, null, 2));
    process.stdout.write(JSON.stringify({ ...report, actualRoutingVerified: true, screenshot: path.join(fixture, 'provider-settings.png') }));
  } catch (error) {
    // Fixture contains synthetic credentials only. Keep report for diagnosis.
    const reportPath = path.join(fixture, 'report.json');
    if (fs.existsSync(reportPath)) process.stderr.write(fs.readFileSync(reportPath, 'utf8'));
    process.stderr.write(error.stack); process.exitCode = 1;
  } finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
})();
