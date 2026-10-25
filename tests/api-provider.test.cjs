'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const { ApiProvider, normalizeBaseUrl } = require('../api-provider.cjs');
const { WorkerRouter, inspectRewrite } = require('../ai-worker.cjs');

async function fixture(handler, run) {
  const requests = [];
  const server = http.createServer(async (req, res) => {
    let input = ''; for await (const part of req) input += part;
    let body; try { body = JSON.parse(input); } catch (_) { body = null; }
    requests.push({ url: req.url, auth: req.headers.authorization, body });
    handler(req, res, body);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try { await run('http://127.0.0.1:' + server.address().port, requests); }
  finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
}
function json(res, body, status = 200) { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); }
function completion(text, finish = 'stop') { return { choices: [{ finish_reason: finish, message: { role: 'assistant', content: text } }] }; }

test('供应商切换使用独立地址、密钥及所选模型，优化与翻译分流', async () => {
  await fixture((req, res) => {
    if (req.url.endsWith('/models')) return json(res, { data: [{ id: req.url.startsWith('/a/') ? 'model-a' : 'model-b' }] });
    json(res, completion(req.url.startsWith('/a/') ? '【AI角色】保留0.5 mM。' : 'Keep 0.5 mM.'));
  }, async (base, requests) => {
    const router = new WorkerRouter();
    const a = { kind: 'openai', baseUrl: base + '/a/v1', model: 'model-a', apiKey: 'synthetic-a', reasoningEffort: 'auto' };
    const b = { kind: 'openai', baseUrl: base + '/b/v1', model: 'manual-b', apiKey: 'synthetic-b', reasoningEffort: 'medium' };
    assert.deepEqual(await router.handle({ action: 'models', provider: a }), ['model-a']);
    assert.deepEqual(await router.handle({ action: 'models', provider: b }), ['model-b']);
    assert.match(await router.handle({ action: 'optimize', provider: a, text: '保留0.5 mM。' }), /^【AI角色】/);
    assert.equal(await router.handle({ action: 'translate', provider: b, text: '保持0.5 mM。', targetLanguage: 'english' }), 'Keep 0.5 mM.');
    assert.deepEqual(requests.map(r => r.auth), ['Bearer synthetic-a', 'Bearer synthetic-b', 'Bearer synthetic-a', 'Bearer synthetic-b']);
    assert.equal(requests[2].body.model, 'model-a'); assert.equal(requests[3].body.model, 'manual-b');
    assert.ok(!('reasoning_effort' in requests[2].body)); assert.equal(requests[3].body.reasoning_effort, 'medium');
    assert.match(requests[2].body.messages[1].content, /user_draft/);
    assert.match(requests[3].body.messages[1].content, /source_text/);
    assert.doesNotMatch(requests[3].body.messages[1].content, /user_draft/);
    router.close();
  });
});

test('模型列表不受支持时仍能手动指定模型并实际调用', async () => {
  await fixture((req, res) => req.url.endsWith('/models') ? json(res, {}, 404) : json(res, completion('OK')), async base => {
    const api = new ApiProvider({ baseUrl: base + '/v1', model: 'manual-model' });
    await assert.rejects(api.models(), error => error.code === 'models_unsupported' && /手动/.test(error.message));
    assert.equal(await api.complete('Reply OK.', 'OK'), 'OK');
  });
});

test('认证、权限、额度、限流、模型及服务错误有独立提示且不回显密钥', async () => {
  const cases = [[401, '', 'authentication'], [403, '', 'permission'], [402, '', 'quota'], [429, 'insufficient_quota', 'quota'], [429, '', 'rate_limit'], [404, 'model_not_found', 'model_unavailable'], [500, '', 'server']];
  for (const [status, code, expected] of cases) {
    await fixture((req, res) => json(res, { error: { code, message: 'synthetic-secret private-source-text' } }, status), async base => {
      const api = new ApiProvider({ baseUrl: base, model: 'test', apiKey: 'synthetic-secret' });
      await assert.rejects(api.complete('x', 'y'), error => error.code === expected && !/synthetic-secret|private-source-text/.test(error.message));
    });
  }
});

test('重定向不转发 Authorization', async () => {
  await fixture((req, res) => { res.writeHead(302, { Location: '/other' }); res.end(); }, async (base, requests) => {
    const api = new ApiProvider({ baseUrl: base, apiKey: 'synthetic-key' });
    await assert.rejects(api.models(), error => error.code === 'redirect');
    assert.equal(requests.length, 1);
  });
});

test('请求超时和网络中断提示清楚', async () => {
  await fixture((req, res) => setTimeout(() => json(res, { data: [] }), 200), async base => {
    await assert.rejects(new ApiProvider({ baseUrl: base }).models(100), error => error.code === 'timeout');
  });
  const api = new ApiProvider({ baseUrl: 'https://example.com' }, async () => { throw new Error('private network details'); });
  await assert.rejects(api.models(), error => error.code === 'network' && !error.message.includes('private'));
});

test('截断、工具调用、空正文和非 JSON 响应不能作为成功结果', async () => {
  const responses = [completion('partial', 'length'), { choices: [{ finish_reason: 'stop', message: { content: 'x', tool_calls: [{}] } }] }, completion(''), {}];
  for (const response of responses) await fixture((req, res) => json(res, response), async base => {
    await assert.rejects(new ApiProvider({ baseUrl: base, model: 'test' }).complete('x', 'y'));
  });
});

test('支持文本分段响应，保留关键字面信息的检查仍生效', async () => {
  await fixture((req, res) => json(res, completion([{ type: 'text', text: '保留' }, { type: 'text', text: '0.5 mM' }])), async base => {
    const router = new WorkerRouter();
    const provider = { kind: 'openai', baseUrl: base, model: 'test' };
    assert.equal(await router.handle({ action: 'optimize', provider, text: '0.5 mM' }), '保留0.5 mM');
    const risky = await router.handle({ action: 'optimize', provider, text: '1.5 mM' });
    assert.equal(inspectRewrite('1.5 mM', risky).requiresReview, true);
  });
});

test('Base URL 规范化且拒绝远程明文、嵌入凭据和查询参数', () => {
  assert.equal(normalizeBaseUrl('https://example.com/v1/chat/completions'), 'https://example.com/v1');
  assert.equal(normalizeBaseUrl('http://localhost:11434/v1/'), 'http://localhost:11434/v1');
  for (const url of ['http://example.com/v1', 'https://user:pass@example.com/v1', 'https://example.com/v1?key=secret', 'file:///tmp']) assert.throws(() => normalizeBaseUrl(url));
});

test('测试连接确实调用所选模型，不发送用户草稿', async () => {
  await fixture((req, res) => json(res, completion('OK')), async (base, requests) => {
    const router = new WorkerRouter();
    const result = await router.handle({ action: 'test', provider: { kind: 'openai', baseUrl: base, model: 'chosen-model' }, text: 'private-draft' });
    assert.equal(result.connected, true); assert.equal(requests[0].body.model, 'chosen-model');
    assert.ok(!JSON.stringify(requests[0].body).includes('private-draft'));
  });
});

test('未知操作、供应商类型和空模型不发起 API 请求', async () => {
  const router = new WorkerRouter();
  await assert.rejects(router.handle({ action: 'bad', provider: { kind: 'openai' } }), /不支持/);
  await assert.rejects(router.handle({ action: 'test', provider: { kind: 'other' } }), /不支持/);
  const api = new ApiProvider({ baseUrl: 'https://example.com' }, async () => { throw new Error('must not fetch'); });
  await assert.rejects(api.complete('x', 'y'), error => error.code === 'missing_model');
});
