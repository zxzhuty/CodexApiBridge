namespace CodexApiBridge;

public static class AdminPage
{
    public const string Html = """
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Codex API Bridge 管理台</title>
  <style>
    :root { color-scheme: light; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
    body { margin: 0; background: #f5f7fb; color: #172033; }
    header { padding: 20px 28px; background: #102033; color: white; display:flex; justify-content:space-between; align-items:center; gap: 16px; }
    header h1 { margin: 0; font-size: 22px; }
    main { padding: 22px; display: grid; grid-template-columns: 1.1fr .9fr; gap: 18px; }
    section { background: white; border: 1px solid #d9e2ef; border-radius: 8px; padding: 18px; }
    h2 { margin: 0 0 14px; font-size: 18px; }
    label { display:block; font-weight:600; margin: 10px 0 5px; }
    input, textarea, select { width:100%; box-sizing:border-box; border:1px solid #c8d2df; border-radius:6px; padding:9px; font: inherit; }
    textarea { min-height: 84px; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; }
    button { border:0; border-radius:6px; padding:9px 14px; background:#1d4ed8; color:white; cursor:pointer; font-weight:600; }
    button.secondary { background:#526176; }
    code { background: #eef3f8; padding: 2px 5px; border-radius: 4px; }
    .grid { display:grid; grid-template-columns: repeat(3, 1fr); gap:12px; }
    .metric { background:#f7f9fc; border:1px solid #dbe3ef; border-radius:8px; padding:12px; }
    .metric b { display:block; font-size:24px; margin-top:4px; overflow-wrap:anywhere; }
    .row { display:grid; grid-template-columns: 1fr 1fr; gap:10px; }
    .actions { display:flex; gap:10px; margin-top:14px; flex-wrap:wrap; }
    .rule { border:1px solid #dbe3ef; border-radius:8px; padding:12px; margin:10px 0; background:#fbfcfe; }
    .hint { color:#526176; line-height: 1.6; }
    pre { background:#101827; color:#dce8f7; padding:12px; border-radius:8px; overflow:auto; max-height:420px; }
    .wide { grid-column: 1 / -1; }
    @media (max-width: 900px) { main { grid-template-columns: 1fr; } .grid { grid-template-columns: 1fr; } .row { grid-template-columns: 1fr; } }
  </style>
</head>
<body>
  <header>
    <h1>Codex API Bridge 管理台</h1>
    <span id="status">加载中...</span>
  </header>
  <main>
    <section>
      <h2>代理配置</h2>
      <div class="row">
        <div><label>监听地址</label><input id="listenUrl"></div>
        <div><label>上游 Base URL</label><input id="upstreamBaseUrl"></div>
      </div>
      <label>上游 API Key</label><input id="upstreamApiKey" type="password">
      <div class="row">
        <div><label>模型覆盖</label><input id="modelOverride"></div>
        <div><label>请求超时秒数</label><input id="timeout" type="number"></div>
      </div>
      <div class="row">
        <div><label>日志目录</label><input id="logDirectory"></div>
        <div><label>缺失 reasoning 兜底文本</label><input id="missingReasoning"></div>
      </div>
      <label><input id="forwardAuth" type="checkbox" style="width:auto"> API Key 为空时转发客户端 Authorization</label>
      <label><input id="verbose" type="checkbox" style="width:auto"> 记录完整请求体到日志</label>
      <label><input id="reasoningCompat" type="checkbox" style="width:auto"> 启用 reasoning_content 兼容</label>
      <label><input id="reasoningFallback" type="checkbox" style="width:auto"> 缺失 reasoning_content 时自动补兜底</label>
      <label><input id="toolExecution" type="checkbox" style="width:auto"> 自动执行配置了网关地址的工具调用</label>
      <label>最大工具执行轮数</label><input id="toolIterations" type="number">
      <div class="actions">
        <button onclick="saveSettings()">保存配置</button>
        <button class="secondary" onclick="loadAll()">刷新</button>
      </div>
      <p id="saveHint" class="hint"></p>
    </section>

    <section>
      <h2>统计</h2>
      <div class="grid" id="statsGrid"></div>
    </section>

    <section class="wide">
      <h2>工具转发/兼容规则</h2>
      <p class="hint">
        将 Responses 工具类型映射成 Chat Completions function。若 <code>forward_mode</code> 不是 <code>function</code> 且填写了网关地址，
        模型发起 function call 后，桥接器会调用该 HTTP/MCP 网关，并把结果作为 tool 消息回灌给上游模型。
      </p>
      <div id="rules"></div>
      <div class="actions">
        <button onclick="addRule()">新增规则</button>
      </div>
    </section>

    <section class="wide">
      <h2>实时日志</h2>
      <div class="actions"><button class="secondary" onclick="loadLogs()">刷新日志</button></div>
      <pre id="logs"></pre>
    </section>
  </main>
<script>
let settings = {};

async function loadAll() {
  await loadSettings();
  await loadStats();
  await loadLogs();
}

async function loadSettings() {
  const res = await fetch('/api/settings');
  const data = await res.json();
  settings = data.settings;
  document.getElementById('status').textContent = `配置文件：${data.config_path}`;
  listenUrl.value = settings.listen_url || '';
  upstreamBaseUrl.value = settings.upstream_base_url || '';
  upstreamApiKey.value = settings.upstream_api_key || '';
  modelOverride.value = settings.model_override || '';
  timeout.value = settings.request_timeout_seconds || 600;
  logDirectory.value = settings.log_directory || 'logs';
  missingReasoning.value = settings.missing_reasoning_content_fallback || '';
  forwardAuth.checked = !!settings.forward_incoming_authorization;
  verbose.checked = !!settings.enable_verbose_body_logging;
  reasoningCompat.checked = !!settings.enable_reasoning_content_compatibility;
  reasoningFallback.checked = !!settings.enable_missing_reasoning_content_fallback;
  toolExecution.checked = !!settings.enable_tool_forwarding_execution;
  toolIterations.value = settings.max_tool_forwarding_iterations || 4;
  renderRules();
}

function collectSettings() {
  settings.listen_url = listenUrl.value;
  settings.upstream_base_url = upstreamBaseUrl.value;
  settings.upstream_api_key = upstreamApiKey.value;
  settings.model_override = modelOverride.value;
  settings.request_timeout_seconds = Number(timeout.value || 600);
  settings.log_directory = logDirectory.value;
  settings.missing_reasoning_content_fallback = missingReasoning.value;
  settings.forward_incoming_authorization = forwardAuth.checked;
  settings.enable_verbose_body_logging = verbose.checked;
  settings.enable_reasoning_content_compatibility = reasoningCompat.checked;
  settings.enable_missing_reasoning_content_fallback = reasoningFallback.checked;
  settings.enable_tool_forwarding_execution = toolExecution.checked;
  settings.max_tool_forwarding_iterations = Number(toolIterations.value || 4);
  settings.tool_forwarding_rules = collectRules();
  return settings;
}

async function saveSettings() {
  const res = await fetch('/api/settings', { method:'POST', headers:{'content-type':'application/json'}, body: JSON.stringify(collectSettings()) });
  if (!res.ok) { saveHint.textContent = await res.text(); return; }
  saveHint.textContent = '已保存。监听地址或日志目录变更需要重启进程。';
  await loadSettings();
}

function renderRules() {
  const root = document.getElementById('rules');
  root.innerHTML = '';
  (settings.tool_forwarding_rules || []).forEach((rule, index) => {
    const div = document.createElement('div');
    div.className = 'rule';
    div.innerHTML = `
      <label><input data-k="enabled" type="checkbox" ${rule.enabled ? 'checked' : ''} style="width:auto"> 启用</label>
      <div class="row">
        <div><label>Responses 工具类型</label><input data-k="responses_tool_type" value="${esc(rule.responses_tool_type || '')}"></div>
        <div><label>Function 名称</label><input data-k="function_name" value="${esc(rule.function_name || '')}"></div>
      </div>
      <div class="row">
        <div><label>转发模式</label><select data-k="forward_mode"><option value="function">function</option><option value="http">http gateway</option><option value="mcp_stdio">mcp stdio</option><option value="mcp_sse">mcp sse</option></select></div>
        <div><label>HTTP 网关地址 / MCP 启动命令</label><input data-k="forward_endpoint" value="${esc(rule.forward_endpoint || '')}"></div>
      </div>
      <label>描述</label><textarea data-k="description">${esc(rule.description || '')}</textarea>
      <label>参数 JSON Schema</label><textarea data-k="parameters_json">${esc(rule.parameters_json || '{}')}</textarea>
      <div class="actions"><button class="secondary" onclick="removeRule(${index})">删除</button></div>`;
    root.appendChild(div);
    div.querySelector('[data-k="forward_mode"]').value = rule.forward_mode || 'function';
  });
}

function collectRules() {
  return [...document.querySelectorAll('.rule')].map(div => ({
    enabled: div.querySelector('[data-k="enabled"]').checked,
    responses_tool_type: div.querySelector('[data-k="responses_tool_type"]').value,
    function_name: div.querySelector('[data-k="function_name"]').value,
    forward_mode: div.querySelector('[data-k="forward_mode"]').value,
    forward_endpoint: div.querySelector('[data-k="forward_endpoint"]').value,
    description: div.querySelector('[data-k="description"]').value,
    parameters_json: div.querySelector('[data-k="parameters_json"]').value
  }));
}

function addRule() {
  settings.tool_forwarding_rules = collectRules();
  settings.tool_forwarding_rules.push({ enabled:true, responses_tool_type:'web_search', function_name:'web_search', forward_mode:'function', forward_endpoint:'', description:'搜索工具映射。', parameters_json:'{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":true}' });
  renderRules();
}

function removeRule(index) {
  settings.tool_forwarding_rules = collectRules().filter((_, i) => i !== index);
  renderRules();
}

async function loadStats() {
  const res = await fetch('/api/stats');
  const s = await res.json();
  const items = [
    ['总请求', s.total_requests], ['Responses', s.responses_requests], ['Chat', s.chat_requests],
    ['流式', s.streaming_requests], ['失败', s.failed_requests], ['总 Token', s.total_tokens],
    ['输入 Token', s.prompt_tokens], ['输出 Token', s.completion_tokens], ['平均延迟', `${Math.round(s.average_latency_ms || 0)} ms`],
    ['最大延迟', `${Math.round(s.max_latency_ms || 0)} ms`], ['最近状态码', s.last_status_code || '-'], ['最近请求', s.last_request_at || '-']
  ];
  statsGrid.innerHTML = items.map(([key,value]) => `<div class="metric">${key}<b>${value ?? 0}</b></div>`).join('');
}

async function loadLogs() {
  const res = await fetch('/api/logs');
  const rows = await res.json();
  logs.textContent = rows.map(x => `[${x.time}] ${x.level} ${x.message}`).join('\n');
}

function esc(value) {
  return String(value).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
}

loadAll();
setInterval(loadStats, 3000);
setInterval(loadLogs, 5000);
</script>
</body>
</html>
""";
}
