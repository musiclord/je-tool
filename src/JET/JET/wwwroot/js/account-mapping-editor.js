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
        nextCursor: null, search: '', searchDraft: '', selected: Object.create(null), anchor: null, category: '',
        loading: false, error: '', notice: '', taxonomyRevision: taxonomyRevision(state), droppedCount: 0 };
    }
    dropDeletedCategories(model, state);
    return model;
  }
  function taxonomyRevision(state) { return state.taxonomy ? state.taxonomy.revision : null; }
  // 分類設定刪掉分類後，要套用該分類的待儲存改動已經存不進去，拿掉並記下取消了幾個科目；其他待儲存改動保留
  // （使用者 2026-10-15 在待決事項勾選頁裁定）。分類清單的版本沒變時不檢查。
  function dropDeletedCategories(m, state) {
    var revision = taxonomyRevision(state);
    if (m.taxonomyRevision === revision) { return; }
    m.taxonomyRevision = revision;
    var known = Object.create(null);
    Ui.taxonomyCategories(state).forEach(function (c) { known[c.categoryId] = true; });
    var dropped = Object.keys(m.changes).filter(function (code) { return !known[m.changes[code]]; });
    dropped.forEach(function (code) { delete m.changes[code]; });
    if (m.category && !known[m.category]) { m.category = ''; }
    m.droppedCount += dropped.length;
  }
  function dirty(m) { return Object.keys(m.changes).length > 0; }
  var PAGE_SIZE = Ui.TABLE_PAGE_SIZE; // 每頁科目數，與一次儲存上限分開。
  var SAVE_BATCH = 500; // accountMapping.save 每次最多 500 筆，超過時分批送出。
  function load(m) {
    if (m.loading) { return Promise.resolve(); }
    // 換頁或搜尋不清掉勾選與待儲存的分類；只重設以本頁列號計算的連選起點。
    // 第一次讀取時畫面已經寫著正在載入，不再要求整個中欄重建：進入第四步時這次重建讓換步驟多頓七、八十毫秒（使用者 2026-10-15）。
    var showingLoading = !m.rows && !m.error;
    m.loading = true; m.error = ''; m.rows = null; m.nextCursor = null; m.anchor = null;
    if (!showingLoading) { Store.touch(); }
    var requestedSearch = m.search;
    var requestedCursor = m.cursor;
    return global.JetApi.queryAccountMappingPage({ cursor: m.cursor, pageSize: PAGE_SIZE, search: m.search })
      .then(function (data) {
        if (model !== m) { return; }
        m.rows = data.rows; m.nextCursor = data.nextCursor;
        if (data.totalCount != null) { m.totalCount = data.totalCount; }
        m.rows.forEach(function (row) { m.saved[row.accountCode] = row.categoryId; });
      }).catch(function (error) {
        if (model === m) { m.error = Ui.errorText(error, '科目清單載入失敗，請重試。'); }
      }).finally(function () {
        if (model !== m) { return; }
        m.loading = false;
        if (m.search !== requestedSearch || m.cursor !== requestedCursor) { load(m); } else { Store.touch(); }
      });
  }
  function html(state, ready) {
    var m = current(state);
    if (!ready) { return '<p class="rule-card__gate">GL 確認配對後，即可設定科目分類。</p>'; }
    var categories = Ui.taxonomyTree(state);
    var rows = (m.rows || []).map(function (row, index) {
      var selected = Object.prototype.hasOwnProperty.call(m.changes, row.accountCode)
        ? m.changes[row.accountCode] : row.categoryId;
      var category = categories.find(function (c) { return c.categoryId === selected; });
      return '<tr' + (m.selected[row.accountCode] ? ' class="account-editor__row--selected"' : '') + '><td class="account-editor__select-cell" data-account-select-cell><input type="checkbox" data-account-select="' + index + '" data-focus-key="account-select-' + index +
        '" aria-label="選取 ' + Ui.esc(row.accountCode + ' ' + (row.accountName || '')) + '"' +
        (m.selected[row.accountCode] ? ' checked' : '') + '></td><td>' + Ui.esc(row.accountCode) +
        '</td><td>' + Ui.esc(row.accountName || '—') + '</td><td data-account-current="' + index +
        '" data-category-id="' + Ui.esc(selected || '') + '">' + Ui.esc(category ? category.label : '尚未配對') +
        (Object.prototype.hasOwnProperty.call(m.changes, row.accountCode) ? '<span class="account-editor__pending">待儲存</span>' : '') + '</td></tr>';
    }).join('');
    return '<section class="account-editor" data-bind="account-editor" aria-label="本案科目分類">' +
      Ui.searchBoxHtml('搜尋科目編號或名稱', { className: 'account-editor__search', attributes: { 'data-account-search': true },
        inputAttributes: { name: 'search', 'data-focus-key': 'account-search' }, maxLength: Ui.SEARCH_TEXT_MAX_LENGTH, value: m.searchDraft, disabled: m.loading }) +
      '<div class="account-editor__batch"><div class="account-editor__selection"><span role="status" data-account-selection-count>' + selectionText(m) + '</span>' +
      // 「全部取消」只在有勾選時出現；標 data-collapsible-inline，出現與消失時左右收放，旁邊的字跟著平順移動
      // （app.css，使用者 2026-10-13 要求展開與收合一致）。
      '<button type="button" class="btn btn--ghost btn--tiny account-editor__clear" data-account-clear-selection data-collapsible-inline' +
        (Object.keys(m.selected).length ? '' : ' hidden') + '>全部取消</button></div>' +
      // 第四步只選一個分類，用下拉選單；上下層以全形空白與「└」縮排（使用者 2026-10-15 要求改回下拉，第五步分類樹不變）。
      '<label><span class="visually-hidden">套用分類</span><select class="form__select" data-account-bulk-category data-focus-key="account-bulk-category">' +
        '<option value="">請選擇分類</option>' + categories.map(function (c) {
          return '<option value="' + Ui.esc(c.categoryId) + '"' + (m.category === c.categoryId ? ' selected' : '') + '>' +
            Ui.esc('　'.repeat(c.depth) + (c.depth ? '└ ' : '') + c.label) + '</option>';
        }).join('') + '</select></label><button type="button" class="btn btn--ghost" data-account-apply' +
        (!m.category || !Object.keys(m.selected).length || m.loading ? ' disabled' : '') + '>套用至已選科目</button></div>' +
      (m.error ? Ui.loadFailureHtml(m.error, { 'data-account-retry': true }) : '') +
      (m.droppedCount ? '<p class="form-notice form-notice--warning" data-account-dropped role="status">有 ' + m.droppedCount +
        ' 個科目要套用的分類已在分類設定中刪除，這些科目的改動已取消。</p>' : '') +
      (m.loading || !m.rows ? (m.error ? '' : '<p role="status">正在載入科目…</p>') :
        '<p class="account-editor__selection-hint">拖曳勾選欄可連選；Shift 點選可選取區間。</p>' +
        '<div class="account-editor__table" data-preserve-scroll="account-editor-table"><table class="preview-table"><thead><tr><th scope="col">' +
        '<label class="account-editor__select-all"><input type="checkbox" data-account-select-page aria-label="全選本頁科目"' +
        (!m.rows.length ? ' disabled' : '') +
        (m.rows.length && m.rows.every(function (row) { return m.selected[row.accountCode]; }) ? ' checked' : '') + '><span>本頁</span></label>' +
        '</th><th>科目編號</th><th>科目名稱</th><th>目前分類</th></tr></thead><tbody>' +
        (rows || '<tr><td colspan="4">沒有符合的科目。</td></tr>') + '</tbody></table></div>') +
      '<div class="account-editor__footer">' + Ui.pageControlsHtml(m.previous.length + 1, m.totalCount, {
        previousAttribute: 'data-account-prev', nextAttribute: 'data-account-next', busy: m.loading, canNext: !!m.nextCursor
      }) +
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
    card.querySelector('[data-account-bulk-category]').addEventListener('change', function (event) {
      m.category = event.currentTarget.value; Store.touch();
    });
    card.querySelector('[data-account-apply]').addEventListener('click', function () {
      if (!m.category || m.loading || !Ui.taxonomyCategories(Store.getState()).some(function (c) { return c.categoryId === m.category; })) { return; }
      Object.keys(m.selected).forEach(function (code) {
        if (m.category === m.saved[code]) { delete m.changes[code]; }
        else { m.changes[code] = m.category; }
      });
      m.notice = ''; m.droppedCount = 0; m.selected = Object.create(null); m.anchor = null; Store.touch();
    });
    card.querySelector('[data-account-save]').addEventListener('click', function () {
      if (!dirty(m)) { return; }
      Ui.run('儲存科目分類', function () { return saveChanges(m); });
    });
    card.querySelector('[data-account-cancel]').addEventListener('click', function () { m.changes = Object.create(null); m.selected = Object.create(null); m.anchor = null; m.notice = ''; m.droppedCount = 0; Store.touch(); });
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
      // 第一次讀取時搜尋框沒有重畫成停用，讀取中送出的搜尋先記下，讀完再照它重讀（load 的 finally）。
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
        // 存檔後讀不回科目配對摘要時，回應不帶摘要欄位：保留畫面上原本的摘要與比對用的批次，
        // 只套用失效結果並顯示提醒（使用者 2026-10-19 裁定）。
        if (data.batchId) {
          // setter 同步重繪前先對齊新批次，分類設定改名不丟棄尚未保存的選擇。
          m.key = key(Object.assign({}, Store.getState(), { importState:
            Object.assign({}, Store.getState().importState, { accountMapping: data }) }));
          Store.setAccountMappingState(data);
        }
        Store.applyMutationEffects(data);
        (data.warnings || []).forEach(function (warning) { Store.addMessage(warning, 'warn'); });
        last = data;
        return saveBatch(index + 1);
      });
    }
    return saveBatch(0).then(function () {
      if (model === m && !dirty(m)) {
        m.selected = Object.create(null); m.anchor = null; m.notice = '科目分類已儲存。'; m.droppedCount = 0; Store.touch();
      }
      return last;
    });
  }

  function pendingCount() {
    var state = Store.getState();
    if (!model || !state.project || model.key !== key(state)) { return 0; }
    dropDeletedCategories(model, state);
    return Object.keys(model.changes).length;
  }

  // 儲存科目分類會清除預篩選結果與第五步的篩選情境，連還沒存的條件也會清掉，對話框要先寫明。
  function workClearedBySave() {
    var state = Store.getState();
    return {
      prescreen: !!(state.lastRuns && state.lastRuns.prescreen),
      scenarios: Store.hasFilterWork(state.filter)
    };
  }

  function clearingNote(cleared) {
    if (cleared.prescreen && cleared.scenarios) { return '儲存會清除預篩選結果和第五步的篩選情境。'; }
    if (cleared.scenarios) { return '儲存會清除第五步的篩選情境。'; }
    if (cleared.prescreen) { return '儲存會清除預篩選結果。'; }
    return '';
  }

  // 離開第四步、執行預篩選或離開案件前，如果還有沒儲存的分類就先問，用第五步篩選情境也在用的共用對話框
  // （Ui.confirmUnsaved）。回傳 Promise<boolean>：true 表示可以繼續。
  // consequence 說明不儲存會怎樣，continueLabel 是「不儲存也繼續」那顆按鈕的文字。
  // options.saveable 為 false 時不提供「儲存並繼續」：匯入 GL 或 TB、確認配對與匯入科目配對檔本身就會清除或取代
  // 已儲存的分類，先存也留不住（使用者 2026-10-15 選擇「拿掉儲存，只留兩個按鈕」）。
  function confirmPending(consequence, continueLabel, options) {
    var count = pendingCount();
    if (!count) { return Promise.resolve(true); }
    var saveable = !(options && options.saveable === false);
    var cleared = saveable ? workClearedBySave() : { prescreen: false, scenarios: false };
    return Ui.confirmUnsaved({
      id: 'account-pending',
      title: '科目分類尚未儲存',
      body: '有 ' + count + ' 個科目的分類尚未儲存。' + consequence + clearingNote(cleared),
      continueLabel: continueLabel || '先不儲存',
      saveLabel: '儲存並繼續',
      // 儲存失敗時錯誤由 Ui.run 顯示。
      save: saveable ? function () {
        return Ui.run('儲存科目分類', function () { return saveChanges(model); })
          .then(function () { return pendingCount() === 0; });
      } : null,
      // 會清除篩選情境或已儲存的分類時，預設停在「留在這裡」，按 Enter 不會直接清掉。
      focusStay: cleared.scenarios || !saveable
    });
  }

  Ui.accountMappingEditor = { html: html, bind: bind, pendingCount: pendingCount, confirmPending: confirmPending };
  Ui.registerWorkflowReset(function () { model = null; });
})(window);
