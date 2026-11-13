'use strict';
// No packages or Windows desktop access. --serve exposes synthetic API fixtures
// for exercising the unmodified dashboard in a real browser.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '../DeskFrz/wwwroot');
const source = fs.readFileSync(path.join(root, 'app.js'), 'utf8').replace(/\r\n/g, '\n');
const selectSource = source.slice(source.indexOf('function selectRecord(id)'), source.indexOf('function modal(title'));
const calls = [];
const context = vm.createContext({
  state: { changed: false, records: [], current: { configKey: 'now', displays: [{ id: 'a', primary: false }, { id: 'b', primary: true }] } },
  preview: (...args) => calls.push(args),
  askChanged: after => { calls.push('changed'); after(); }
});
vm.runInContext(selectSource, context);
function route(record, expected, changed = false) {
  calls.length = 0;
  context.state.records = [record]; context.state.changed = changed;
  vm.runInContext(`selectRecord(${JSON.stringify(record.id)})`, context);
  assert.deepEqual(calls, changed ? ['changed', expected] : [expected]);
}
const multi = { id: 'multi', configKey: 'old-grid', isSingleScreen: false, icons: [{ monitorId: 'a' }] };
route(multi, ['multi']);
route({ ...multi, isSingleScreen: true }, ['multi']);
route({ ...multi, icons: [{ monitorId: 'missing' }] }, ['multi', false, 'b']);
route({ ...multi, icons: [{ monitorId: 'a' }, { monitorId: 'b' }] }, ['multi']);
route({ ...multi, icons: [] }, ['multi']);
route(multi, ['multi'], true);
console.log('Passed 6 frontend restore-routing checks.');

async function checkUpdateStartup() {
  const initialize = source.slice(source.indexOf('async function initializeUpdates()'), source.indexOf('async function checkForUpdates('));
  const button = { hidden: true };
  let checks = 0, status;
  const updateContext = vm.createContext({ state: { settings: { checkUpdates: true } },
    api: async endpoint => { assert.equal(endpoint, 'update/status'); return status; },
    $: id => { assert.equal(id, 'update-button'); return button; },
    checkForUpdates: async () => { checks++; }
  });
  vm.runInContext('let updateInfo = null;\n' + initialize, updateContext);
  status = { status: 'available', lastCheckedAt: new Date().toISOString() };
  await vm.runInContext('initializeUpdates()', updateContext);
  assert.equal(checks, 0); assert.equal(button.hidden, false);
  status = { status: 'current', lastCheckedAt: new Date(Date.now() - 7 * 60 * 60 * 1000).toISOString() };
  await vm.runInContext('initializeUpdates()', updateContext);
  assert.equal(checks, 1);
  status = { status: 'unchecked', lastCheckedAt: null };
  await vm.runInContext('initializeUpdates()', updateContext);
  assert.equal(checks, 2);
  updateContext.state.settings.checkUpdates = false;
  await vm.runInContext('initializeUpdates()', updateContext);
  assert.equal(checks, 2);
  console.log('Passed 4 dashboard-reopen update checks.');
}

async function checkBackgroundUI() {
  const button = { disabled: false, attributes: {}, setAttribute(name,value) { this.attributes[name]=value; } };
  const tooltip = { textContent: '' };
  const requests = [];
  const controls = vm.createContext({state:{settings:{panelGap:56,checkUpdates:false,backgroundEnabled:false}},
    $:id => id==='background-toggle' ? button : tooltip,
    perform:async action=>action(),
    api:async (endpoint,body)=>{ requests.push({endpoint,body}); return {panelGap:56,checkUpdates:false,backgroundEnabled:body.backgroundEnabled}; }
  });
  const controlsSource=source.slice(source.indexOf('function renderBackgroundMode('),source.indexOf('async function refresh()'));
  vm.runInContext(controlsSource,controls);
  vm.runInContext('renderBackgroundMode(state.settings)',controls);
  assert.equal(button.attributes['aria-checked'],'false'); assert.equal(tooltip.textContent,'是否开启后台挂载：当前为否');
  vm.runInContext('renderBackgroundMode({backgroundEnabled:true})',controls);
  assert.equal(button.attributes['aria-checked'],'true'); assert.equal(tooltip.textContent,'是否开启后台挂载：当前为是');
  await vm.runInContext('toggleBackgroundMode()',controls);
  assert.equal(requests[0].endpoint,'settings'); assert.equal(requests[0].body.backgroundEnabled,true); assert.equal(button.disabled,false);
  await vm.runInContext('toggleBackgroundMode()',controls);
  assert.equal(requests[1].body.backgroundEnabled,false); assert.equal(controls.state.settings.checkUpdates,false);

  let renders=0,savePrompts=0,screenPrompts=0;
  const polling=vm.createContext({running:false,document:{hidden:false},token:'test',
    api:async endpoint=>{ assert.equal(endpoint,'state'); return {}; },
    perform:async action=>action(),render:()=>renders++,refresh:async()=>{},
    state:{settings:{backgroundEnabled:false},changed:true,saved:{},current:{displays:[{}]}},
    dialog:{open:false},pendingDesktopReview:null,askChanged:()=>savePrompts++,askScreenChange:()=>screenPrompts++,failure:error=>{throw error;}
  });
  const pollingSource=source.slice(source.indexOf('async function pollDesktop()'),source.indexOf('setInterval(pollDesktop'));
  vm.runInContext(pollingSource,polling);
  await vm.runInContext('pollDesktop()',polling);
  assert.equal(renders,1); assert.equal(savePrompts+screenPrompts,0);
  const reviewSource=source.slice(source.indexOf('async function reviewDesktopNotification('),source.indexOf("window.addEventListener('deskfrza-review-changes'"));
  vm.runInContext(reviewSource,polling);
  await vm.runInContext('reviewDesktopNotification()',polling);
  assert.equal(savePrompts,0);
  polling.state.settings.backgroundEnabled=true;
  await vm.runInContext('reviewDesktopNotification()',polling);
  assert.equal(savePrompts,1);
  polling.state.current.displays=[{},{}]; polling.state.switchSource={};
  await vm.runInContext('reviewDesktopNotification()',polling);
  assert.equal(savePrompts,2); assert.equal(screenPrompts,0);
  await vm.runInContext("reviewDesktopNotification('screen')",polling);
  assert.equal(screenPrompts,1);
  let closeReview,queuedReview;
  polling.dialog.open=true;
  polling.dialog.addEventListener=(_,callback)=>{ closeReview=callback; };
  polling.setTimeout=callback=>{ queuedReview=callback; };
  await vm.runInContext('reviewDesktopNotification()',polling);
  assert.equal(polling.pendingDesktopReview,'desktop'); assert.equal(savePrompts,2);
  const deferredStart=source.indexOf("dialog.addEventListener('close', () => {\n  if (!pendingDesktopReview)");
  assert.notEqual(deferredStart,-1,'missing deferred notification review handler');
  vm.runInContext(source.slice(deferredStart,source.indexOf('\n});',deferredStart)+4),polling);
  polling.dialog.open=false; closeReview(); queuedReview();
  await new Promise(resolve=>setImmediate(resolve));
  assert.equal(savePrompts,3); assert.equal(polling.pendingDesktopReview,null);
  const markup=fs.readFileSync(path.join(root,'index.html'),'utf8');
  assert.ok(markup.indexOf('id="background-toggle"')<markup.indexOf('class="icon-button header-github"'));
  assert.match(markup,/role="switch"/);
  console.log('Passed 11 resident-mode frontend checks.');
}

const screen = (id, x, primary) => ({ id, name: id === 'a' ? '测试屏幕 A' : '测试屏幕 B', device: `DISPLAY${id === 'a' ? 1 : 2}`,
  primary, bounds: { x, y: 0, width: 1800, height: 1000 }, workArea: { x, y: 0, width: 1800, height: 1000 }, scale: 100, rotation: 1, targetCount: 1 });
const displays = [screen('a', -1800, false), screen('b', 0, true)];
const icons = Array.from({ length: 70 }, (_, n) => ({ id: `i${n}`, name: `测试图标 ${n}`, monitorId: 'b', x: 26 + n % 10 * 100, y: 2 + Math.floor(n / 10) * 100 }));
const snapshot = (id, screens, items, flags = 4) => ({ id, number: id === 'multi' ? 1 : 2, name: id === 'multi' ? '双屏历史 50 图标' : '单屏自由摆放', savedAt: '2026-11-13T00:00:00Z',
  configKey: id, monitorSet: screens.map(s => s.id).sort().join('|'), displays: screens, icons: items,
  spacing: { x: 100, y: 100 }, viewOrigin: { x: -1800, y: 0 }, iconSize: 48, folderFlags: flags, isSingleScreen: screens.length === 1 });
const old = snapshot('multi', displays, icons.slice(0, 50).map(i => ({ ...i, monitorId: 'a', x: i.x - 1800 })), 0);
const single = snapshot('single', [{ ...displays[0], primary: true }], icons.slice(0, 50).map(i => ({ ...i, monitorId: 'a', x: i.x - 1796, y: i.y + 8 })), 0);
const initial = snapshot('current', displays, icons);
const records = [old, single];
let current = structuredClone(initial), saved = old, backup = null;
const log = [];
const settings = { panelGap:48, checkUpdates:false, backgroundEnabled:false };
const planFor = record => ({ snapshotId: record.id, currentKey: current.configKey, exact: false,
  moves: record.icons.map(i => ({ ...i, isNew: false })), newCount: 70 - record.icons.length, missingCount: 0, blockedReason: null,
  gridAlignment: record.id === 'single' ? false : null, transfer: null });
const state = () => ({ current, saved, records, defaultSingleId: 'single',
  singleRecoveryMessage: null, singleRecoveryFailed: false, singleRecoveryPending: false, changed: false,
  baselineKey: current.configKey, baselinePrimaryId: 'b', desktopKey: current.id, canUndo: backup !== null,
  settings, plan: planFor(saved), switchSource: null, canUndoSwitch: false });
const json = (res, data, code = 200) => { res.writeHead(code, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); res.end(JSON.stringify(data)); };
const server = http.createServer(async (req, res) => {
  try {
    if (req.url === '/__audit') return json(res, { log, state: state() });
    if (req.url.startsWith('/api/')) {
      const chunks = []; for await (const chunk of req) chunks.push(chunk);
      const body = chunks.length ? JSON.parse(Buffer.concat(chunks)) : {};
      if (req.method === 'POST') log.push({ path: req.url, body });
      if (req.url === '/api/session') return json(res, { token: 'synthetic-browser-test', version: 'test' });
      if (req.url === '/api/state') return json(res, state());
      if (req.url === '/api/settings') { Object.assign(settings,body); return json(res,settings); }
      if (req.url === '/api/update/status') return json(res, { status: 'unchecked', lastCheckedAt: null });
      if (req.url === '/api/preview') {
        const record = records.find(r => r.id === body.id);
        if (body.targetId) throw Error('Ordinary history was incorrectly routed to transfer.');
        return json(res, { saved: record, current, desktopKey: current.id, plan: planFor(record) });
      }
      if (req.url === '/api/restore') {
        const record = records.find(r => r.id === body.id);
        if (record.id === 'single' && body.allowFreePlacement !== true) throw Error('Grid consent was not forwarded.');
        if (body.targetId) throw Error('Unexpected target override.');
        backup = structuredClone(current); saved = record;
        const moves = planFor(record).moves;
        current = { ...current, id: `after-${record.id}`, configKey: `after-${record.id}`, folderFlags: record.id === 'single' ? 0 : current.folderFlags,
          icons: current.icons.map(i => ({ ...i, ...moves.find(m => m.id === i.id) })) };
        return json(res, state());
      }
      if (req.url === '/api/undo-preview') return json(res, { saved: backup, current, desktopKey: current.id, plan: { ...planFor(backup), gridAlignment: true, newCount: 0 } });
      if (req.url === '/api/undo') { current = backup; backup = null; saved = old; return json(res, state()); }
      throw Error(`Unexpected fixture API: ${req.url}`);
    }
    const pathname = req.url.split('?')[0];
    const name = pathname === '/' ? 'index.html' : pathname.slice(1);
    if (!['index.html', 'app.js', 'styles.css', 'mark.png'].includes(name)) { res.writeHead(404); return res.end(); }
    const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.png': 'image/png' };
    res.writeHead(200, { 'Content-Type': types[path.extname(name)], 'Cache-Control': 'no-store' }); fs.createReadStream(path.join(root, name)).pipe(res);
  } catch (error) { json(res, { error: error.message }, 409); }
});
checkUpdateStartup().then(checkBackgroundUI).then(() => {
  if (process.argv.includes('--serve'))
    server.listen(0, '127.0.0.1', () => console.log(`Synthetic browser test: http://127.0.0.1:${server.address().port}`));
}).catch(error => { console.error(error); process.exitCode = 1; });
