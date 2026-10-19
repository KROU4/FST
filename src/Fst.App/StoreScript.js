(() => {
  'use strict';
  const version = 5;
  if (window.__FSTStore?.version === version) return;
  window.__FSTStore?.dispose?.();
  const bridge = window.__FST = { version: 1, requests: {}, responses: {} };
  let disposed = false, currentApp = '', card = null, job = null;
  let status = { installed: [], busy: false }, connectionError = '';
  let timer, scheduled, polling = false, submitting = false;
  let nextRequest = 0;
  const english = {
    'Подключить Ryuu':'Connect Ryuu','Подготовка…':'Preparing…','Добавлено в библиотеку':'Added to library',
    'Бесплатно добавить в библиотеку':'Add to library for free','Войдите через Discord':'Sign in with Discord',
    'Скачивание манифеста…':'Downloading manifest…','Установка манифеста…':'Installing manifest…',
    'Перезапуск Steam…':'Restarting Steam…','Готово. Steam перезапускается.':'Done. Restarting Steam.',
    'Не удалось добавить игру':'Could not add game','Выполняется другая установка. Дождитесь её завершения.':'Another installation is running. Please wait.',
    'Denuvo не указан':'Denuvo not declared','По данным страницы Steam':'According to the Steam store page',
    'FST недоступен. Проверьте значок утилиты в трее.':'FST is unavailable. Check its tray icon.',
    'Нет ответа от FST. Проверьте значок утилиты в трее.':'No response from FST. Check its tray icon.',
    'Откройте подключение Ryuu через значок FST в трее.':'Open the Ryuu connection from the FST tray menu.'
  };
  const t = value => status.language === 'en' ? (english[value] || value) : value;
  const labels = {
    downloading: 'Скачивание манифеста…', installing: 'Установка манифеста…',
    restarting: 'Перезапуск Steam…', complete: 'Готово. Steam перезапускается.',
    failed: 'Не удалось добавить игру'
  };
  const appId = () => /^\/app\/(\d+)(?:\/|$)/.exec(location.pathname)?.[1] || '';
  function rpc(method, args = {}) {
    const id = `fst-${Date.now()}-${++nextRequest}`;
    bridge.requests[id] = { method, args };
    const deadline = Date.now() + 15000;
    return new Promise((resolve, reject) => {
      function check() {
        if (Object.prototype.hasOwnProperty.call(bridge.responses, id)) {
          const response = bridge.responses[id];
          delete bridge.responses[id];
          if (response.error) reject(new Error(response.error));
          else resolve(response.result);
          return;
        }
        if (disposed || Date.now() >= deadline) {
          delete bridge.requests[id];
          reject(new Error('Нет ответа от FST. Проверьте значок утилиты в трее.'));
          return;
        }
        setTimeout(check, 100);
      }
      check();
    });
  }
  function render() {
    if (!card || !card.isConnected) return;
    const button = card.querySelector('button');
    const note = card.querySelector('[data-fst-message]');
    const installed = (status.installed || []).some(id => String(id) === currentApp);
    const active = submitting || (job && !['complete', 'failed'].includes(job.state));
    const state = job?.state;
    const buttonText = status.connected === false ? 'Подключить Ryuu' : active ? (labels[state] || 'Подготовка…') :
      (installed || state === 'complete' ? 'Добавлено в библиотеку' : 'Бесплатно добавить в библиотеку');
    const message = (status.connected === false ? 'Войдите через Discord' : '') || job?.message || (state ? labels[state] : '') || connectionError ||
      (status.busy ? 'Выполняется другая установка. Дождитесь её завершения.' : '');
    if (button.textContent !== t(buttonText)) button.textContent = t(buttonText);
    if (note.textContent !== t(message)) note.textContent = t(message);
    note.hidden = !message;
    const drm = card.querySelector('[data-fst-drm]');
    const hasDenuvo = Array.from(document.querySelectorAll('.DRM_notice')).some(el => /denuvo/i.test(el.textContent));
    const drmText = hasDenuvo ? 'Denuvo' : '';
    if (drm.textContent !== drmText) drm.textContent = drmText;
    drm.dataset.present = String(hasDenuvo);
    drm.hidden = !hasDenuvo;
    drm.title = t('По данным страницы Steam');
    button.disabled = Boolean(active || (status.connected !== false && (installed || state === 'complete')) || status.busy || connectionError);
    card.dataset.error = String(state === 'failed' || Boolean(connectionError));
    button.setAttribute('aria-busy', String(Boolean(active)));
  }
  async function install() {
    if (status.connected === false) { try { await rpc('connect'); } catch { connectionError = 'Откройте подключение Ryuu через значок FST в трее.'; render(); } return; }
    if (submitting || job && !['complete', 'failed'].includes(job.state)) return;
    const id = currentApp;
    submitting = true; job = null; render();
    try {
      const result = await rpc('install', { appid: id });
      if (!result.jobId) throw new Error('Сервис не вернул идентификатор установки.');
      job = { id: result.jobId, appid: id, state: 'downloading', message: '' };
      connectionError = '';
    } catch (error) {
      job = { appid: id, state: 'failed', message: error.message || 'Ошибка соединения с FST.' };
    } finally { submitting = false; render(); }
    refresh();
  }
  function reconcile() {
    if (disposed) return;
    const id = appId();
    if (id !== currentApp) {
      card?.remove(); card = null; currentApp = id;
      if (job?.appid !== id) job = null;
    }
    if (!id) return;
    if (!card?.isConnected) {
      const host = document.querySelector('#game_area_purchase') ||
        document.querySelector('.game_area_purchase') || document.querySelector('.game_area_purchase_game');
      if (!host) return;
      card = document.createElement('section');
      card.id = 'fst-store-action';
      card.setAttribute('aria-label', 'FST');
      const button = document.createElement('button');
      button.type = 'button'; button.addEventListener('click', install);
      const note = document.createElement('div');
      note.dataset.fstMessage = ''; note.setAttribute('role', 'status');
      note.setAttribute('aria-live', 'polite');
      const actions = document.createElement('div'); actions.dataset.fstActions = '';
      const drm = document.createElement('span'); drm.dataset.fstDrm = ''; drm.title = 'По данным страницы Steam';
      actions.append(button, drm); card.append(actions, note); host.prepend(card);
    }
    render();
  }
  async function refresh() {
    if (disposed || polling) return;
    polling = true;
    try {
      status = await rpc('status');
      if (job?.id && !['complete', 'failed'].includes(job.state)) {
        const activeJob = job;
        const update = await rpc('job', { jobId: activeJob.id });
        if (job === activeJob) {
          job = { ...activeJob, ...update, id: activeJob.id, appid: activeJob.appid };
          connectionError = '';
        }
      } else {
        connectionError = '';
      }
    } catch (error) {
      if (!disposed) connectionError = 'FST недоступен. Проверьте значок утилиты в трее.';
    } finally { polling = false; reconcile(); }
  }
  const style = document.createElement('style');
  style.id = 'fst-store-style';
  style.textContent = `
    #fst-store-action { box-sizing:border-box; margin:0 0 16px; padding:16px;
      background:linear-gradient(110deg,#273c4d,#1b2838); border:1px solid #3b5265;
      border-radius:3px; color:#c7d5e0; font:13px/1.45 Arial,sans-serif; }
    #fst-store-action button { border:0; border-radius:3px; padding:10px 20px;
      background:linear-gradient(90deg,#75b022,#588a1b); color:#fff; font:16px Arial,sans-serif;
      cursor:pointer; box-shadow:0 1px 3px #0005; }
    #fst-store-action [data-fst-actions] { display:flex; align-items:center; gap:18px; flex-wrap:wrap; }
    #fst-store-action [data-fst-drm] { color:#9ba6ad; font-size:12px; }
    #fst-store-action [data-fst-drm][data-present="true"] { color:#e4ba89; border:1px solid #846747; padding:4px 8px; border-radius:2px; }
    #fst-store-action button { padding-left:40px; background-color:#668f25; background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Cpath fill='%23fff' d='M7 7h18v4H11v3h10v4H11v7H7zm15 15h3v3h-3z'/%3E%3C/svg%3E"); background-position:10px center; background-size:22px; background-repeat:no-repeat; }
    #fst-store-action button:hover:not(:disabled) { background-color:#80b735; }
    #fst-store-action button:focus-visible { outline:2px solid #c7e6ff; outline-offset:3px; }
    #fst-store-action button:disabled { cursor:default; opacity:.65; }
    #fst-store-action [data-fst-message] { margin-top:9px; color:#a9bac7; overflow-wrap:anywhere; }
    #fst-store-action[data-error="true"] [data-fst-message] { color:#f3b9ad; }
  `;
  (document.head || document.documentElement).append(style);
  const observer = new MutationObserver(() => {
    clearTimeout(scheduled); scheduled = setTimeout(reconcile, 120);
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });
  timer = setInterval(() => { reconcile(); refresh(); }, 1500);
  window.__FSTStore = {
    version,
    dispose() {
      disposed = true; observer.disconnect(); clearInterval(timer); clearTimeout(scheduled);
      bridge.requests = {}; bridge.responses = {}; card?.remove(); style.remove();
    }
  };
  reconcile(); refresh();
})();
