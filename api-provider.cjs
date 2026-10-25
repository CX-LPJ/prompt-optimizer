'use strict';

class ApiError extends Error {
  constructor(code, message) { super(message); this.code = code; }
}
function cancelled() { return new ApiError('cancelled', '已停止请求，原文已保留。'); }
function checkSignal(signal) { if (signal?.aborted) throw cancelled(); }

function normalizeBaseUrl(value) {
  let url;
  try { url = new URL(String(value || '').trim()); } catch (_) { throw new ApiError('invalid_url', '请填写有效的 Base URL。'); }
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
  if ((url.protocol !== 'https:' && !(url.protocol === 'http:' && local)) || url.username || url.password || url.search || url.hash)
    throw new ApiError('invalid_url', 'Base URL 须使用 HTTPS；本机 localhost、127.0.0.1 可使用 HTTP。地址中不要包含账号、密码或查询参数。');
  url.pathname = url.pathname.replace(/\/(?:chat\/completions|models)\/?$/, '').replace(/\/+$/, '');
  return url.toString().replace(/\/+$/, '');
}

function httpError(status, payload, action) {
  const error = payload?.error || {};
  const detail = String(error.code || '') + ' ' + String(error.type || '') + ' ' + String(error.message || '');
  // Inspect provider detail only for classification; never display a response
  // body, which could contain a reflected API key, request, or private text.
  if (status === 402 || /insufficient_quota|insufficient[_ ]balance|credit|quota[_ ]exceeded|余额|额度不足/i.test(detail))
    return new ApiError('quota', '额度或余额不足，请检查当前供应商的余额与用量限制。');
  if (status === 401) return new ApiError('authentication', '认证失败（401），请检查此供应商的 API Key。');
  if (/model[_ ]not[_ ]found|invalid[_ ]model|model.*(?:not exist|unavailable|not supported|access)|模型.*(?:不存在|不可用)/i.test(detail))
    return new ApiError('model_unavailable', '模型不存在、不可用或无访问权限，请加载模型列表或修改模型名称。');
  if (status === 403) return new ApiError('permission', '访问被拒绝（403），请检查密钥权限、模型授权或供应商的区域限制。');
  if (status === 429) return new ApiError('rate_limit', '请求过于频繁（429），请稍后再试或检查供应商的限流设置。');
  if (action === 'models' && [404, 405, 501].includes(status))
    return new ApiError('models_unsupported', '接口不支持获取模型列表，可直接手动填写模型名称。');
  if (status === 404) return new ApiError('not_found', '接口地址不存在（404），请检查 Base URL；通常应填写到 /v1，不含 /chat/completions。');
  if (status >= 300 && status < 400) return new ApiError('redirect', '接口返回重定向，未转发密钥；请填写供应商提供的最终 Base URL。');
  if ([400, 422].includes(status)) return new ApiError('invalid_request', '供应商不接受当前请求，请检查模型名、兼容接口格式；可将 API 推理强度设为 auto。');
  if (status >= 500) return new ApiError('server', '供应商服务暂时异常（' + status + '），请稍后再试。');
  return new ApiError('http', 'API 请求失败（HTTP ' + status + '），请检查供应商配置。');
}

class ApiProvider {
  constructor(profile, fetchImpl = fetch) {
    this.profile = profile; this.baseUrl = normalizeBaseUrl(profile.baseUrl); this.fetch = fetchImpl; this.streamSupported = true;
  }
  async request(action, body, timeoutMs = 35000, options = {}) {
    checkSignal(options.signal);
    const controller = new AbortController();
    const externalAbort = () => controller.abort();
    options.signal?.addEventListener('abort', externalAbort, { once: true });
    if (options.signal?.aborted) controller.abort();
    const timer = setTimeout(() => controller.abort(), Math.min(Math.max(timeoutMs, 100), 120000));
    try {
      const headers = { Accept: 'application/json' };
      if (this.profile.apiKey) headers.Authorization = 'Bearer ' + this.profile.apiKey;
      if (body) headers['Content-Type'] = 'application/json';
      const response = await this.fetch(this.baseUrl + (action === 'models' ? '/models' : '/chat/completions'), {
        method: body ? 'POST' : 'GET', headers, body: body ? JSON.stringify(body) : undefined,
        redirect: 'manual', signal: controller.signal
      });
      checkSignal(options.signal);
      if (response.ok && body?.stream && /text\/event-stream/i.test(response.headers.get('content-type') || ''))
        return await readChatStream(response, options.onDelta, controller.signal);
      let payload;
      const reader = response.body?.getReader();
      let text = '', size = 0;
      if (reader) {
        const decoder = new TextDecoder();
        while (true) {
          const part = await reader.read(); if (part.done) break;
          size += part.value.length;
          if (size > 2 * 1024 * 1024) { await reader.cancel(); throw new ApiError('response_size', '供应商响应过大，已停止处理。'); }
          text += decoder.decode(part.value, { stream: true });
        }
        text += decoder.decode();
      } else { text = await response.text(); }
      try { payload = JSON.parse(text); } catch (_) { payload = {}; }
      if (!response.ok) {
        const error = payload?.error || {};
        const explicitlyRejected = [400, 422].includes(response.status) &&
          ((error.param === 'stream' && /unsupported|unknown|invalid|unrecognized/i.test(String(error.code || error.type || ''))) ||
           /\bstream\b/i.test(String(error.message || '')) && /not support|unsupported|not allowed|unknown|unrecognized|unexpected/i.test(String(error.message || '')));
        if (body?.stream && explicitlyRejected) throw new ApiError('stream_unsupported', '此接口不支持流式请求。');
        throw httpError(response.status, payload, action);
      }
      if (!payload || typeof payload !== 'object' || payload.error) throw new ApiError('invalid_response', '供应商返回了无效响应，请检查兼容接口地址和服务状态。');
      return payload;
    } catch (error) {
      if (options.signal?.aborted) throw cancelled();
      if (controller.signal.aborted) throw new ApiError('timeout', 'API 请求超时，原文已保留；可检查网络或适当提高超时时间。');
      if (error instanceof ApiError) throw error;
      throw new ApiError('network', '连接失败，请检查 Base URL、网络、代理与 HTTPS 证书。');
    } finally { clearTimeout(timer); options.signal?.removeEventListener('abort', externalAbort); }
  }
  async models(timeoutMs, options = {}) {
    const payload = await this.request('models', null, timeoutMs, options);
    if (!Array.isArray(payload.data)) throw new ApiError('models_unsupported', '模型列表格式不受支持，可直接手动填写模型名称。');
    return [...new Set(payload.data.map(item => item?.id).filter(id => typeof id === 'string' && id.length > 0 && id.length <= 256))].sort().slice(0, 2000);
  }
  async complete(instruction, text, timeoutMs, options = {}) {
    checkSignal(options.signal);
    const budget = timeoutMs ?? 35000, started = Date.now();
    if (!this.profile.model?.trim()) throw new ApiError('missing_model', '请选择或手动填写模型名称。');
    const body = { model: this.profile.model, messages: [{ role: 'system', content: instruction }, { role: 'user', content: text }], stream: this.streamSupported };
    if (this.profile.reasoningEffort && this.profile.reasoningEffort !== 'auto') body.reasoning_effort = this.profile.reasoningEffort;
    let payload, emitted = false;
    const requestOptions = { ...options, onDelta: delta => { checkSignal(options.signal); emitted = true; options.onDelta?.(delta); } };
    try { payload = await this.request('complete', body, timeoutMs, requestOptions); }
    catch (error) {
      if (error.code !== 'stream_unsupported' || emitted || options.signal?.aborted) throw error;
      this.streamSupported = false; body.stream = false;
      const remaining = budget - (Date.now() - started);
      if (remaining <= 0) throw new ApiError('timeout', 'API 请求超时，原文已保留。');
      payload = await this.request('complete', body, remaining, requestOptions);
    }
    checkSignal(options.signal);
    const choice = payload.choices?.[0];
    if (!choice || choice.finish_reason !== 'stop' || choice.message?.tool_calls?.length || choice.message?.function_call)
      throw new ApiError('incomplete', '模型未返回完整文本（可能被截断、过滤或请求工具），原文已保留。');
    const content = choice.message?.content;
    const result = typeof content === 'string' ? content : Array.isArray(content) ? content.filter(p => p.type === 'text' && typeof p.text === 'string').map(p => p.text).join('') : '';
    if (!result.trim() || result.length > 60000) throw new ApiError('empty_result', '模型没有返回有效正文，原文已保留。');
    if (!emitted) options.onDelta?.(result);
    return result.trim();
  }
}

async function readChatStream(response, onDelta, signal) {
  const reader = response.body?.getReader();
  if (!reader) throw new ApiError('incomplete', '供应商未返回完整流式响应，原文已保留。');
  const decoder = new TextDecoder();
  let buffer = '', output = '', bytes = 0, done = false, finish = null;
  function event(block) {
    const data = block.split(/\r?\n/).filter(line => line.startsWith('data:')).map(line => line.slice(5).replace(/^ /, '')).join('\n');
    if (!data) return;
    if (data.trim() === '[DONE]') { done = true; return; }
    let value;
    try { value = JSON.parse(data); } catch (_) { throw new ApiError('invalid_response', '供应商返回了无效的流式响应，原文已保留。'); }
    if (value.error) throw httpError(Number.isInteger(value.error.status) ? value.error.status : 400, value, 'complete');
    const choice = value.choices?.[0];
    if (!choice) return;
    if (choice.delta?.tool_calls?.length || choice.delta?.function_call || choice.finish_reason && choice.finish_reason !== 'stop')
      throw new ApiError('incomplete', '模型未返回完整文本（可能被截断、过滤或请求工具），原文已保留。');
    const content = choice.delta?.content;
    const delta = typeof content === 'string' ? content : Array.isArray(content) ? content.filter(p => p.type === 'text' && typeof p.text === 'string').map(p => p.text).join('') : '';
    if (delta) {
      checkSignal(signal); output += delta;
      if (output.length > 60000) throw new ApiError('response_size', '模型响应过长，已停止处理。');
      onDelta?.(delta);
    }
    if (choice.finish_reason) finish = choice.finish_reason;
  }
  try {
    while (!done) {
      checkSignal(signal);
      const part = await reader.read(); if (part.done) break;
      bytes += part.value.length;
      if (bytes > 2 * 1024 * 1024) throw new ApiError('response_size', '供应商响应过大，已停止处理。');
      buffer += decoder.decode(part.value, { stream: true });
      let boundary;
      while ((boundary = /\r?\n\r?\n/.exec(buffer))) {
        const block = buffer.slice(0, boundary.index);
        buffer = buffer.slice(boundary.index + boundary[0].length); event(block);
        if (done) break;
      }
    }
    checkSignal(signal);
    if (!done || finish !== 'stop' || !output.trim()) throw new ApiError('incomplete', '流式响应未完整结束，原文已保留。');
    return { choices: [{ finish_reason: 'stop', message: { role: 'assistant', content: output } }] };
  } finally { await reader.cancel().catch(() => {}); }
}

module.exports = { ApiProvider, ApiError, normalizeBaseUrl, httpError, cancelled, checkSignal, readChatStream };
