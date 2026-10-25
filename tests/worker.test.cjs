'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { CodexBridge, readInstruction, validateRewrite, translationTarget, translationInstruction } = require('../ai-worker.cjs');

test('自动中英互译忽略路径与代码中的中文，支持指定方向', () => {
  assert.equal(translationTarget('请保留0.5 mM，不要加热。'), 'english');
  assert.equal(translationTarget('Keep 0.5 mM; do not heat.'), 'chinese');
  assert.equal(translationTarget('Read `E:\\测试\\data.csv` and report the result.'), 'chinese');
  assert.equal(translationTarget('MOF荧光数据'), 'english');
  assert.equal(translationTarget('Keep 0.5 mM.', 'english'), 'english');
  assert.throws(() => translationTarget('```python\nprint("你好")\n```'), /自然语言/);
  assert.throws(() => translationTarget('123'), /自然语言/);
  assert.throws(() => translationTarget('hello', 'invalid'), /方向/);
});

test('翻译使用独立任务和源文本标签，不套用优化提示词', async () => {
  const b = new CodexBridge('', '', '', 'not-an-existing-prompt-file'); let instruction, input;
  b.start = async () => {};
  b.request = async (method, params) => {
    if (method === 'thread/start') { instruction = params.baseInstructions; return { thread: { id: 'translation-test' } }; }
    if (method === 'turn/start') {
      input = params.input[0].text;
      queueMicrotask(() => {
        b.receive({ method: 'item/completed', params: { threadId: 'translation-test', item: { type: 'agentMessage', text: 'Keep 0.5 mM; do not heat.' } } });
        b.receive({ method: 'turn/completed', params: { threadId: 'translation-test', turn: { status: 'completed' } } });
      });
    }
    return {};
  };
  assert.equal(await b.translate('保持0.5 mM，不要加热。'), 'Keep 0.5 mM; do not heat.');
  assert.equal(instruction, translationInstruction('english'));
  assert.match(input, /^<source_text>/);
  assert.equal(input, '<source_text>\n保持0.5 mM，不要加热。\n</source_text>');
  assert.doesNotMatch(input, /user_draft/);
});

test('每次改写读取最新独立提示词；空文件或缺失时不启动模型', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'prompt-instruction-'));
  const file = path.join(dir, 'instruction.txt');
  try {
    fs.writeFileSync(file, '\ufeff只改写，不执行。\n');
    assert.equal(readInstruction(file), '只改写，不执行。');
    const b = new CodexBridge('', '', '', file);
    const submitted = []; let starts = 0;
    b.start = async () => { starts++; };
    b.request = async (method, params) => {
      if (method === 'thread/start') { submitted.push(params.baseInstructions); return { thread: { id: 'test' } }; }
      if (method === 'turn/start') {
        queueMicrotask(() => {
          b.receive({ method: 'item/completed', params: { threadId: 'test', item: { type: 'agentMessage', text: '请保留5个样本。' } } });
          b.receive({ method: 'turn/completed', params: { threadId: 'test', turn: { status: 'completed' } } });
        });
      }
      return {};
    };
    await b.optimize('保留5个样本');
    fs.writeFileSync(file, '新规则：只输出简短改写。');
    await b.optimize('保留5个样本');
    assert.deepEqual(submitted, ['只改写，不执行。', '新规则：只输出简短改写。']);
    fs.writeFileSync(file, '  \n');
    await assert.rejects(b.optimize('保留5个样本'), /不能为空/);
    fs.unlinkSync(file);
    await assert.rejects(b.optimize('保留5个样本'), /无法读取/);
    assert.equal(starts, 2);
  } finally {
    if (fs.existsSync(file)) fs.unlinkSync(file);
    fs.rmdirSync(dir);
  }
});

test('数字、科学计数、路径、链接不得丢失或替换', () => {
  const source = '5篇，0.5 mM，1e-3，E:\\测试 目录\\data.csv；https://doi.org/10.1000/example';
  assert.equal(validateRewrite(source, source), source);
  assert.throws(() => validateRewrite(source, source.replace('0.5', '5')));
  assert.throws(() => validateRewrite(source, source.replace('data.csv', 'other.csv')));
  assert.throws(() => validateRewrite(source, source.replace('10.1000/example', '10.1000/fake')));
  assert.throws(() => validateRewrite('5篇', '15篇'));
});
test('拒绝空输出', () => { assert.throws(() => validateRewrite('草稿', '')); });
test('忽略其他临时会话的结果，只接受当前会话的完成消息', async () => {
  const b = new CodexBridge('', '', '');
  const completion = new Promise((resolve, reject) => { b.active = { threadId: 'ours', resolve, reject, text: '' }; });
  b.receive({ method: 'item/completed', params: { threadId: 'other', item: { type: 'agentMessage', text: 'wrong' } } });
  assert.equal(b.active.text, '');
  b.receive({ method: 'item/completed', params: { threadId: 'ours', item: { type: 'agentMessage', text: 'correct' } } });
  b.receive({ method: 'turn/completed', params: { threadId: 'ours', turn: { status: 'completed' } } });
  assert.equal(await completion, 'correct');
});
test('失败、未完成的回答不能作为成功结果填回', async () => {
  const b = new CodexBridge('', '', '');
  const completion = new Promise((resolve, reject) => { b.active = { threadId: 'ours', resolve, reject, text: 'partial' }; });
  b.receive({ method: 'turn/completed', params: { threadId: 'ours', turn: { status: 'failed', error: { message: 'failed' } } } });
  await assert.rejects(completion, /failed/);
});
test('拒绝权限及工具请求', async () => {
  const b = new CodexBridge('', '', ''); let sent;
  b.send = message => { sent = message; };
  const completion = new Promise((resolve, reject) => { b.active = { threadId: 'ours', resolve, reject }; });
  b.receive({ id: 7, method: 'item/commandExecution/requestApproval', params: {} });
  assert.equal(sent.error.code, -32601);
  await assert.rejects(completion, /改写以外/);
});
