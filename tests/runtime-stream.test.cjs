'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const path = require('node:path');
const readline = require('node:readline');
const { spawn } = require('node:child_process');
const { ApiProvider } = require('../api-provider.cjs');
const { CodexBridge, inspectRewrite, validateRewrite } = require('../ai-worker.cjs');

async function fixture(handler, run) {
  const requests = [];
  const server = http.createServer(async (req, res) => {
    let input = ''; for await (const part of req) input += part;
    const body = input ? JSON.parse(input) : null;
    requests.push({ path: req.url, body });
    handler(req, res, body);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try { await run('http://127.0.0.1:' + server.address().port, requests); }
  finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
}
function json(res, value, status = 200) { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); }
function frame(text) { return 'data: ' + JSON.stringify({ choices: [{ delta: { content: text }, finish_reason: null }] }) + '\n\n'; }
const ending = 'data: {"choices":[{"delta":{},"finish_reason":"stop"}]}\n\ndata: [DONE]\n\n';
async function waitUntil(predicate, timeout = 2500) {
  const deadline = Date.now() + timeout;
  while (!predicate()) { if (Date.now() > deadline) throw new Error('Synthetic fixture timed out'); await new Promise(resolve => setTimeout(resolve, 5)); }
}

test('关键差异覆盖数值单位互换、符号、次数和否定/授权边界', () => {
  const source = '浓度0.5 mM，温度4 °C，不要加热。';
  const review = inspectRewrite(source, '浓度4 mM，温度0.5 °C，加热。');
  assert.equal(review.requiresReview, true);
  assert.ok(review.reviewWarnings.some(text => text.includes('单位')));
  assert.ok(review.reviewWarnings.some(text => text.includes('否定')));
  assert.equal(inspectRewrite('保持-5 mV。', '保持5 mV。').requiresReview, true);
  assert.equal(inspectRewrite('5个样品与5个对照。', '5个样品与对照。').requiresReview, true);
  assert.equal(inspectRewrite('确认后再修改，不要上传。', '直接修改，不要上传。').requiresReview, true);
  assert.equal(inspectRewrite('确认后再修改代码。', '先修改代码，再确认。').requiresReview, true);
  assert.equal(inspectRewrite('确认后再修改代码。', '无需确认，直接修改代码。').requiresReview, true);
  assert.equal(inspectRewrite('不应加热，不准上传，不允许删除。', '加热，上传，删除。').requiresReview, true);
  assert.equal(inspectRewrite('不要上传。', '不要加热。').requiresReview, true);
  assert.equal(inspectRewrite('Do not upload the file.', 'Do not heat the file.').requiresReview, true);
  assert.equal(inspectRewrite('5篇文献。', '15篇文献。').requiresReview, true);
  assert.throws(() => validateRewrite(source, '浓度4 mM，温度0.5 °C，加热。'), /关键内容/);
});

test('等价否定和正常中英翻译不因词面不同全量告警', () => {
  const source = '浓度0.5 mM，温度4 °C，不要加热。';
  assert.equal(inspectRewrite(source, '保持浓度0.5 mM和温度4 °C，禁止加热。').requiresReview, false);
  assert.equal(inspectRewrite(source, 'Keep 0.5 mM at 4 °C; do not heat.', { translation: true }).requiresReview, false);
  assert.equal(inspectRewrite('Do not heat 5 mL.', '不要加热5 mL。', { translation: true }).requiresReview, false);
  assert.equal(inspectRewrite('Do not heat 5 mL.', '加热5 mL。', { translation: true }).requiresReview, true);
});

test('结构编号不改变数值检查，路径链接改变仍要求预览', () => {
  assert.equal(inspectRewrite('查找5篇文献。', '1. 查找5篇文献。').requiresReview, false);
  assert.equal(inspectRewrite('浓度1.5 mM。', '浓度1.5 mM。').requiresReview, false);
  assert.equal(inspectRewrite('读取 E:\\data\\a.csv；https://example.com/a', '读取 E:\\data\\b.csv；https://example.com/b').requiresReview, true);
  assert.equal(inspectRewrite('读取 E:/data/a.csv；DOI:10.1234/example', '读取 E:/data/b.csv；DOI:10.1234/changed').requiresReview, true);
});

test('SSE 分块、UTF-8 和完整结束只发布可见正文', async () => {
  await fixture((req, res) => {
    res.writeHead(200, { 'Content-Type': 'text/event-stream' });
    const hidden = 'data: {"choices":[{"delta":{"reasoning_content":"private reasoning"},"finish_reason":null}]}\r\n\r\n';
    const bytes = Buffer.from(hidden + frame('保持0.5 ') + frame('mM，不要加热。') + ending);
    const split = bytes.indexOf(Buffer.from('保持')) + 1;
    res.write(bytes.subarray(0, split));
    setTimeout(() => res.end(bytes.subarray(split)), 10);
  }, async (base, requests) => {
    const deltas = [];
    const output = await new ApiProvider({ baseUrl: base, model: 'synthetic' }).complete('x', 'y', 1000, { onDelta: text => deltas.push(text) });
    assert.equal(requests[0].body.stream, true);
    assert.equal(output, '保持0.5 mM，不要加热。');
    assert.equal(deltas.join(''), output);
    assert.ok(!deltas.join('').includes('private reasoning'));
  });
});

test('SSE 未完整结束、截断或工具调用不返回可填回结果', async () => {
  const bodies = [
    frame('partial'),
    frame('partial') + 'data: [DONE]\n\n',
    frame('partial') + 'data: {"choices":[{"delta":{},"finish_reason":"length"}]}\n\ndata: [DONE]\n\n',
    'data: {"choices":[{"delta":{"tool_calls":[{"id":"x"}]},"finish_reason":null}]}\n\n' + ending
  ];
  for (const content of bodies) await fixture((req, res) => { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); res.end(content); }, async base => {
    await assert.rejects(new ApiProvider({ baseUrl: base, model: 'x' }).complete('x', 'y'), error => error.code === 'incomplete');
  });
});

test('只对明确拒绝 stream 参数回退一次，并记住兼容能力', async () => {
  await fixture((req, res, body) => body.stream ? json(res, { error: { code: 'unsupported_parameter', param: 'stream' } }, 400) : json(res, { choices: [{ finish_reason: 'stop', message: { content: 'OK' } }] }), async (base, requests) => {
    const api = new ApiProvider({ baseUrl: base, model: 'x' });
    assert.equal(await api.complete('x', 'y'), 'OK');
    assert.equal(await api.complete('x', 'y'), 'OK');
    assert.deepEqual(requests.map(request => request.body.stream), [true, false, false]);
  });
  for (const status of [401, 429, 500]) await fixture((req, res) => json(res, { error: { message: 'stream request failed' } }, status), async (base, requests) => {
    await assert.rejects(new ApiProvider({ baseUrl: base, model: 'x' }).complete('x', 'y'));
    assert.equal(requests.length, 1);
  });
});

test('API 可在首包前与部分内容后取消，取消后不再发布片段', async () => {
  await fixture((req, res) => {
    res.writeHead(200, { 'Content-Type': 'text/event-stream' }); res.write(frame('first'));
    const timer = setTimeout(() => res.end(frame('late') + ending), 300);
    res.on('close', () => clearTimeout(timer));
  }, async base => {
    const controller = new AbortController(), deltas = [];
    const pending = new ApiProvider({ baseUrl: base, model: 'x' }).complete('x', 'y', 2000, { signal: controller.signal, onDelta: text => deltas.push(text) });
    await waitUntil(() => deltas.length > 0); controller.abort();
    await assert.rejects(pending, error => error.code === 'cancelled');
    await new Promise(resolve => setTimeout(resolve, 30)); assert.deepEqual(deltas, ['first']);
    const already = new AbortController(); already.abort();
    await assert.rejects(new ApiProvider({ baseUrl: base, model: 'x' }).complete('x', 'y', 1000, { signal: already.signal }), error => error.code === 'cancelled');
  });
});

test('Codex 使用已确认的增量事件并隔离错误会话/轮次', async () => {
  const bridge = new CodexBridge('', '', ''); const delta = [];
  const completion = new Promise((resolve, reject) => { bridge.active = { threadId: 'ours', turnId: 'turn-a', resolve, reject, text: '', onDelta: text => delta.push(text) }; });
  bridge.receive({ method: 'item/agentMessage/delta', params: { threadId: 'other', turnId: 'turn-a', delta: 'wrong' } });
  bridge.receive({ method: 'item/agentMessage/delta', params: { threadId: 'ours', turnId: 'old', delta: 'wrong' } });
  bridge.receive({ method: 'item/agentMessage/delta', params: { threadId: 'ours', turnId: 'turn-a', itemId: 'message-a', delta: '可见正文' } });
  bridge.receive({ method: 'item/completed', params: { threadId: 'ours', turnId: 'turn-a', item: { type: 'agentMessage', text: '可见正文' } } });
  bridge.receive({ method: 'turn/completed', params: { threadId: 'ours', turn: { id: 'turn-a', status: 'completed' } } });
  assert.equal(await completion, '可见正文'); assert.deepEqual(delta, ['可见正文']);
});

test('Codex 取消等待中的 turn/start，晚到响应只中断旧轮次', async () => {
  const bridge = new CodexBridge('', '', ''); const calls = []; let finishStart;
  bridge.start = async () => {}; bridge.effort = 'medium';
  bridge.request = async (method, params) => {
    calls.push({ method, params });
    if (method === 'thread/start') return { thread: { id: 'old-thread' } };
    if (method === 'turn/start') return new Promise(resolve => { finishStart = resolve; });
    return {};
  };
  const controller = new AbortController();
  const pending = bridge.optimize('草稿', 1000, { signal: controller.signal });
  await waitUntil(() => finishStart); controller.abort();
  await assert.rejects(pending, error => error.code === 'cancelled');
  finishStart({ turn: { id: 'late-turn' } });
  await waitUntil(() => calls.some(call => call.method === 'turn/interrupt'));
  assert.equal(bridge.active, null); assert.equal(bridge.runBusy, false);
  assert.ok(calls.filter(call => call.method === 'turn/interrupt').every(call => call.params.threadId === 'old-thread' && call.params.turnId === 'late-turn'));
});

async function worker(run) {
  const process = spawn(global.process.execPath, [path.resolve(__dirname, '../ai-worker.cjs')], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const events = []; let stderr = '';
  process.stderr.on('data', data => { stderr += data; });
  readline.createInterface({ input: process.stdout }).on('line', line => events.push(JSON.parse(line)));
  const send = value => process.stdin.write(JSON.stringify(value) + '\n');
  try { await run(send, events); assert.equal(stderr, ''); }
  finally { process.stdin.end(); await new Promise(resolve => process.once('exit', resolve)); }
}

test('Codex 取消后应答缺失时仍中断晚到的 started 事件，记录有界', async () => {
  const bridge = new CodexBridge('', '', ''), calls = [], deltas = [];
  let finishStart;
  bridge.start = async () => {}; bridge.effort = 'medium';
  bridge.request = async (method, params) => {
    calls.push({ method, params });
    if (method === 'thread/start') return { thread: { id: 'cancelled-thread' } };
    if (method === 'turn/start') return new Promise(resolve => { finishStart = resolve; });
    return {};
  };
  const controller = new AbortController();
  const pending = bridge.optimize('草稿', 1000, { signal: controller.signal });
  await waitUntil(() => finishStart); controller.abort();
  await assert.rejects(pending, error => error.code === 'cancelled');
  bridge.active = { threadId: 'new-thread', turnId: 'new-turn', onDelta: text => deltas.push(text) };
  bridge.receive({ method: 'turn/started', params: { threadId: 'cancelled-thread', turn: { id: 'late-turn' } } });
  bridge.receive({ method: 'item/agentMessage/delta', params: { threadId: 'cancelled-thread', turnId: 'late-turn', delta: '旧内容' } });
  bridge.receive({ method: 'item/agentMessage/delta', params: { threadId: 'new-thread', turnId: 'new-turn', delta: '新内容' } });
  assert.ok(calls.some(call => call.method === 'turn/interrupt' && call.params.threadId === 'cancelled-thread' && call.params.turnId === 'late-turn'));
  assert.deepEqual(deltas, ['新内容']);
  finishStart({ turn: { id: 'late-turn' } });
  for (let i = 0; i < 100; i++) bridge.rememberCancelled('old-' + i);
  assert.ok(bridge.cancelledThreads.size <= 64);
  bridge.cancelledThreads.set('expired', { expires: Date.now() - 1, interrupted: null });
  const count = calls.length;
  bridge.receive({ method: 'turn/started', params: { threadId: 'expired', turn: { id: 'old-turn' } } });
  assert.equal(calls.length, count); assert.equal(bridge.cancelledThreads.has('expired'), false);
  bridge.close(); assert.equal(bridge.cancelledThreads.size, 0);
});

test('真实 JSONL 控制能取消活动和排队请求，立即重试且不串旧增量', async () => {
  await fixture((req, res, body) => {
    res.writeHead(200, { 'Content-Type': 'text/event-stream' });
    if (body.model === 'slow') {
      res.write(frame('first'));
      const timer = setTimeout(() => res.end(frame('late') + ending), 1000);
      res.on('close', () => clearTimeout(timer));
    } else res.end(frame('保持0.5 mM。') + ending);
  }, async (base, requests) => worker(async (send, events) => {
    const profile = model => ({ kind: 'openai', baseUrl: base, model, apiKey: 'synthetic' });
    send({ id: 1, action: 'optimize', provider: profile('slow'), text: '0.5 mM' });
    await waitUntil(() => events.some(event => event.id === 1 && event.event === 'delta'));
    send({ id: 2, action: 'cancel', targetId: 1 });
    send({ id: 3, action: 'optimize', provider: profile('fast'), text: '0.5 mM' });
    send({ id: 4, action: 'optimize', provider: profile('never'), text: '0.5 mM' });
    send({ id: 5, action: 'cancel', targetId: 4 });
    await waitUntil(() => events.some(event => event.id === 3 && event.ok === true) && events.some(event => event.id === 4 && event.errorCode === 'cancelled'));
    assert.ok(events.some(event => event.id === 1 && event.errorCode === 'cancelled'));
    assert.ok(!events.some(event => event.id === 1 && event.ok === true));
    const acknowledged = events.findIndex(event => event.id === 2 && event.ok === true);
    assert.ok(!events.slice(acknowledged + 1).some(event => event.id === 1 && event.event === 'delta'));
    assert.ok(!requests.some(request => request.body.model === 'never'));
    send({ id: 6, action: 'cancel', targetId: 999 });
    send({ id: 7, action: 'optimize', provider: profile('fast'), text: '0.5 mM' });
    await waitUntil(() => events.some(event => event.id === 7 && event.ok === true));
    assert.equal(events.find(event => event.id === 6).result.cancelled, false);
  }));
});

test('真实 IPC 风险正文保留并返回预览标记，不作为请求失败丢弃', async () => {
  await fixture((req, res) => json(res, { choices: [{ finish_reason: 'stop', message: { content: '浓度4 mM，温度0.5 °C，加热。' } }] }), async base => worker(async (send, events) => {
    send({ id: 10, action: 'optimize', provider: { kind: 'openai', baseUrl: base, model: 'risk' }, text: '浓度0.5 mM，温度4 °C，不要加热。' });
    await waitUntil(() => events.some(event => event.id === 10 && event.ok === true));
    const final = events.find(event => event.id === 10 && event.ok === true);
    assert.equal(final.requiresReview, true); assert.ok(final.reviewWarnings.length >= 2); assert.match(final.result, /4 mM/);
  }));
});
