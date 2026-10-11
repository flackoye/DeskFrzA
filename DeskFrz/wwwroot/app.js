'use strict';

const paths = {
  freeze: '<path d="M12 2v20M3.3 7l17.4 10M3.3 17L20.7 7M8 4l4 4 4-4M8 20l4-4 4 4M3 11l5-1-1-5M21 13l-5 1 1 5M7 19l1-5-5-1M17 5l-1 5 5 1"/>',
  refresh: '<path d="M20 7v5h-5M4 17v-5h5M5.4 8a7 7 0 0 1 11.7-3L20 8M4 16l2.9 3A7 7 0 0 0 18.6 16"/>',
  arrow: '<path d="M5 12h14M13 6l6 6-6 6"/>',
  restore: '<path d="M4 9V4m0 5h5M4 9a8 8 0 1 1 .9 8M12 8v5l3 2"/>',
  undo: '<path d="M8 5l-5 5 5 5M3 10h11a6 6 0 0 1 0 12"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v6l4 2"/>',
  sliders: '<path d="M4 7h7m4 0h5M4 17h3m4 0h9"/><circle cx="13" cy="7" r="2"/><circle cx="9" cy="17" r="2"/>',
  chevron: '<path d="M6 9l6 6 6-6"/>',
  close: '<path d="M6 6l12 12M6 18L18 6"/>',
  layers: '<path d="M12 3L2 8l10 5 10-5-10-5ZM2 12l10 5 10-5M2 16l10 5 10-5"/>'
};
function icon(name) { const el = document.createElement('span'); el.innerHTML = `<svg class="icon" viewBox="0 0 24 24" aria-hidden="true">${paths[name] || paths.layers}</svg>`; return el.firstChild; }
function hydrate(root = document) {
  root.querySelectorAll('[data-icon]').forEach(el => {
    const svg = icon(el.dataset.icon);
    el.classList.forEach(name => svg.classList.add(name));
    el.replaceWith(svg);
  });
}
function element(tag, className = '', text = '') { const el = document.createElement(tag); el.className = className; el.textContent = text; return el; }
const $ = id => document.getElementById(id);
let token = '', state = null, running = false, queuedAction = false, toastTimer, actionFinished = Promise.resolve();
const dialog = $('modal');

async function api(path, body) {
  const response = await fetch(`/api/${path}`, body === undefined ? { cache: 'no-store' } : {
    method: 'POST', headers: { 'Content-Type': 'application/json', 'X-DeskFrz-Token': token }, body: JSON.stringify(body)
  });
  let data;
  try { data = await response.json(); } catch { throw new Error(`连接失败 (${response.status})，请重新打开 DeskFrzA。`); }
  if (!response.ok) throw new Error(data.error || `操作失败 (${response.status})`);
  return data;
}
function toast(message) { clearTimeout(toastTimer); $('toast').textContent = message; $('toast').hidden = false; toastTimer = setTimeout(() => $('toast').hidden = true, 4000); }
function failure(error) {
  $('error-text').textContent = error.message; $('error-banner').hidden = false; $('action-status').textContent = '操作未完成';
  if (dialog.open) {
    let notice = $('modal-body').querySelector('[role="alert"]');
    if (!notice) { notice = element('p', 'notice error'); notice.setAttribute('role', 'alert'); $('modal-body').append(notice); }
    notice.textContent = error.message;
  }
}
async function perform(action, quiet = false) {
  if (running) { if (quiet || queuedAction) return; queuedAction = true; await actionFinished; queuedAction = false; return perform(action, quiet); }
  let finishAction; actionFinished = new Promise(resolve => finishAction = resolve);
  running = true;
  document.querySelectorAll('.action-button').forEach(el => el.classList.add('busy'));
  try { if (!quiet) $('error-banner').hidden = true; await action(); }
  catch (error) { failure(error); }
  finally { running = false; finishAction(); document.querySelectorAll('.action-button').forEach(el => el.classList.remove('busy')); }
}
function date(value) { return new Intl.DateTimeFormat('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false }).format(new Date(value)); }
function fullDate(value) { return new Intl.DateTimeFormat('zh-CN', { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false }).format(new Date(value)); }

function displayName(m) {
  const number = /DISPLAY(\d+)$/i.exec(m.name);
  return number && m.name === m.device ? `显示器 ${number[1]}` : m.name;
}
function screenTable(snapshot) {
  const table = element('table', 'display-table');
  const head = element('thead'), headers = element('tr');
  ['显示器', '分辨率 / 缩放', '位置', '图标'].forEach(label => {
    const cell = element('th', '', label); cell.scope = 'col'; headers.append(cell);
  });
  head.append(headers);
  const body = element('tbody');
  snapshot.displays.forEach(m => {
    const row = element('tr');
    for (const value of [
      `${displayName(m)} · ${m.primary ? '主屏' : '副屏'}`,
      `${m.bounds.width} × ${m.bounds.height} · ${m.scale}%`,
      `(${m.bounds.x}, ${m.bounds.y})`,
      `${snapshot.icons.filter(i => i.monitorId === m.id).length} 个`
    ]) row.append(element('td', '', value));
    body.append(row);
  });
  table.append(head, body); return table;
}
function compareScreens(saved, current) {
  const root = element('div', 'screen-comparison');
  for (const [label, snapshot] of [['记录中的屏幕', saved], ['当前连接的屏幕', current]]) {
    const mode = snapshot.displays.some(m => m.targetCount > 1) ? '复制显示' : snapshot.displays.length > 1 ? '扩展桌面' : '单屏桌面';
    root.append(element('h3', '', `${label} · ${mode}`), screenTable(snapshot));
  }
  const missing = saved.displays.filter(m => !current.displays.some(now => now.id === m.id));
  const extra = current.displays.filter(m => !saved.displays.some(old => old.id === m.id));
  if (missing.length) root.append(element('p', 'comparison-issue', `未匹配到：${missing.map(displayName).join('、')}。`));
  if (extra.length) root.append(element('p', 'comparison-issue', `当前新增：${extra.map(displayName).join('、')}。`));
  if (missing.length || extra.length) {
    root.append(element('p', '', '可以将保存的布局迁移到当前主屏；若要按原屏恢复，请连接并启用记录中的显示器。'));
  }
  return root;
}
function renderScreens() {
  if (!state) return;
  const { displays, icons } = state.current;
  const map = $('screen-map');
  map.replaceChildren();
  const left = Math.min(...displays.map(m => m.bounds.x));
  const top = Math.min(...displays.map(m => m.bounds.y));
  const right = Math.max(...displays.map(m => m.bounds.x + m.bounds.width));
  const bottom = Math.max(...displays.map(m => m.bounds.y + m.bounds.height));
  const width = map.clientWidth, height = map.clientHeight;
  const scale = Math.min((width - 70) / (right - left), (height - 60) / (bottom - top));
  const offsetX = (width - (right - left) * scale) / 2;
  const offsetY = (height - (bottom - top) * scale) / 2 - 4;
  displays.forEach((m, index) => {
    const frame = element('div', `screen-device ${m.primary ? 'primary' : ''}`);
    frame.style.left = `${offsetX + (m.bounds.x - left) * scale}px`;
    frame.style.top = `${offsetY + (m.bounds.y - top) * scale}px`;
    frame.style.width = `${m.bounds.width * scale}px`;
    frame.style.height = `${m.bounds.height * scale}px`;
    frame.setAttribute('aria-label', `${displayName(m)}，${m.primary ? '主屏' : '副屏'}，${m.bounds.width}乘${m.bounds.height}，缩放${m.scale}%`);
    const inner = element('div', 'screen-inner');
    inner.append(element('span', 'screen-watermark', String(index + 1).padStart(2, '0')));
    icons.filter(i => i.monitorId === m.id).forEach(i => {
      const dot = element('span', 'mini-icon');
      dot.style.left = `${(i.x - m.bounds.x) / m.bounds.width * 100}%`;
      dot.style.top = `${(i.y - m.bounds.y) / m.bounds.height * 100}%`;
      dot.title = i.name;
      inner.append(dot);
    });
    frame.append(inner);
    if (m.primary) frame.append(element('span', 'primary-tag', '主屏'));
    const caption = element('div', 'screen-caption');
    caption.append(element('span', 'screen-name', displayName(m)), element('span', 'screen-resolution', `${m.bounds.width} × ${m.bounds.height} · 缩放 ${m.scale}%`));
    frame.append(caption); map.append(frame);
  });
  $('screen-details').replaceChildren(screenTable(state.current));
  $('screen-count').textContent = String(displays.length);
  $('live-text').textContent = `${displays.length} 屏 · 已连接`;
  $('icon-count').textContent = `${icons.length} 个图标`;
  $('layout-label').textContent = displays.some(m => m.targetCount > 1) ? '复制显示' : displays.length > 1 ? '扩展桌面' : '单屏桌面';
}
function recordName(record) { return record.name || `${record.isSingleScreen ? '单屏' : '多屏'}布局 ${String(record.number).padStart(2, '0')}`; }
function recordNumber(record) { return `${record.isSingleScreen ? '单屏' : '多屏'} ${record.number} 号`; }
function renderRecord() {
  const list = $('record-list');
  const expanded = new Set([...list.querySelectorAll('details[open]')].map(el => el.dataset.recordId));
  list.replaceChildren();
  if (!state.records.length) {
    const empty = element('div', 'empty-record');
    const button = element('button', 'text-button', '保存当前布局'); button.onclick = save;
    empty.append(icon('layers'), element('h3', '', '给桌面留下第一个定格。'), button); list.append(empty);
  }
  for (const [label, single] of [['单屏布局', true], ['多屏布局', false]]) {
    const group = element('section', 'record-group');
    const heading = element('div', 'record-group-heading');
    heading.append(element('h3', '', label));
    if (single) {
      const button = element('button', 'text-button', '保存单屏布局');
      button.onclick = () => saveNow(null, true); heading.append(button);
    }
    group.append(heading);
    const records = state.records.filter(record => record.isSingleScreen === single);
    records.forEach(record => group.append(recordRow(record, expanded.has(record.id))));
    if (!records.length) group.append(element('p', 'group-empty', '暂无布局'));
    list.append(group);
  }
  $('record-count').textContent = `${state.records.length} 套布局`;
  $('active-layout').textContent = state.saved ? `当前选用 · ${recordNumber(state.saved)} · ${recordName(state.saved)}` : '尚未选用布局';
  $('save-changes').hidden = !state.changed;
}
function renderDesktopStatus(data) {
  const mode = data.current.displays.length === 1 ? '单屏' : '多屏';
  const name = data.saved ? recordName(data.saved) : '未选用布局';
  const warning = data.singleRecoveryFailed || data.needsSingleSetup || data.singleRecoveryPending || data.changed || !data.saved;
  const text = data.singleRecoveryFailed ? '恢复未完成' : data.needsSingleSetup ? '待设置' : data.singleRecoveryPending ? '等待恢复' : data.changed ? '布局有改动' : data.saved ? '布局一致' : '未保存';
  $('status-mode').textContent = mode;
  $('status-name').textContent = name;
  $('status-name').title = name;
  $('status-text').textContent = text;
  $('desktop-status').classList.toggle('warning', !!warning);
  $('desktop-status').setAttribute('aria-label', `${mode} · ${name} · ${text}，查看布局记录`);
}
function render(data) {
  state = data;
  document.documentElement.style.setProperty('--panel-gap', `${data.settings.panelGap}px`);
  renderScreens(); renderRecord(); renderDesktopStatus(data);
  $('save-button').disabled = false;
  $('restore-button').disabled = !data.saved;
  $('undo-button').disabled = !data.canUndo;
  const auto = (data.current.folderFlags & 1) !== 0;
  $('match-badge').textContent = auto ? '自动排列已开启' : data.saved ? (data.plan.exact ? '原位恢复' : '适配恢复') : '尚未保存';
  $('restore-caption').textContent = data.saved && !data.plan.exact ? '适配当前屏幕排列' : '回到保存的位置';
  $('action-status').textContent = data.saved ? `上次保存 · ${date(data.saved.savedAt)}` : '当前排列准备就绪';
  $('single-recovery').hidden = !data.singleRecoveryMessage;
  $('single-recovery-text').textContent = data.singleRecoveryMessage || '';
  $('single-retry').hidden = !data.singleRecoveryFailed;
  if (data.needsSingleSetup && !dialog.open) setTimeout(singleSetup, 0);
}
async function refresh() {
  await perform(async () => {
    if (!token) token = (await api('session')).token;
    render(await api('state'));
  });
}
let askedChange = '';
async function saveNow(after, single = state?.current.displays.length === 1) {
  await perform(async () => {
    render(await api('save', { single }));
    dialog.close(); toast(`已加入${recordNumber(state.saved)}布局`);
  });
  if (after && !state.changed) after();
}
function askChanged(after) {
  askedChange = `${state.current.configKey}:${state.desktopKey}`;
  modal('当前桌面布局已改变', 'NEW FREEZE');
  $('modal-body').append(element('p', '', '要将当前桌面加入新的历史记录吗？'));
  if (after) {
    modalButton('直接恢复', () => { dialog.close(); after(); }, true);
    modalButton('保存后恢复', () => saveNow(after));
  } else {
    modalButton('暂不保存', () => dialog.close(), true);
    modalButton('保存为新记录', () => saveNow());
  }
}
async function save() {
  if (state?.changed) askChanged();
  else await saveNow();
}
function selectRecord(id) {
  const record = state.records.find(r => r.id === id);
  if (!record) return;
  const restore = () => {
    const primary = state.current.displays.find(m => m.primary);
    const sourceIds = new Set(record.icons.map(i => i.monitorId));
    const sourceAvailable = state.current.displays.some(m => sourceIds.has(m.id));
    const needsTransfer = record.configKey !== state.current.configKey && sourceIds.size === 1
      && (!record.isSingleScreen || !sourceAvailable);
    if (needsTransfer) preview(id, false, primary.id);
    else preview(id);
  };
  if (state.changed) askChanged(restore);
  else restore();
}
function modal(title, kicker = 'DESKFRZA') {
  $('modal-close').hidden = false;
  $('modal-title').textContent = title; $('modal-kicker').textContent = kicker;
  $('modal-body').replaceChildren(); $('modal-actions').replaceChildren();
  if (!dialog.open) dialog.showModal();
}
function modalButton(label, callback, secondary = false) {
  const button = element('button', secondary ? 'outline-button' : 'solid-button', label);
  button.onclick = callback; $('modal-actions').append(button); return button;
}
async function setDefaultSingle(id = null) {
  await perform(async () => {
    render(await api('single-default', { id }));
    dialog.close(); toast('默认单屏布局已设置');
  });
}
function singleSetup() {
  if (!state?.needsSingleSetup || dialog.open) return;
  modal('先设置默认单屏布局', 'SINGLE SCREEN');
  $('modal-close').hidden = true;
  $('modal-body').append(element('p', '', '只剩一块屏幕时，自动恢复这套布局。'));
  const singles = state.records.filter(r => r.isSingleScreen);
  const candidates = singles.length ? singles : state.records.filter(r => new Set(r.icons.map(i => i.monitorId)).size <= 1 && r.displays.every(m => m.targetCount === 1));
  let select;
  if (candidates.length) {
    select = element('select', 'name-input'); select.setAttribute('aria-label', '默认单屏布局');
    candidates.forEach(record => {
      const option = element('option', '', recordName(record)); option.value = record.id; select.append(option);
    });
    $('modal-body').append(select);
  }
  const canSave = new Set(state.current.icons.map(i => i.monitorId)).size <= 1 && state.current.displays.every(m => m.targetCount === 1);
  if (!canSave) $('modal-body').append(element('p', '', '请将图标集中到一块屏幕，或先断开外接屏。'));
  if (select) {
    if (canSave) modalButton('保存当前布局', () => setDefaultSingle(), true);
    modalButton('使用所选布局', () => setDefaultSingle(select.value));
  } else if (canSave) modalButton('保存当前布局为默认', () => setDefaultSingle());
  else modalButton('重新读取桌面', () => perform(async () => { render(await api('state')); dialog.close(); }));
}
async function restoreFailure(error, id, undo, targetId, allowResize) {
  failure(error);
  try { render(await api('state')); }
  catch (refreshError) { $('modal-body').append(element('p', 'comparison-issue', `状态刷新失败：${refreshError.message}`)); }
  modalButton('重新预检', () => preview(id, undo, targetId, allowResize), true);
  if (state?.canUndo && !undo) modalButton('预览撤销', () => preview('', true));
}
function transferMap(saved, current, plan) {
  const root = element('div', 'transfer-preview');
  const target = current.displays.find(m => m.id === plan.transfer.targetId);
  const projected = current.icons.map(i => { const move = plan.moves.find(m => m.id === i.id); return move ? { ...i, ...move } : i; });
  for (const [label, screens, icons] of [['原布局', saved.displays.filter(m => saved.icons.some(i => i.monitorId === m.id)), saved.icons], ['迁移预览', [target], projected.filter(i => i.monitorId === target.id)]]) {
    const box = element('div', 'transfer-preview-box'); box.append(element('strong', '', label));
    const area = element('div', 'transfer-stage');
    const left = Math.min(...screens.map(s => s.bounds.x)), top = Math.min(...screens.map(s => s.bounds.y));
    const width = Math.max(...screens.map(s => s.bounds.x + s.bounds.width)) - left;
    const height = Math.max(...screens.map(s => s.bounds.y + s.bounds.height)) - top;
    area.style.aspectRatio = `${width} / ${height}`;
    icons.forEach(i => {
      const dot = element('span', 'transfer-dot'); dot.title = i.name;
      dot.style.left = `${(i.x - left) / width * 100}%`; dot.style.top = `${(i.y - top) / height * 100}%`;
      area.append(dot);
    });
    box.append(area); root.append(box);
  }
  return root;
}
async function preview(id, undo = false, targetId = null, allowResize = false) {
  await perform(async () => {
    let response = await api(undo ? 'undo-preview' : 'preview', undo ? {} : { id, targetId, allowResize });
    const { plan } = response;
    if (targetId) {
      const canResize = response.plan.blockedReason === '需要整体等比例缩小布局。';
      if (canResize) {
        response = await api('preview', { id, targetId, allowResize: true });
        if (!response.plan.blockedReason) allowResize = true;
      }
      const { current, desktopKey, saved } = response;
      const transferPlan = response.plan;
      modal(transferPlan.blockedReason ? '暂不能迁移' : allowResize ? '整体缩小后迁移？' : '将布局移到新主屏幕？', 'MOVE YOUR LAYOUT');
      if (transferPlan.blockedReason) {
        $('modal-body').append(element('p', 'comparison-issue', transferPlan.blockedReason));
        modalButton('知道了', () => dialog.close());
        return;
      }
      const report = transferPlan.transfer;
      const stats = element('div', 'preview-stats');
      stats.append(element('span', '', `${report.iconCount} 个图标`), element('span', '', report.scaleFactor < 1 ? `整体缩小至 ${Math.round(report.scaleFactor * 100)}%` : '间距保持不变'), element('span', '', '可撤销'));
      $('modal-body').append(stats, transferMap(saved, current, transferPlan));
      if (report.freePlacement) $('modal-body').append(element('p', '', '为保留原间距，迁移时会关闭“对齐到网格”。'));
      const sourceScreen = saved.displays.find(m => m.id === saved.icons[0]?.monitorId);
      const targetScreen = current.displays.find(m => m.id === targetId);
      const shortScreen = m => `屏幕 ${/DISPLAY(\d+)$/i.exec(m.device)?.[1] || displayName(m)}`;
      modalButton(sourceScreen && current.icons.every(i => i.monitorId === sourceScreen.id) ? `留在${shortScreen(sourceScreen)}` : '保持现状', () => dialog.close(), true);
      const confirm = modalButton(allowResize ? '缩小并迁移' : `移到${shortScreen(targetScreen)}`, () => perform(async () => {
        confirm.disabled = true;
        try { render(await api('restore', { id: transferPlan.snapshotId, currentKey: transferPlan.currentKey, desktopKey, targetId, allowResize, allowFreePlacement: report.freePlacement })); dialog.close(); toast('布局已迁移'); }
        catch (error) { await restoreFailure(error, id, undo, targetId, allowResize); }
      }));
      return;
    }
    const { current, desktopKey, saved } = response;
    if (plan.blockedReason) {
      modal('恢复已暂停', 'PREFLIGHT');
      let reason = saved.monitorSet !== current.monitorSet ? '当前启用的显示器组合与这条记录不同。' : plan.blockedReason;
      [...saved.displays, ...current.displays].forEach(m => { reason = reason.split(m.name).join(displayName(m)); });
      $('modal-body').append(element('p', '', reason), compareScreens(saved, current));
      if (plan.blockedReason === '请先关闭桌面的自动排列。') {
        modalButton('取消', () => dialog.close(), true);
        modalButton('关闭自动排列', () => perform(async () => {
          render(await api('auto-arrange-off', {})); dialog.close(); toast('自动排列已关闭，请再次点击恢复');
        }));
      } else {
        if (!undo && new Set(saved.icons.map(i => i.monitorId)).size === 1) {
          modalButton('取消', () => dialog.close(), true);
          modalButton('迁移到主屏', () => preview(id, false, current.displays.find(m => m.primary).id));
        } else modalButton('知道了', () => dialog.close());
      }
      return;
    }
    modal(undo ? '撤销上次恢复？' : plan.exact ? '回到保存的位置？' : '适配当前屏幕布局？', 'RESTORE LAYOUT');
    const stats = element('div', 'preview-stats');
    stats.append(element('span', '', `${plan.moves.filter(m => !m.isNew).length} 个原有图标`), element('span', '', `${current.displays.length} 块屏幕`));
    if (plan.newCount) stats.append(element('span', '', `${plan.newCount} 个新图标`));
    if (plan.missingCount) stats.append(element('span', '', `${plan.missingCount} 个已删除 · 跳过`));
    stats.append(element('span', '', '可撤销')); $('modal-body').append(stats);
    modalButton('取消', () => dialog.close(), true);
    const confirm = modalButton(undo ? '确认撤销' : '确认恢复', () => perform(async () => {
      confirm.disabled = true;
      try {
        render(await api(undo ? 'undo' : 'restore', { id: plan.snapshotId, currentKey: plan.currentKey, desktopKey }));
        dialog.close(); toast(undo ? '已回到恢复前的排列' : '布局已恢复');
      } catch (error) { await restoreFailure(error, id, undo, targetId, allowResize); }
    }));
  });
}
function recordRow(record, expanded = false) {
  const row = element('div', `layout-row ${record.id === state.saved?.id ? 'selected' : ''}`);
  const number = element('span', 'layout-number', String(record.number).padStart(2, '0'));
  const name = element('div', 'layout-name');
  name.append(element('strong', '', recordName(record)), element('span', '', `${fullDate(record.savedAt)} · ${record.displays.length} 屏 · ${record.icons.length} 个图标`));
  const actions = element('div', 'layout-row-actions');
  if (record.id === state.saved?.id) actions.append(element('span', 'record-tag', '当前选用'));
  if (record.id === state.defaultSingleId) actions.append(element('span', 'record-tag', '默认单屏'));
  else if (record.isSingleScreen) {
    const button = element('button', 'text-button', '设为默认');
    button.onclick = () => setDefaultSingle(record.id); actions.append(button);
  }
  for (const [label, callback] of [['恢复布局', () => selectRecord(record.id)], ['重命名', () => renameRecord(record)], ['删除', () => deleteRecord(record)]]) {
    const button = element('button', 'text-button', label); button.onclick = callback;
    if (label === '删除' && record.id === state.defaultSingleId) { button.disabled = true; button.title = '先将另一套单屏布局设为默认'; }
    actions.append(button);
  }
  row.append(number, name, actions);
  const wrapper = element('div', 'layout-entry');
  const details = element('details', 'record-screen-details'); details.dataset.recordId = record.id; details.open = expanded;
  const summary = element('summary', '', '保存时的屏幕配置'); summary.append(icon('chevron'));
  details.append(summary, screenTable(record)); wrapper.append(row, details); return wrapper;
}
function renameRecord(record) {
  modal(`重命名${recordNumber(record)}布局`, 'RENAME');
  const input = element('input', 'name-input'); input.value = recordName(record); input.maxLength = 60; input.setAttribute('aria-label', '布局名称');
  $('modal-body').append(input);
  modalButton('取消', () => dialog.close(), true);
  modalButton('保存名称', () => perform(async () => { render(await api('rename', { id: record.id, name: input.value })); dialog.close(); toast('名称已更新'); }));
  input.focus(); input.select();
}
function deleteRecord(record) {
  modal(`删除${recordNumber(record)}布局？`, 'DELETE');
  $('modal-body').append(element('p', '', `删除“${recordName(record)}”的保存记录，桌面图标保持原位。`));
  modalButton('取消', () => dialog.close(), true);
  modalButton('确认删除', () => perform(async () => { render(await api('delete', { id: record.id })); dialog.close(); toast('记录已删除'); }));
}
function settings() {
  modal('设置', 'MAKE IT YOURS');
  const row = element('div', 'settings-row'); const value = element('strong', '', `${state?.settings.panelGap ?? 48} px`);
  row.append(element('span', '', '面板间距'), value);
  const range = element('input'); range.type = 'range'; range.min = '24'; range.max = '96'; range.step = '4';
  range.value = String(state?.settings.panelGap ?? 48); range.setAttribute('aria-label', '面板间距');
  range.oninput = () => { value.textContent = `${range.value} px`; document.documentElement.style.setProperty('--panel-gap', `${range.value}px`); };
  $('modal-body').append(row, range);
  const updateRow = element('label', 'settings-row', '启动时检查更新');
  const checkUpdates = element('input'); checkUpdates.type = 'checkbox'; checkUpdates.checked = state?.settings.checkUpdates ?? true; checkUpdates.setAttribute('aria-label', '启动时检查更新'); updateRow.append(checkUpdates);
  const check = element('button', 'outline-button', '检查更新'); check.onclick = () => checkForUpdates(true);
  $('modal-body').append(updateRow, check);
  modalButton('默认 48 px', () => { range.value = '48'; range.oninput(); }, true);
  modalButton('保存设置', () => perform(async () => {
    const result = await api('settings', { panelGap: Number(range.value), checkUpdates: checkUpdates.checked });
    if (state) state.settings = result;
    dialog.close(); toast('面板间距已保存');
  }));
}
function about() {
  modal('DeskFrzA', 'ABOUT');
  $('modal-body').append(
    element('p', '', 'Windows 多屏桌面图标布局工具。'),
    element('p', '', '支持保存、恢复和管理布局，数据保存在本机。'),
    element('p', '', 'MIT 开源许可。'));
}
let updateInfo = null, updateBusy = false, updateCheckPromise = null;
async function checkForUpdates(manual = false) {
  if (!token || (updateBusy && !updateCheckPromise)) return;
  if (!updateCheckPromise) {
    updateBusy = true;
    updateCheckPromise = api('update/check', {}).then(result => {
      updateInfo = result;
      $('update-button').hidden = result.status !== 'available';
    }).finally(() => { updateBusy = false; updateCheckPromise = null; });
  }
  const checkButton = manual ? [...$('modal-body').querySelectorAll('button')].find(b => b.textContent === '检查更新') : null;
  if (checkButton) { checkButton.textContent = '正在检查…'; checkButton.disabled = true; }
  try {
    await updateCheckPromise;
    if (manual) showUpdate();
  } catch (error) {
    updateInfo = null; $('update-button').hidden = true;
    if (manual) { modal('无法检查更新', 'UPDATE'); $('modal-body').append(element('p', '', error.message)); modalButton('关闭', () => dialog.close()); }
  } finally { if (checkButton) { checkButton.textContent = '检查更新'; checkButton.disabled = false; } }
}
function showUpdate() {
  if (!updateInfo) return;
  modal(updateInfo.status === 'available' ? '有新版本可用' : '检查更新', 'UPDATE');
  const message = updateInfo.status === 'available' ? `DeskFrzA ${updateInfo.currentVersion} → ${updateInfo.latestVersion}` : updateInfo.status === 'unpublished' ? updateInfo.notes : `当前 ${updateInfo.currentVersion}，已是最新正式版本。`;
  $('modal-body').append(element('p', '', message));
  if (updateInfo.status === 'available' && updateInfo.notes) $('modal-body').append(element('p', 'release-notes', updateInfo.notes));
  modalButton('稍后', () => dialog.close(), true);
  if (updateInfo.status !== 'available') return;
  if (!updateInfo.canInstall) {
    $('modal-body').append(element('p', '', '当前为便携版。请下载新版便携包，关闭应用后解压替换；布局记录会保留。'));
    const link = element('a', 'solid-button', '打开下载页'); link.href = updateInfo.releaseUrl; link.target = '_blank'; link.rel = 'noopener noreferrer'; $('modal-actions').append(link);
    return;
  }
  $('modal-body').append(element('p', '', '更新会关闭并重新启动应用，保留布局记录和设置。'));
  const button = modalButton(updateInfo.ready ? '安装并重启' : '下载更新', async () => {
    if (updateBusy) return;
    updateBusy = true; button.disabled = true;
    try {
      if (!updateInfo.ready) {
        button.textContent = '正在下载并校验…';
        updateInfo = await api('update/download', { version: updateInfo.latestVersion });
        // Download completion does not authorize installation: offer a separate install action.
        showUpdate();
      } else {
        button.textContent = '正在启动更新…';
        await api('update/install', { version: updateInfo.latestVersion });
      }
    } catch (error) {
      if (updateInfo.ready) {
        try { updateInfo = await api('update/status'); } catch { updateInfo = { ...updateInfo, ready: false }; }
      }
      failure(error); button.textContent = updateInfo.ready ? '安装并重启' : '重试下载';
    }
    finally { updateBusy = false; button.disabled = false; }
  });
}
function askScreenChange() {
  askedScreen = screenChangeKey();
  const source = state.switchSource || state.saved;
  const primary = state.current.displays.find(m => m.primary);
  if (!source) return;
  if (new Set(source.icons.map(i => i.monitorId)).size !== 1) {
    modal('不建议切换主屏幕', 'DESKFRZA');
    $('modal-body').append(element('p', '', '图标分布在多个屏幕，切换可能打乱布局。'));
    modalButton('保留更改', () => dialog.close(), true);
    if (state.canUndoSwitch) modalButton('撤销切换', () => perform(async () => {
      render(await api('undo-screen-switch', { id: source.id, currentKey: state.current.configKey, desktopKey: state.desktopKey })); dialog.close(); toast('已恢复原主屏和布局');
    }));
    else $('modal-body').append(element('p', '', '无法直接撤销，请在 Windows 设置中恢复原主屏。'));
    return;
  }
  dialog.close();
  preview(source.id, false, primary.id);
}
let scrollPending = false;
function updateSticky() {
  scrollPending = false;
  const headerHeight = document.querySelector('main > section .panel-heading').getBoundingClientRect().height;
  const offset = document.querySelector('.topbar').getBoundingClientRect().height;
  document.querySelectorAll('main > section').forEach(section => {
    const heading = section.querySelector('.panel-heading');
    const r = heading.getBoundingClientRect();
    heading.classList.toggle('is-stuck', r.top <= offset + 1 && section.getBoundingClientRect().bottom > offset + headerHeight);
  });
}
hydrate();
$('refresh-button').onclick = refresh; $('retry-button').onclick = refresh;
$('save-button').onclick = save; $('save-changes').onclick = save;
$('restore-button').onclick = () => state?.saved && selectRecord(state.saved.id);
$('undo-button').onclick = () => preview('', true);
$('settings-button').onclick = settings; $('about-button').onclick = about;
$('update-button').onclick = showUpdate;
$('modal-close').onclick = () => dialog.close();
dialog.addEventListener('cancel', e => { if (state?.needsSingleSetup) e.preventDefault(); });
dialog.addEventListener('close', () => { if (state?.needsSingleSetup) setTimeout(singleSetup, 0); });
$('single-retry').onclick = () => perform(async () => { render(await api('single-retry', {})); toast('正在重新恢复单屏布局'); });
dialog.addEventListener('close', () => { if (state) document.documentElement.style.setProperty('--panel-gap', `${state.settings.panelGap}px`); });
dialog.addEventListener('click', e => { if (e.target === dialog) { const r = dialog.getBoundingClientRect(); if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) dialog.close(); } });
window.addEventListener('scroll', () => { if (!scrollPending) { scrollPending = true; requestAnimationFrame(updateSticky); } }, { passive: true });
new ResizeObserver(() => { renderScreens(); updateSticky(); }).observe($('screen-map'));
refresh().then(() => { if (state?.settings.checkUpdates) checkForUpdates(); });

let askedScreen = '';
function screenChangeKey() { return state.switchSource?.id || state.current.configKey; }
async function pollDesktop() {
  if (running || document.hidden || !token) return;
  await perform(async () => {
    render(await api('state'));
    const screenChanged = state.current.displays.length > 1 && (state.switchSource || (state.saved && state.baselinePrimaryId && state.baselinePrimaryId !== state.current.displays.find(m => m.primary).id));
    if (state.needsSingleSetup) return;
    if (state.current.displays.length === 1 && (state.singleRecoveryPending || state.singleRecoveryFailed)) return;
    if (screenChanged && askedScreen !== screenChangeKey()) setTimeout(askScreenChange, 0);
    else if (!dialog.open && state.changed && !screenChanged && askedChange !== `${state.current.configKey}:${state.desktopKey}`) askChanged();
    if (!state.changed) askedChange = '';
    if (!screenChanged) askedScreen = '';
  }, true);
}
setInterval(pollDesktop, 2000);
window.addEventListener('deskfrza-screen-switch', () => { if (running) actionFinished.then(pollDesktop); else pollDesktop(); });
document.addEventListener('visibilitychange', () => { if (!document.hidden) pollDesktop(); });
setInterval(() => { if (!document.hidden && !dialog.open && state?.settings.checkUpdates) checkForUpdates(); }, 6 * 60 * 60 * 1000);

const brandLetters = $('brand-letters');
const brandWord = 'DeskFrzA...';
const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
let brandTimer;
function typeBrand(index = 0) {
  clearTimeout(brandTimer);
  if (document.hidden || reducedMotion.matches) { brandLetters.textContent = brandWord; return; }
  brandLetters.textContent = brandWord.slice(0, index);
  brandTimer = setTimeout(() => typeBrand(index === brandWord.length ? 0 : index + 1),
    index === brandWord.length ? 3000 : index === 0 ? 650 : 230);
}
document.addEventListener('visibilitychange', () => typeBrand());
reducedMotion.addEventListener('change', () => typeBrand());
typeBrand();
