(function (global) {
  'use strict';
  var Ui = global.JetUi, Store = global.JetStore;
  var model = null;
  function key(state) {
    return JSON.stringify([state.project.projectId, state.importState.gl && state.importState.gl.batchId,
      state.importState.tb && state.importState.tb.batchId,
      state.importState.accountMapping && state.importState.accountMapping.batchId,
        state.mapping.gl.committed, state.mapping.tb.committed, state.accountMappingGeneration]);
  }
  function current(state) {
    if (!model || model.key !== key(state)) {
      // 勾選與待儲存的分類都跨頁保留（使用者 2026-10-08 裁定「跨頁保留，一次儲存」）；
      // saved 記住每個載入過的科目目前已儲存的分類，套用到其他頁的勾選時才判斷得出是否真的有改。
      model = { key: key(state), rows: null, changes: Object.create(null), saved: Object.create(null), cursor: null, previous: [],
        nextCursor: null, search: '', searchDraft: '', selected: Object.create(null), anchor: null, category: '', categoryOpen: false,
        categoryTree: { collapsed: {}, focusKey: 'account-bulk' }, loading: false, error: '', notice: '' };
    }
    return model;
  }
  function dirty(m) { return Object.keys(m.changes).length > 0; }
  var PAGE_SIZE = 50; // 每頁科目數，與一次儲存上限分開。
  var SAVE_BATCH = 500; // accountMapping.save 每次最多 500 筆，超過時分批送出。
  function load(m) {
    if (m.loading) { return Promise.resolve(); }
    // 換頁或搜尋不清掉勾選與待儲存的分類；只重設以本頁列號計算的連選起點。
    m.loading = true; m.error = ''; m.rows = null; m.nextCursor = null; m.anchor = null; Store.touch();
    return global.JetApi.queryAccountMappingPage({ cursor: m.cursor, pageSize: PAGE_SIZE, search: m.search })
      .then(function (data) {
        if (model !== m) { return; }
        m.rows = data.rows; m.nextCursor = data.nextCursor;
        m.rows.forEach(function (row) { m.saved[row.accountCode] = row.categoryId; });
      }).catch(function (error) {
        if (model === m) { m.error = error.message || '科目清單載入失敗，請重試。'; }
      }).finally(function () {
        if (model === m) { m.loading = false; Store.touch(); }
      });
  }
  function html(state, ready) {
    var m = current(state);
    if (!ready) { return '<p class="rule-card__gate">完成 GL 欄位配對後，即可設定科目分類。</p>'; }
    var categories = Ui.taxonomyTree(state);
    var rows = (m.rows || []).map(function (row, index) {
      var selected = Object.prototype.hasOwnProperty.call(m.changes, row.accountCode)
        ? m.changes[row.accountCode] : row.categoryId;
      var category = categories.find(function (c) { return c.categoryId === selected; });
      return '<tr' + (m.selected[row.accountCode] ? ' class="account-editor__row--selected"' : '') + '><td data-account-select-cell><input type="checkbox" data-account-select="' + index + '" data-focus-key="account-select-' + index +
        '" aria-label="選取 ' + Ui.esc(row.accountCode + ' ' + (row.accountName || '')) + '"' +
        (m.selected[row.accountCode] ? ' checked' : '') + '></td><td>' + Ui.esc(row.accountCode) +
        '</td><td>' + Ui.esc(row.accountName || '—') + '</td><td data-account-current="' + index +
        '" data-category-id="' + Ui.esc(selected || '') + '">' + Ui.esc(category ? category.label : '尚未配對') +
        (Object.prototype.hasOwnProperty.call(m.changes, row.accountCode) ? '<span class="account-editor__pending">待儲存</span>' : '') + '</td></tr>';
    }).join('');
    return '<section class="account-editor" data-bind="account-editor" aria-label="本案科目分類">' +
      Ui.searchBoxHtml('搜尋科目編號或名稱', { className: 'account-editor__search', attributes: { 'data-account-search': true },
        inputAttributes: { name: 'search', 'data-focus-key': 'account-search' }, maxLength: 400, value: m.searchDraft, disabled: m.loading }) +
      '<div class="account-editor__batch"><div class="account-editor__selection"><span role="status" data-account-selection-count>' + selectionText(m) + '</span>' +
      // 「全部取消」只在有勾選時出現；標 data-collapsible-inline，出現與消失時左右收放，旁邊的字跟著平順移動
      // （app.css，使用者 2026-10-13 要求展開與收合一致）。
      '<button type="button" class="btn btn--ghost btn--tiny" data-account-clear-selection data-collapsible-inline' +
        (Object.keys(m.selected).length ? '' : ' hidden') + '>全部取消</button></div>' +
      '<details class="account-editor__categories" data-account-category-tree' + (m.categoryOpen ? ' open' : '') + '><summary data-focus-key="account-bulk-category">套用分類：' +
        Ui.esc(m.category ? Ui.taxonomyCategoryLabel(state, m.category) : '請選擇分類') + '</summary>' +
        Ui.taxonomyTreeHtml(state, m.categoryTree, function (category) {
          return '<label class="category-option"><input type="radio" name="account-bulk-category" data-account-bulk-category value="' + Ui.esc(category.categoryId) + '"' +
            (m.category === category.categoryId ? ' checked' : '') + '><span>' + Ui.esc(category.label) + '</span></label>';
        }) + '</details><button type="button" class="btn btn--ghost" data-account-apply' +
        (!m.category || !Object.keys(m.selected).length || m.loading ? ' disabled' : '') + '>套用至已選科目</button></div>' +
      (m.error ? Ui.loadFailureHtml(m.error, { 'data-account-retry': true }) : '') +
      (m.loading || !m.rows ? (m.error ? '' : '<p role="status">正在載入科目…</p>') :
        '<p class="account-editor__selection-hint">拖曳勾選欄可連選；Shift 點選可選取區間。</p>' +
        '<div class="account-editor__table" data-preserve-scroll="account-editor-table"><table class="preview-table"><thead><tr><th scope="col">' +
        '<label class="account-editor__select-all"><input type="checkbox" data-account-select-page aria-label="全選本頁科目"' +
        (!m.rows.length ? ' disabled' : '') +
        (m.rows.length && m.rows.every(function (row) { return m.selected[row.accountCode]; }) ? ' checked' : '') + '><span>本頁</span></label>' +
        '</th><th>科目編號</th><th>科目名稱</th><th>目前分類</th></tr></thead><tbody>' +
        (rows || '<tr><td colspan="4">沒有符合的科目。</td></tr>') + '</tbody></table></div>') +
      '<div class="account-editor__footer"><div class="account-editor__pager"><button class="btn btn--ghost" data-account-prev' +
        (!m.previous.length || m.loading ? ' disabled' : '') + '>上一頁</button>' +
        '<span class="account-editor__page">第 ' + (m.previous.length + 1) + ' 頁' + (m.rows && m.rows.length && !m.loading && !m.nextCursor ? '（最後一頁）' : '') +
          (m.rows && m.rows.length ? '<span class="account-editor__range">本頁第 ' + (m.previous.length * PAGE_SIZE + 1) + '–' + (m.previous.length * PAGE_SIZE + m.rows.length) + ' 個</span>' : '') + '</span><button class="btn btn--ghost" data-account-next' +
        (!m.nextCursor || m.loading ? ' disabled' : '') + '>下一頁</button></div>' +
        '<div class="panel__actions">' +
        '<button class="btn btn--ghost" data-account-cancel' + (!dirty(m) || m.loading ? ' disabled' : '') + '>取消變更</button>' +
        '<button class="btn" data-account-save' + (!dirty(m) || m.loading ? ' disabled' : '') + '>儲存科目分類</button></div></div>' +
      '<p class="account-editor__notice" role="status">' + (dirty(m) ? '共 ' + Object.keys(m.changes).length +
        ' 個科目待儲存，換頁不會遺失；按「儲存科目分類」後才會用在預篩選與後續步驟。' : Ui.esc(m.notice)) + '</p></section>';
  }
  function selectionText(m) {
    var total = Object.keys(m.selected).length;
    var onPage = (m.rows || []).filter(function (row) { return m.selected[row.accountCode]; }).length;
    return '已選 ' + total + ' 個科目' + (total > onPage ? '（其中 ' + (total - onPage) + ' 個在其他頁）' : '');
  }
  // Selection is UI-only. During a drag, keep a reversible preview outside the model;
  // neither pointer movement nor checkbox changes rebuild the table or write categories.
  function bindAccountSelection(card, m) {
    var rows = m.rows || [];
    var boxes = Array.from(card.querySelectorAll('[data-account-select]'));
    var table = card.querySelector('.account-editor__table');
    var selectPage = card.querySelector('[data-account-select-page]');
    var count = card.querySelector('[data-account-selection-count]');
    var apply = card.querySelector('[data-account-apply]');
    var clear = card.querySelector('[data-account-clear-selection]');
    var drag = null, frame = null, suppressClick = false;
    function sync(selection) {
      boxes.forEach(function (box) {
        box.checked = !!selection[rows[Number(box.getAttribute('data-account-select'))].accountCode];
        box.closest('tr').classList.toggle('account-editor__row--selected', box.checked);
      });
      var size = Object.keys(selection).length;
      var onPage = boxes.filter(function (box) { return box.checked; }).length;
      if (count) { count.textContent = selectionText({ selected: selection, rows: rows }); }
      // 「全部取消」展開時上方那一排會變高，把表格往下推；拖曳途中游標下的列會跟著跑掉，所以放開後才出現或收起
      // （2026-10-14 Gui 在拖曳連選時查到）。
      if (clear && !drag) { clear.hidden = !size; }
      if (selectPage) {
        selectPage.checked = !!boxes.length && onPage === boxes.length;
        selectPage.indeterminate = onPage > 0 && onPage < boxes.length;
      }
      if (apply) { apply.disabled = !m.category || !size || m.loading; }
    }
    function range(base, from, to, checked) {
      var selected = Object.assign(Object.create(null), base);
      for (var i = Math.min(from, to); i <= Math.max(from, to); i++) {
        if (checked) { selected[rows[i].accountCode] = true; }
        else { delete selected[rows[i].accountCode]; }
      }
      return selected;
    }
    boxes.forEach(function (checkbox) {
      var shift = false;
      checkbox.addEventListener('click', function (event) { shift = event.shiftKey; });
      checkbox.addEventListener('change', function () {
        if (m.rows !== rows || m.loading) { return; }
        var index = Number(checkbox.getAttribute('data-account-select'));
        var from = shift && m.anchor !== null ? m.anchor : index;
        m.selected = range(m.selected, from, index, shift || checkbox.checked);
        if (!shift || m.anchor === null) { m.anchor = index; }
        shift = false; sync(m.selected);
      });
    });
    if (selectPage && m.rows) {
      selectPage.addEventListener('change', function () {
        if (m.rows !== rows || m.loading) { return; }
        // 只切換本頁的勾選，其他頁已勾的科目保留。
        m.selected = Object.assign(Object.create(null), m.selected); m.anchor = null;
        m.rows.forEach(function (row) {
          if (selectPage.checked) { m.selected[row.accountCode] = true; } else { delete m.selected[row.accountCode]; }
        });
        sync(m.selected);
      });
    }
    sync(m.selected);
    if (!table || !boxes.length) { return sync; }
    function finish(commit) {
      if (!drag) { return; }
      var finished = drag; drag = null;
      if (frame !== null) { global.cancelAnimationFrame(frame); frame = null; }
      if (commit && table.isConnected && m.rows === rows && !m.loading) { m.selected = finished.selection; m.anchor = finished.anchor; }
      table.classList.toggle('account-editor__table--selecting', false);
      sync(m.selected);
      if (table.hasPointerCapture(finished.id)) { table.releasePointerCapture(finished.id); }
    }
    function updateRange() {
      var index = 0;
      boxes.forEach(function (box, i) {
        if (drag.y >= box.closest('tr').getBoundingClientRect().top) { index = i; }
      });
      drag.selection = range(drag.base, drag.anchor, index, drag.checked);
      sync(drag.selection);
    }
    function scrollFrame() {
      frame = null;
      if (!drag) { return; }
      if (!table.isConnected || m.rows !== rows || m.loading) { finish(false); return; }
      var rect = table.getBoundingClientRect(), header = table.querySelector('thead').getBoundingClientRect();
      var top = Math.max(rect.top, header.bottom, 0), bottom = Math.min(rect.bottom, global.innerHeight || rect.bottom);
      var delta = drag.y < top + 24 ? -12 : drag.y > bottom - 24 ? 12 : 0;
      if (delta && drag.x >= rect.left && drag.x <= rect.right) {
        table.scrollTop = Math.max(0, Math.min(table.scrollTop + delta, table.scrollHeight - table.clientHeight));
        updateRange();
      }
      frame = global.requestAnimationFrame(scrollFrame);
    }
    table.addEventListener('pointerdown', function (event) {
      suppressClick = false;
      // Touch keeps native scrolling and checkbox taps; mouse/pen start in the selection gutter only.
      if (m.rows !== rows || m.loading || event.button !== 0 || event.isPrimary === false || event.pointerType === 'touch') { return; }
      var cell = event.target.closest('[data-account-select-cell]');
      if (!cell) { return; }
      var checkbox = cell.querySelector('[data-account-select]');
      var index = Number(checkbox.getAttribute('data-account-select'));
      var anchor = event.shiftKey && m.anchor !== null ? m.anchor : index;
      drag = { id: event.pointerId, anchor: anchor, base: m.selected, checked: event.shiftKey || !checkbox.checked,
        x: event.clientX, y: event.clientY };
      drag.selection = range(drag.base, anchor, index, drag.checked);
      suppressClick = true; event.preventDefault(); checkbox.focus({ preventScroll: true });
      table.setPointerCapture(event.pointerId);
      table.classList.toggle('account-editor__table--selecting', true);
      sync(drag.selection); frame = global.requestAnimationFrame(scrollFrame);
    });
    table.addEventListener('pointermove', function (event) {
      if (!drag || drag.id !== event.pointerId) { return; }
      if (!(event.buttons & 1)) { finish(false); return; }
      drag.x = event.clientX; drag.y = event.clientY; updateRange();
    });
    table.addEventListener('pointerup', function (event) { if (drag && event.pointerId === drag.id) { finish(true); } });
    table.addEventListener('pointercancel', function () { finish(false); });
    table.addEventListener('lostpointercapture', function () { finish(false); });
    table.addEventListener('keydown', function (event) { if (drag && event.key === 'Escape') { event.preventDefault(); finish(false); } });
    table.addEventListener('click', function (event) {
      if (suppressClick && event.detail > 0) { suppressClick = false; event.preventDefault(); event.stopPropagation(); }
    }, true);
    return sync;
  }
  function bind(container) {
    var card = container.querySelector('[data-bind="account-editor"]');
    if (!card) { return; }
    var m = current(Store.getState());
    var syncSelection = bindAccountSelection(card, m);
    var categoryTree = card.querySelector('[data-account-category-tree]');
    Ui.bindTaxonomyPicker(categoryTree, Store.getState(), m.categoryTree);
    categoryTree.addEventListener('toggle', function () { m.categoryOpen = categoryTree.open; });
    card.querySelectorAll('[data-account-bulk-category]').forEach(function (control) {
      control.addEventListener('change', function (event) {
        if (!event.currentTarget.checked) { return; }
        m.category = event.currentTarget.value; m.categoryOpen = false; Store.touch();
        if (global.JetFocus) { global.JetFocus.defer(function () { return document.querySelector('[data-account-category-tree] summary'); }); }
      });
    });
    card.querySelector('[data-account-apply]').addEventListener('click', function () {
      if (!m.category || m.loading || !Ui.taxonomyCategories(Store.getState()).some(function (c) { return c.categoryId === m.category; })) { return; }
      Object.keys(m.selected).forEach(function (code) {
        if (m.category === m.saved[code]) { delete m.changes[code]; }
        else { m.changes[code] = m.category; }
      });
      m.notice = ''; m.selected = Object.create(null); m.anchor = null; Store.touch();
    });
    card.querySelector('[data-account-save]').addEventListener('click', function () {
      if (!dirty(m)) { return; }
      Ui.run('儲存科目分類', function () { return saveChanges(m); });
    });
    card.querySelector('[data-account-cancel]').addEventListener('click', function () { m.changes = Object.create(null); m.selected = Object.create(null); m.anchor = null; m.notice = ''; Store.touch(); });
    card.querySelector('[data-account-clear-selection]').addEventListener('click', function () {
      // 清除前先讓勾選的列輕微快速地晃一下，讓使用者知道哪些勾選被拿掉（使用者 2026-10-13）。
      // 勾選只在畫面上，所以就地取消勾選（和逐列勾選相同），不整段重繪；重繪會把正在晃的列換掉。
      if (global.JetMotion && typeof global.JetMotion.shake === 'function') {
        global.JetMotion.shake(Array.from(card.querySelectorAll('tr.account-editor__row--selected')));
      }
      m.selected = Object.create(null); m.anchor = null;
      if (syncSelection) { syncSelection(m.selected); } else { Store.touch(); }
    });
    card.querySelector('[data-account-prev]').addEventListener('click', function () {
      if (!m.loading && m.previous.length) { m.cursor = m.previous.pop(); load(m); }
    });
    card.querySelector('[data-account-next]').addEventListener('click', function () {
      if (!m.loading && m.nextCursor) { m.previous.push(m.cursor); m.cursor = m.nextCursor; load(m); }
    });
    Ui.bindSearchBox(card.querySelector('[data-account-search]'), function (text) {
      if (m.loading) { return; }
      m.search = text; m.searchDraft = text; m.cursor = null; m.previous = []; load(m);
    }, function (text) { m.searchDraft = text; });
    var retry = card.querySelector('[data-account-retry]');
    if (retry) { retry.addEventListener('click', function () { load(m); }); }
    if (!m.rows && !m.loading && !m.error) { load(m); }
  }
  // 依 500 筆分批儲存。每批成功就從待儲存移除並更新已儲存分類；中途失敗時，已存的批次保留，
  // 其餘仍待儲存，錯誤交給 Ui.run 顯示，使用者可以再按一次儲存。
  function saveChanges(m) {
    var codes = Object.keys(m.changes);
    var batches = [];
    for (var i = 0; i < codes.length; i += SAVE_BATCH) { batches.push(codes.slice(i, i + SAVE_BATCH)); }
    var last = null;
    // 第一批在按下儲存的同一輪送出；之後依序一批接一批。
    function saveBatch(index) {
      if (index >= batches.length || model !== m || key(Store.getState()) !== m.key) { return Promise.resolve(); }
      var batch = batches[index];
      return global.JetApi.accountMappingSave({ changes: batch.map(function (code) {
        return { accountCode: code, categoryId: m.changes[code] };
      }) }).then(function (data) {
        if (model !== m || key(Store.getState()) !== m.key) { return; }
        batch.forEach(function (code) { m.saved[code] = m.changes[code]; delete m.changes[code]; });
        (m.rows || []).forEach(function (row) {
          if (Object.prototype.hasOwnProperty.call(m.saved, row.accountCode)) { row.categoryId = m.saved[row.accountCode]; }
        });
        // setter 同步重繪前先對齊新批次，分類設定改名不丟棄尚未保存的選擇。
        m.key = key(Object.assign({}, Store.getState(), { importState:
          Object.assign({}, Store.getState().importState, { accountMapping: data }) }));
        Store.setAccountMappingState(data);
        Store.applyMutationEffects(data);
        last = data;
        return saveBatch(index + 1);
      });
    }
    return saveBatch(0).then(function () {
      if (model === m && !dirty(m)) {
        m.selected = Object.create(null); m.anchor = null; m.notice = '科目分類已儲存。'; Store.touch();
      }
      return last;
    });
  }

  function pendingCount() {
    var state = Store.getState();
    if (!model || !state.project || model.key !== key(state)) { return 0; }
    return Object.keys(model.changes).length;
  }

  // 儲存科目分類會清除預篩選結果與第五步的篩選情境，連還沒存的條件也會清掉，對話框要先寫明。
  function workClearedBySave() {
    var state = Store.getState();
    var filter = state.filter || {};
    return {
      prescreen: !!(state.lastRuns && state.lastRuns.prescreen),
      scenarios: (filter.savedScenarios || []).length > 0 || ((filter.draft && filter.draft.groups) || []).length > 0
    };
  }

  function clearingNote(cleared) {
    if (cleared.prescreen && cleared.scenarios) { return '儲存會清除預篩選結果和第五步的篩選情境。'; }
    if (cleared.scenarios) { return '儲存會清除第五步的篩選情境。'; }
    if (cleared.prescreen) { return '儲存會清除預篩選結果。'; }
    return '';
  }

  var confirmationOpen = false;

  // 離開第四步、執行預篩選或離開案件前，如果還有沒儲存的分類就先問。回傳 Promise<boolean>：true 表示可以繼續。
  // consequence 說明不儲存會怎樣，continueLabel 是「不儲存也繼續」那顆按鈕的文字。
  // 同一時間只開一個對話框；已經開著時，新的要求當成「留在這裡」。
  function confirmPending(consequence, continueLabel) {
    var count = pendingCount();
    if (!count) { return Promise.resolve(true); }
    if (confirmationOpen) { return Promise.resolve(false); }
    confirmationOpen = true;
    var cleared = workClearedBySave();
    return new Promise(function (resolve) {
      function finish(result) {
        confirmationOpen = false;
        resolve(result);
      }
      var previousFocus = document.activeElement;
      var modal = document.createElement('div');
      modal.className = 'modal';
      modal.setAttribute('data-bind', 'account-pending-modal');
      modal.innerHTML =
        '<div class="modal__backdrop" data-pending-choice="stay"></div>' +
        '<div class="modal__card" role="dialog" aria-modal="true" aria-labelledby="account-pending-title" aria-describedby="account-pending-body">' +
          '<h3 class="modal__title" id="account-pending-title">科目分類尚未儲存</h3>' +
          '<p class="modal__body" id="account-pending-body">有 ' + count + ' 個科目的分類尚未儲存。' +
            Ui.esc(consequence) + Ui.esc(clearingNote(cleared)) + '</p>' +
          '<div class="modal__actions">' +
            '<button type="button" class="btn btn--ghost" data-pending-choice="stay">留在這裡</button>' +
            '<button type="button" class="btn btn--ghost" data-pending-choice="continue">' + Ui.esc(continueLabel || '先不儲存') + '</button>' +
            '<button type="button" class="btn" data-pending-choice="save">儲存並繼續</button>' +
          '</div>' +
        '</div>';
      function detach() {
        document.removeEventListener('keydown', onKey, true);
        // 對話框淡出後才移除（見 app.js 的 JetMotion.dismiss），不一瞬間消失。
        if (global.JetMotion) { global.JetMotion.dismiss(modal); } else { modal.remove(); }
      }
      function close(result) {
        detach();
        if (previousFocus && previousFocus.isConnected && previousFocus.focus) { previousFocus.focus(); }
        finish(result);
      }
      function onKey(event) {
        if (event.key === 'Escape') { event.preventDefault(); close(false); return; }
        var card = modal.querySelector('.modal__card');
        if (global.JetFocus && card) { global.JetFocus.trapDialogTab(card, event); }
      }
      modal.addEventListener('click', function (event) {
        var target = event.target.closest('[data-pending-choice]');
        if (!target) { return; }
        var choice = target.getAttribute('data-pending-choice');
        if (choice === 'stay') { close(false); return; }
        if (choice === 'continue') { close(true); return; }
        detach();
        // 儲存失敗時留在原畫面，錯誤由 Ui.run 顯示，焦點回到原本的按鈕。
        Ui.run('儲存科目分類', function () { return saveChanges(model); })
          .then(function () {
            var saved = pendingCount() === 0;
            if (!saved && previousFocus && previousFocus.isConnected && previousFocus.focus) { previousFocus.focus(); }
            finish(saved);
          }, function () { finish(false); });
      });
      document.addEventListener('keydown', onKey, true);
      document.body.appendChild(modal);
      // 會清除篩選情境時，預設停在「留在這裡」，按 Enter 不會直接清掉。
      var primary = modal.querySelector(cleared.scenarios ? '.modal__actions [data-pending-choice="stay"]' : '[data-pending-choice="save"]');
      if (primary) { primary.focus(); }
    });
  }

  Ui.accountMappingEditor = { html: html, bind: bind, pendingCount: pendingCount, confirmPending: confirmPending };
  Ui.registerWorkflowReset(function () { model = null; });
})(window);
