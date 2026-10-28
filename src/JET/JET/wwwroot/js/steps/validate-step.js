/*
  第四步：資料驗證與測試，四項資料驗證與預篩選在同一步。
  規則名稱對齊後端 RuleCatalog（V/R 代號已退役）；
  標題與說明只是 presentation，權威規則一律在後端 SQL。
  規則無法執行時只顯示後端寫的一句原因，句子已寫出缺什麼、到哪裡補，畫面不另接補救文字。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  function naStatus(rule) {
    return { tone: 'na', text: '無法執行', tip: rule.naReason };
  }
  function countText(value) { return value == null ? '—' : Number(value).toLocaleString(); }
  var differencePrecisionNote = '差額顯示為「不足 0.01」時，完整數字請見驗證報告。';

  function withControlTotals(validation, detail) {
    var totals = validation.completenessTest.partA || {}, source = totals.eligibleSource || {}, target = totals.effectiveTarget || {};
    var control = {
      kind: 'table',
      // 2026-10-05 V2：兩欄都是 JET 自己算的數字，一欄在確認配對時算出，一欄在存下後重新計算；不比對來源檔本身。
      columns: ['核對項目', '確認配對時算出', '存下後重新計算'],
      numeric: [1, 2],
      rows: [['分錄筆數', countText(source.rowCount), countText(target.rowCount)],
        ['借方合計', Ui.money(source.totalDebit), Ui.money(target.totalDebit)],
        ['貸方合計', Ui.money(source.totalCredit), Ui.money(target.totalCredit)]]
    };
    return { kind: 'sections', sections: detail ? [control, detail] : [control] };
  }

  // 完整性測試的第一個檢查：存下的分錄筆數與借貸合計，和確認配對時算出的是否一致。後端 partA 缺漏時所有值為 null，不算不符。
  function controlTotalsMismatch(validation) {
    var partA = validation.completenessTest.partA;
    return !!partA && (partA.rowCountMatch === false || partA.amountMatch === false);
  }

  // 命中型規則（row-tag）：命中是「值得留意」而非「錯誤」。
  // rule 為 null/undefined 表示這次 run 不含此規則（resume 舊版結果缺新增鍵）→ 視為「未執行」，
  // 不可讓缺鍵的舊結果在 render 階段丟例外而拖垮整個步驟。
  function hitCountStatus(rule, tip) {
    if (!rule) { return null; }
    if (rule.naReason) { return naStatus(rule); }
    if (rule.count == null) { return null; }
    var n = Number(rule.count);
    return n > 0
      ? { tone: 'hit', text: n.toLocaleString() + ' 筆', tip: tip }
      : { tone: 'ok', text: '未發現', tip: tip };
  }

  // 過帳／核准成對計數（週末／假日規則）。naReason 只在整張卡無法執行時才有；只有核准側沒有檢查時
  // approvalCount 為 null，提示用後端的 approvalNaReason，說明只檢查了總帳日期。
  // rule 缺漏（resume 舊版結果）→「未執行」，理由同 hitCountStatus。
  function pairCountStatus(rule) {
    if (!rule) { return null; }
    if (rule.naReason) { return naStatus(rule); }
    if (rule.postingCount == null && rule.approvalCount == null) { return null; }
    var approval = countText(rule.approvalCount);
    var any = Number(rule.postingCount) > 0 || Number(rule.approvalCount) > 0;
    return {
      tone: any ? 'hit' : 'ok',
      text: '過帳 ' + countText(rule.postingCount) + '，核准 ' + approval,
      tip: rule.approvalNaReason || undefined
    };
  }

  // 週末與假日卡的明細用同一個方法：整張卡無法執行時只顯示後端的原因；只有核准側沒有檢查時，
  // 照常列出總帳日期那一側的分錄，前面放後端那句「只檢查了總帳日期」的說明。
  function activityDetail(activity, postingKey, approvalKey) {
    if (activity.naReason) { return { kind: 'reason', text: activity.naReason }; }
    var previews = [];
    if (Number(activity.postingCount) > 0) { previews.push({ label: '過帳側', key: postingKey }); }
    if (activity.approvalCount != null && Number(activity.approvalCount) > 0) { previews.push({ label: '核准側', key: approvalKey }); }
    var sections = [];
    if (activity.approvalNaReason) { sections.push({ kind: 'reason', text: activity.approvalNaReason }); }
    if (previews.length > 0) { sections.push({ kind: 'prescreenPreview', previews: previews }); }
    if (sections.length === 0) { return null; }
    return sections.length === 1 ? sections[0] : { kind: 'sections', sections: sections };
  }

  // 預篩選命中首頁快取：以 runId + '|' + prescreenKey 為鍵；完整明細仍由使用者以 keyset 逐頁載入。
  // 每次新的 run（新 runId）自然不命中快取，重新抓取。
  var prescreenPreviewCache = {};
  var detailPreviewProject = null;
  var detailPreviewGeneration = null;
  var detailPreviewRuns = {};
  var detailViews = { project: null, generation: null, runs: {} };

  // 只保存目前案件、資料世代與結果的畫面狀態；列資料僅來自使用者實際載入的有界分頁。
  function detailView(runKind) {
    var state = Store.getState();
    if (detailViews.project !== state.project || detailViews.generation !== state.dataGeneration) {
      detailViews = { project: state.project, generation: state.dataGeneration, runs: {} };
    }
    var view = detailViews.runs[runKind];
    if (!view || view.run !== state.lastRuns[runKind]) {
      view = { run: state.lastRuns[runKind], expanded: {}, pages: {} };
      detailViews.runs[runKind] = view;
    }
    return view;
  }

  function detailRunKind(scope) { return scope === 'p' || scope === 'ps' ? 'prescreen' : 'validate'; }

  function detailPageState(runKind, key) {
    var view = detailView(runKind);
    if (!view.pages[key]) { view.pages[key] = {}; }
    return view.pages[key];
  }

  function detailPageKey(element, suffix) {
    var panel = element.closest('.rule-detail');
    return (panel ? panel.getAttribute('data-bind') : 'detail') + ':' + suffix;
  }

  // 查詢頁是唯讀，使用區塊自己的載入狀態；不爭用全畫面的 mutation busy。
  // 同一請求可接回重繪後的新區塊；案件、資料或執行結果改變後，晚到回應作廢。
  function loadDetailPreview(body, key, runKind, run, fetchPage, display) {
    var state = Store.getState();
    if (detailPreviewProject !== state.project || detailPreviewGeneration !== state.dataGeneration) {
      prescreenPreviewCache = {};
      dynamicPageCache = {};
      detailPreviewProject = state.project;
      detailPreviewGeneration = state.dataGeneration;
      detailPreviewRuns = {};
    }
    if (detailPreviewRuns[runKind] !== run) {
      if (runKind === 'prescreen') { prescreenPreviewCache = {}; }
      else { dynamicPageCache = {}; }
      detailPreviewRuns[runKind] = run;
    }
    var cache = runKind === 'prescreen' ? prescreenPreviewCache : dynamicPageCache;
    var project = state.project;
    var generation = state.dataGeneration;
    function current() {
      var latest = Store.getState();
      return latest.project === project && latest.dataGeneration === generation && latest.lastRuns[runKind] === run;
    }
    var entry = cache[key];
    if (!entry) {
      entry = {};
      cache[key] = entry;
      // 先登記再發出，避免連續收合、展開建立重複請求。
      try { entry.promise = Promise.resolve(fetchPage()); }
      catch (error) { entry.promise = Promise.reject(error); }
      entry.promise = entry.promise.then(function (data) {
        if (current()) { entry.data = data; }
        return data;
      }).catch(function (error) {
        if (cache[key] === entry) { delete cache[key]; }
        throw error;
      });
    }
    function show(data) {
      if (body._jetDetailEntry === entry) { return; }
      body.setAttribute('aria-busy', 'false');
      display(data);
      body._jetDetailEntry = entry;
    }
    if (entry.data) { show(entry.data); return; }
    body.setAttribute('aria-busy', 'true');
    body.innerHTML = '<p role="status">正在讀取分錄…</p>';
    entry.promise.then(function (data) {
      if (current() && body.isConnected) { morphDetailBody(body, function () { show(data); }); }
    }).catch(function (error) {
      if (!current() || !body.isConnected) { return; }
      morphDetailBody(body, function () {
        body.setAttribute('aria-busy', 'false');
        var message = error && error.code === 'operation_cancelled'
          ? '讀取已取消。'
          : '讀取分錄失敗。' + (Ui.errorText(error, '') ? ' ' + Ui.errorText(error, '') : '');
        body.innerHTML = '<p role="status">' + Ui.esc(message) + '</p>' +
          '<button type="button" class="btn btn--ghost btn--tiny" data-detail-retry>重試</button>';
      });
      body.querySelector('[data-detail-retry]').addEventListener('click', function () {
        if (current() && body.isConnected) {
          morphDetailBody(body, function () { loadDetailPreview(body, key, runKind, run, fetchPage, display); });
        }
      });
    });
  }

  // 明細晚到的內容（讀到的分錄或失敗提示）走 JetMotion：明細從「正在讀取分錄…」的高度沿彈簧長到表格的高度，
  // 不在展開後才一下跳開（使用者 2026-10-13 要求展開與收合一致）。body 自己有 data-motion-key（detailBodyKey）。
  // 展開當下同步放進去的內容不走這裡：那時外層的展開區正在由 app.css 長出來，量一次會讓它停在舊的高度。
  function morphDetailBody(body, fill) {
    if (global.JetMotion && typeof global.JetMotion.morph === 'function' && body.hasAttribute &&
        body.hasAttribute('data-motion-key')) {
      global.JetMotion.morph(body, fill);
    } else {
      fill();
    }
  }

  function detailBodyKey(scrollKey, part) {
    return Ui.esc('detail-preview-' + (scrollKey || 'detail') + '-' + part);
  }

  // 科目分類編輯草稿（純畫面狀態）。null = 尚未編輯，直接顯示後端 snapshot；
  // 一旦使用者改動就在本地保留，直到保存成功、取消或後端 revision 換版。
  var taxonomyDraft = null;
  var taxonomyDraftRevision = null;
  var taxonomyOpen = false;
  var taxonomyAdvanced = false;
  var accountExcelOpen = false;
  // 新增分類的本地識別（尚未取得後端 stable ID 前只用於 DOM 對位；絕不送進 payload）。
  var taxonomyDraftSeq = 0;
  var taxonomyRemoval = null;
  // 預篩選「篩選結果」有沒有展開；重繪時照原樣畫出來，不每次都收起（使用者 2026-10-13 交接）。
  var prescreenSignalsOpen = false;

  Ui.registerWorkflowReset(function () {
    prescreenPreviewCache = {};
    dynamicPageCache = {};
    detailPreviewProject = null;
    detailPreviewGeneration = null;
    detailPreviewRuns = {};
    detailViews = { project: null, generation: null, runs: {} };
    taxonomyDraft = null;
    taxonomyDraftRevision = null;
    taxonomyOpen = false;
    taxonomyAdvanced = false;
    taxonomyRemoval = null;
    accountExcelOpen = false;
    prescreenSignalsOpen = false;
  });

  var PRESCREEN_PAGE_COLUMNS = [
    function (row) { return row.documentNumber || ''; },
    function (row) { return row.lineItem || '—'; },
    function (row) { return row.postDate || ''; },
    function (row) { return (row.accountCode || '') + ' ' + (row.accountName || ''); },
    function (row) { return row.documentDescription || ''; },
    { cell: function (row) { return Ui.money(row.amount); }, className: 'preview-table__amount' },
    function (row) { return row.drCr === 'DEBIT' ? '借' : '貸'; }
  ];

  // 預篩選命中表：每頁最多 200 筆，排序與搜尋由後端對完整查詢範圍處理；
  // 首擊或換排序時先清掉首屏列，再從資料庫端的第一頁接續。
  function bindPrescreenTable(block, key, data) {
    var tbody = block.querySelector('.preview-table tbody');
    if (!tbody) { return; }
    Ui.bindPagedTable(block, {
      initialPage: data,
      sourceKind: 'prescreen',
      viewState: detailPageState('prescreen', detailPageKey(block, 'prescreen-' + key)),
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryPrescreenPage({ ruleKey: key, cursor: cursor, pageSize: 200, sort: sort, search: search || null });
      },
      appendRows: function (rows) { Ui.appendRowsToTbody(tbody, rows, PRESCREEN_PAGE_COLUMNS); },
      clearRows: function () { tbody.innerHTML = ''; },
      table: block.querySelector('.preview-table'),
      search: block.querySelector('[data-page-search]')
    });
  }

  function prescreenPageHtml(data, scrollKey) {
    var rows = (data && data.rows) || [];
    return (
      Ui.pageSearchHtml('依傳票號碼查看') +
      '<div class="preview-table__wrap" data-preserve-scroll="' + Ui.esc(scrollKey || 'prescreen-detail') + '">' + Ui.previewTableHtml(rows, 'query.prescreenPage') + '</div>'
    );
  }

  // 詳情渲染輔助：把 detail 物件轉成 HTML 字串（放進 hidden 容器）。
  // prescreenPreview 類型回傳佔位 HTML；展開時觸發惰性抓取（見 bind()）。
  function detailHtml(detail, scrollKey) {
    if (!detail) { return ''; }
    if (detail.kind === 'sections') { return detail.sections.map(function (section, index) { return detailHtml(section, (scrollKey || 'detail') + '-' + index); }).join(''); }
    if (detail.kind === 'reason') {
      return '<p class="rule-detail__reason">' + Ui.esc(detail.text) + '</p>';
    }
    if (detail.kind === 'table') {
      // detail.pageQuery 是這張表分頁查詢的識別（DETAIL_PAGE_SPECS 的鍵）；有分頁查詢的表才提供排序與搜尋。
      var pageQuery = detail.pageQuery || null;
      var sortable = !!detail.sortAction && !!pageQuery;
      // 金額與筆數的資料格靠右（使用者 2026-10-13），表頭依 2026-10-16 要求統一靠左；detail.numeric 列出是數字的欄。
      var numeric = detail.numeric || [];
      var amountClass = function (index) { return numeric.indexOf(index) >= 0 ? 'preview-table__amount' : ''; };
      var thead = '<tr>' + (sortable
        ? Ui.sortableHeadCellsHtml(detail.sortAction, detail.columns.map(function (c, index) {
            return { label: c, key: (detail.sortKeys || [])[index] || null, className: amountClass(index) };
          }))
        : detail.columns.map(function (c, index) {
            return '<th' + (amountClass(index) ? ' class="' + amountClass(index) + '"' : '') + '>' + Ui.esc(c) + '</th>';
          }).join('')) + '</tr>';
      var searchHtml = sortable && detail.searchLabel ? Ui.pageSearchHtml(detail.searchLabel) : '';
      var tbody = detail.rows.map(function (row) {
        return '<tr>' + row.map(function (cell, index) {
          return '<td' + (amountClass(index) ? ' class="' + amountClass(index) + '"' : '') + '>' +
            Ui.esc(String(cell == null ? '' : cell)) + '</td>';
        }).join('') + '</tr>';
      }).join('');
      var prefix = detail.prefix ? '<p class="rule-detail__prefix">' + Ui.esc(detail.prefix) + '</p>' : '';
      // tbody 以 data-page-query 對應分頁查詢；展開後由共用分頁讀第一頁，取代摘要樣本。
      var targetAttr = pageQuery ? ' data-page-query="' + Ui.esc(pageQuery) + '"' : '';
      return prefix + searchHtml +
        '<div class="preview-table__wrap" data-preserve-scroll="detail-' + Ui.esc(scrollKey || pageQuery || 'table') + '">' +
          '<table class="preview-table"><thead>' + thead + '</thead>' +
          '<tbody' + targetAttr + '>' + tbody + '</tbody></table>' +
        '</div>';
    }
    if (detail.kind === 'dynamicPage') {
      var spec = DYNAMIC_PAGE_SPECS[detail.pageKey];
      return '<div class="rule-detail__preview-block" data-dynamic-page="' + Ui.esc(detail.pageKey) + '">' +
        '<span class="rule-detail__preview-label">' + Ui.esc(spec ? spec.label : '') + '</span>' +
        '<div class="rule-detail__preview-body" data-motion-key="' + detailBodyKey(scrollKey, detail.pageKey) + '">載入中…</div>' +
      '</div>';
    }
    if (detail.kind === 'prescreenPreview') {
      // 每個 preview 項目產生一個帶佔位符的子區塊；data-prescreen-key 供 bind() 識別。
      return detail.previews.map(function (pv) {
        return '<div class="rule-detail__preview-block" data-prescreen-key="' + Ui.esc(pv.key) + '">' +
          '<span class="rule-detail__preview-label">' + Ui.esc(pv.label) + '</span>' +
          '<div class="rule-detail__preview-body" data-motion-key="' + detailBodyKey(scrollKey, pv.key) + '">載入中…</div>' +
        '</div>';
      }).join('');
    }
    return '';
  }

  // 分頁金額一律走 Ui.money，和摘要樣本保持相同格式。
  // 目前 num() 的呼叫點全為金額（tbAmount/glAmount/diff/debit/credit）；若日後拿它顯示計數，請改回 toLocaleString。
  function num(v) { return Ui.money(v); }

  // 明細查詢規格：id 對應 fetchPage 與 columns，供共用分頁取得每一頁。
  // fetchPage 只發對應 query.*Page action、回 { rows, nextCursor };columns 為每欄的 row→cell 取值函式。
  // 純呼叫膠水:不算差異、不判命中、不組 SQL;顯示形狀(金額換算、借貸文字)由後端 row 決定,前端僅格式化呈現。
  var DETAIL_PAGE_SPECS = {
    completeness: {
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryCompletenessDiffPage({ cursor: cursor, pageSize: 200, sort: sort || null, search: search || null });
      },
      columns: [
        function (r) { return r.accountCode || ''; },
        function (r) { return r.accountName || ''; },
        amountColumn(function (r) { return num(r.tbAmount); }),
        amountColumn(function (r) { return num(r.glAmount); }),
        amountColumn(function (r) { return Ui.moneyDifference(r.diff); })
      ]
    },
    docBalance: {
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryDocBalancePage({ cursor: cursor, pageSize: 200, sort: sort || null, search: search || null });
      },
      columns: [
        function (r) { return r.documentNumber || ''; },
        amountColumn(function (r) { return num(r.debit); }),
        amountColumn(function (r) { return num(r.credit); }),
        amountColumn(function (r) { return Ui.moneyDifference(r.diff); })
      ]
    },
  };

  // 分頁數字欄靠右，和 detailHtml 的 numeric 對齊。
  function amountColumn(cell) { return { cell: cell, className: 'preview-table__amount' }; }

  // 空值子項對應後端 category 與固定的「異常類別」顯示字；id 形如 null-<category>，動態註冊進 DETAIL_PAGE_SPECS
  // （columns 與既有空值 table 欄序對齊）。空白摘要只在預篩選訊號那一處呈現，這裡不再註冊它的明細頁。
  function nullPageSpec(category, categoryLabel) {
    return {
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryNullRecordsPage({ category: category, cursor: cursor, pageSize: 200, sort: sort || null, search: search || null });
      },
      columns: [
        function (r) { return r.documentNumber || ''; },
        function (r) { return r.accountCode || ''; },
        function (r) { return r.postDate || ''; },
        function (r) { return r.description || ''; },
        function () { return categoryLabel; }
      ]
    };
  }

  // 後端 nullRecordsPage 白名單四 category → 「異常類別」顯示字(對齊 issuesCn map)。
  // 空白總帳日期已改由來源品質承載，不再屬於空值紀錄測試。
  DETAIL_PAGE_SPECS['null-outOfRangeDate'] = nullPageSpec('outOfRangeDate', '核准日不在期間');
  DETAIL_PAGE_SPECS['null-nullDocument'] = nullPageSpec('nullDocument', '空白傳票號碼');
  DETAIL_PAGE_SPECS['null-nullAccount'] = nullPageSpec('nullAccount', '空白科目編號');

  // 來源品質全量明細（query.sourceQualityPage）：不套查核期間述詞，因此仍看得到
  // 被期間界定排除的空白總帳日期；欄序與下方 detail 的固定欄一致。
  DETAIL_PAGE_SPECS.sourceQuality = {
    fetchPage: function (cursor, sort, search) {
      return global.JetApi.querySourceQualityPage({ cursor: cursor, pageSize: 200, sort: sort || null, search: search || null });
    },
    columns: [
      function (r) { return sourceQualityCategoryLabel(r.category); },
      function (r) { return r.sourceLabel || ''; },
      function (r) { return r.sourceRowNumber == null ? '' : String(r.sourceRowNumber); },
      function (r) { return r.documentNumber || ''; },
      function (r) { return r.accountCode || ''; },
      function (r) { return r.description || ''; }
    ]
  };

  // 來源品質 finding 類別 → 顯示字。目前後端只回一種類別，未登錄值原樣顯示。
  function sourceQualityCategoryLabel(category) {
    return category === 'nullPostDate' ? '總帳日期空白' : (category || '');
  }

  // INF 抽樣明細改由後端欄位 metadata 驅動（固定欄 + 已提交的攸關資料元素欄位）。
  var DYNAMIC_PAGE_SPECS = {
    inf: {
      label: '抽樣樣本明細',
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryInfSamplePage({ cursor: cursor, pageSize: 200, sort: sort || null, search: search || null });
      }
    }
  };

  // 動態欄結果表的首頁快取（以 runId + pageKey 為鍵；新 run 自然不命中）。
  var dynamicPageCache = {};

  // issues 陣列（英文鍵）→ 中文顯示，以「、」接合。
  function issuesCn(issues) {
    // 空白總帳日期已移出空值紀錄測試（改由來源品質承載），故不在這份封閉類別內。
    var map = {
      account: '空白科目編號', document: '空白傳票號碼', description: '空白摘要',
      date: '核准日不在期間'
    };
    return (issues || []).map(function (k) { return map[k] || k; }).join('、');
  }

  // 空值紀錄測試在後端是單一規則（null_records_test）；UI 依異常類別拆成四個子項呈現。
  // 每個子項讀對應子計數作徽章，詳情用 nullRows 的 issues 標籤過濾出該類列
  // 摘要樣本只供尚未展開的初始結構；展開後直接讀取該類別第一頁，不以樣本判斷是否有明細。
  // categories 只放一個 nullRecordsPage 的 category 白名單值，對應 DETAIL_PAGE_SPECS 的 null-<category>。
  // naReasonOf：後端判定這一類無法檢查時回傳它寫的原因（例如傳票核准日不可用），畫面只顯示這一句，不寫「通過」。
  function nullSubItem(title, desc, issueKeys, countOf, categories, naReasonOf) {
    function naReason(v) { return naReasonOf ? naReasonOf(v.nullRecordsTest) : null; }
    return {
      title: title,
      desc: desc,
      status: function (v) {
        if (!v) { return null; }
        if (naReason(v)) { return naStatus({ naReason: naReason(v) }); }
        var n = countOf(v.nullRecordsTest);
        return n > 0
          ? { tone: 'alert', text: n.toLocaleString() + ' 筆異常' }
          : { tone: 'ok', text: '通過' };
      },
      detail: function (v) {
        if (v && naReason(v)) { return { kind: 'reason', text: naReason(v) }; }
        if (!v || countOf(v.nullRecordsTest) <= 0) { return null; }
        var rows = (v.nullRecordsTest.nullRows || []).filter(function (r) {
          return (r.issues || []).some(function (k) { return issueKeys.indexOf(k) >= 0; });
        }).map(function (r) {
          return [r.documentNumber || '', r.accountCode || '', r.postDate || '', r.description || '', issuesCn(r.issues)];
        });
        return {
          kind: 'table',
          columns: ['傳票號碼', '科目編號', '總帳日期', '摘要', '異常類別'],
          sortAction: 'query.nullRecordsPage', sortKeys: ['documentNumber', 'accountCode', 'postDate', 'description', null],
          searchLabel: '依傳票號碼、科目編號或摘要搜尋',
          rows: rows,
          pageQuery: 'null-' + categories[0]
        };
      }
    };
  }

  var VALIDATION_ITEMS = [
    {
      title: '完整性測試',
      // 2026-10-05 V2：照實說明第一項比對的對象；最後一句是使用者原句，逐字保留。
      desc: '先確認 JET 存下的分錄筆數與借貸合計，和確認欄位配對時算出的一樣。這一步不和來源檔自己的合計比對。再比對各科目的總帳本期借貸淨額與試算表本期變動金額。金額不符代表總帳母體可能缺漏。',
      status: function (v) {
        if (!v) { return null; }
        var eligibility = Ui.completenessEligibility(v);
        var n = Number(v.completenessTest.diffAccountCount);
        if (!eligibility.isEligible || eligibility.warning) {
          if (v.completenessTest.naReason) {
            return { tone: 'na', text: '無法執行', tip: eligibility.reason || eligibility.warning };
          }
          var text = '未通過';
          if (n > 0) { text = n.toLocaleString() + ' 個科目不符'; }
          else if (controlTotalsMismatch(v)) { text = '存下的分錄數字不一致'; }
          else if (v.completenessTest.partA && v.completenessTest.partA.rowCountMatch == null) {
            text = '存下的分錄無法核對';
          }
          return { tone: 'alert', text: text, tip: eligibility.reason || eligibility.warning };
        }
        return { tone: 'ok', text: '通過' };
      },
      detail: function (v) {
        if (!v) { return null; }
        var eligibility = Ui.completenessEligibility(v);
        var diffAccountCount = Number(v.completenessTest.diffAccountCount);
        if (diffAccountCount > 0) {
          var rows = (v.completenessTest.diffAccounts || []).map(function (d) {
            return [
              d.accountCode || '',
              d.accountName || '',
              Ui.money(d.tbAmount),
              Ui.money(d.glAmount),
              Ui.moneyDifference(d.diff)
            ];
          });
          return withControlTotals(v, {
            kind: 'table',
            columns: ['科目編號', '科目名稱', 'TB 金額', 'GL 金額', '差額'],
            numeric: [2, 3, 4],
            sortAction: 'query.completenessDiffPage', sortKeys: ['accountCode', 'accountName', 'tbAmount', 'glAmount', 'diff'],
            searchLabel: '依科目編號查看',
            rows: rows,
            prefix: [eligibility.reason || eligibility.warning, differencePrecisionNote].filter(Boolean).join(' '),
            pageQuery: 'completeness'
          });
        }
        if (!eligibility.isEligible || eligibility.warning) {
          return withControlTotals(v, { kind: 'reason', text: eligibility.reason || eligibility.warning });
        }
        return withControlTotals(v, null);
      }
    },
    {
      title: '借貸不平測試',
      // 2026-10-05 W1：判讀句照使用者原件用「、」。
      desc: '依傳票號碼彙總借方與貸方金額，列出不相等的傳票。借貸不平代表傳票編號、金額欄位可能有誤。',
      status: function (v) {
        if (!v) { return null; }
        var n = Number(v.docBalanceTest.unbalancedDocumentCount);
        return n > 0
          ? { tone: 'alert', text: n.toLocaleString() + ' 張傳票不平' }
          : { tone: 'ok', text: '通過' };
      },
      detail: function (v) {
        if (!v) { return null; }
        if (Number(v.docBalanceTest.unbalancedDocumentCount) > 0) {
          var rows = (v.docBalanceTest.unbalancedDocuments || []).map(function (d) {
            return [
              d.documentNumber || '',
              Ui.money(d.debit),
              Ui.money(d.credit),
              Ui.moneyDifference(d.diff)
            ];
          });
          return {
            kind: 'table',
            columns: ['傳票號碼', '借方', '貸方', '差額'],
            numeric: [1, 2, 3],
            sortAction: 'query.docBalancePage', sortKeys: ['documentNumber', 'debit', 'credit', 'diff'],
            searchLabel: '依傳票號碼查看',
            rows: rows,
            prefix: differencePrecisionNote,
            pageQuery: 'docBalance'
          };
        }
        return null;
      }
    },
    {
      title: '資料可靠性測試',
      desc: '抽出分錄樣本，供核對傳票附件，確認摘要、日期等篩選欄位是否正確。需先確認攸關資料元素（RDE）的可靠性後，再執行高風險篩選條件。',
      status: function (v) {
        if (!v) { return null; }
        if (v.infSamplingTest.naReason) { return naStatus(v.infSamplingTest); }
        return { tone: 'info', text: '已抽出 ' + Number(v.infSamplingTest.sampleSize).toLocaleString() + ' 筆' };
      },
      detail: function (v) {
        // INF 抽樣明細的欄位由系統端決定（固定欄 + 已提交的攸關資料元素欄位），
        // 因此展開時才連同欄位定義一起取回，畫面不預先假設欄序或欄名。
        if (!v || v.infSamplingTest.naReason) { return null; }
        if (Number(v.infSamplingTest.sampleSize) <= 0) { return null; }
        return { kind: 'dynamicPage', pageKey: 'inf' };
      }
    },
    // 傳票核准日不可用時後端不檢查，筆數是 null 並寫原因（使用者 2026-10-23 裁定）。
    nullSubItem('核准日不在期間', '傳票核准日落在查核期間之外的分錄。', ['date'],
      function (nrt) { return Number(nrt.outOfRangeDateCount); }, ['outOfRangeDate'],
      function (nrt) { return nrt.outOfRangeDateNaReason || null; }),
    {
      // 來源品質是 validate.run 的獨立區塊，不是第五條資料驗證規則：它看的是 PBC-GL，
      // 不套查核期間，因此筆數不與空值紀錄的四類相加。
      title: '總帳日期空白',
      desc: '總帳日期空白的分錄，無法判斷是否在查核期間內，因此未納入本次測試。',
      status: function (v) {
        if (!v || !v.sourceQuality) { return null; }
        var n = Number(v.sourceQuality.findingCount);
        return n > 0
          ? { tone: 'alert', text: n.toLocaleString() + ' 筆待確認' }
          : { tone: 'ok', text: '未發現' };
      },
      detail: function (v) {
        if (!v || !v.sourceQuality || Number(v.sourceQuality.findingCount) <= 0) { return null; }
        var rows = (v.sourceQuality.sampleRows || []).map(function (r) {
          return [
            sourceQualityCategoryLabel(r.category),
            r.sourceLabel || '',
            r.sourceRowNumber == null ? '' : String(r.sourceRowNumber),
            r.documentNumber || '',
            r.accountCode || '',
            r.description || ''
          ];
        });
        return {
          kind: 'table',
          columns: ['類別', '來源檔', '來源列號', '傳票號碼', '科目編號', '摘要'],
          sortAction: 'query.sourceQualityPage', sortKeys: [null, null, 'sourceRowNumber', 'documentNumber', 'accountCode', 'description'],
          searchLabel: '依傳票號碼查看',
          rows: rows,
          prefix: '這些分錄保留在 PBC-GL 中，並列入 GL(未納入測試)。',
          pageQuery: 'sourceQuality'
        };
      }
    }
  ];

  // 空值紀錄測試的「空白傳票號碼」「空白科目編號」兩個子項列在「風險預篩選」卡的資料缺漏輔助檢視
  // （呈現位置移動，資料來源不變）。兩者仍是後端單一 null_records_test 規則的子計數，
  // 由 validate.run 產生，因此它們的 runData 一律吃 state.lastRuns.validate（不是 prescreen）。
  // 空白摘要只留預篩選訊號那一處（預篩選收斂計畫階段九），驗證報告照舊由後端寫出；
  // 「核准日不在期間」留在資料驗證卡，因為它是資料品質面（期間界定）而非風險候選面。
  var NULL_PRESCREEN_ITEMS = [
    nullSubItem('空白傳票號碼', '傳票號碼空白的分錄。與空白科目編號分開列示；傳票號碼與科目編號都空白的分錄，兩份明細都會列出。', ['document'],
      function (nrt) { return Number(nrt.nullDocumentCount); }, ['nullDocument']),
    nullSubItem('空白科目編號', '科目編號空白的分錄。各類筆數分別計算，不將兩類相加當作分錄總數。', ['account'],
      function (nrt) { return Number(nrt.nullAccountCount); }, ['nullAccount'])
  ];

  var PRESCREEN_ITEMS = [
    {
      title: '期末財報準備日起核准之分錄',
      desc: Ui.prescreenConditionDescription('postPeriodApproval'),
      status: function (p) { return p ? hitCountStatus(p.postPeriodApproval) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.postPeriodApproval.naReason) { return { kind: 'reason', text: p.postPeriodApproval.naReason }; }
        if (Number(p.postPeriodApproval.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '符合條件的分錄', key: 'postPeriodApproval' }] };
        }
        return null;
      }
    },
    {
      title: '分錄摘要出現特定描述',
      desc: Ui.prescreenConditionDescription('suspiciousKeywords'),
      status: function (p) { return p ? hitCountStatus(p.suspiciousKeywords) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (Number(p.suspiciousKeywords.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '符合條件的分錄', key: 'suspiciousKeywords' }] };
        }
        return null;
      }
    },
    {
      title: '未預期出現之特定借貸組合',
      desc: Ui.prescreenConditionDescription('unexpectedAccountPair'),
      status: function (p) { return p ? hitCountStatus(p.unexpectedAccountPair) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.unexpectedAccountPair.naReason) { return { kind: 'reason', text: p.unexpectedAccountPair.naReason }; }
        if (Number(p.unexpectedAccountPair.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '符合條件的分錄', key: 'unexpectedAccountPair' }] };
        }
        return null;
      }
    },
    {
      title: Ui.prescreenConditionLabel('trailingZeros') + ' 的分錄',
      desc: Ui.prescreenConditionDescription('trailingZeros'),
      status: function (p) {
        return p ? hitCountStatus(p.trailingZeros,
          '本次門檻：尾數連續 ' + p.trailingZeros.zerosThreshold + ' 個 0。') : null;
      },
      detail: function (p) {
        if (!p) { return null; }
        if (Number(p.trailingZeros.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '符合條件的分錄', key: 'trailingZeros' }] };
        }
        return null;
      }
    },
    {
      title: '依分錄編製者彙總',
      desc: '依傳票建立人員統計筆數、金額與人工分錄筆數，呈現查核期間的編製分布。',
      status: function (p) {
        if (!p) { return null; }
        if (p.creatorSummary.naReason) { return naStatus(p.creatorSummary); }
        // V9：creators 最多 50 列，人數改用後端的完整人數（空白人員算一組，與流程總覽同一個數字）。
        // 舊的預篩選結果沒有這個數字，照「較少使用之科目」的前例請使用者重新執行，不拿清單長度冒充。
        if (p.creatorSummary.totalPreparerCount == null) {
          return { tone: 'info', text: '預篩選結果未包含人員數，請重新執行預篩選' };
        }
        return { tone: 'info', text: countText(p.creatorSummary.totalPreparerCount) + ' 位人員' };
      },
      detail: function (p) {
        if (!p) { return null; }
        if (p.creatorSummary.naReason) { return { kind: 'reason', text: p.creatorSummary.naReason }; }
        if (p.creatorSummary.creators && p.creatorSummary.creators.length > 0) {
          var rows = p.creatorSummary.creators.map(function (c) {
            return [
              c.createdBy == null || !String(c.createdBy).trim() ? '（空白）' : c.createdBy,
              Number(c.entryCount).toLocaleString(),
              Ui.money(c.debitTotal),
              Ui.money(c.creditTotal),
              Number(c.manualCount).toLocaleString()
            ];
          });
          var total = p.creatorSummary.totalPreparerCount;
          var prefix = total != null && Number(total) > rows.length
            ? '共 ' + countText(total) + ' 位人員，這裡列出分錄筆數最多的 ' + countText(rows.length) + ' 位。'
            : null;
          return { kind: 'table', prefix: prefix, columns: ['編製人員', '分錄筆數', '借方', '貸方', '人工分錄筆數'], numeric: [1, 2, 3, 4], rows: rows };
        }
        return null;
      }
    },
    {
      title: '較少使用之科目',
      desc: '按科目編號統計分錄筆數並由低到高排列，作為了解查核期間分錄分布與少用科目的彙總參考。',
      status: function (p) {
        if (!p) { return null; }
        if (!p.rareAccounts || p.rareAccounts.lowFrequencyAccountCount == null) {
          return { tone: 'info', text: '預篩選結果未包含較少使用的科目數，請重新執行預篩選' };
        }
        return { tone: 'info', text: countText(p.rareAccounts.lowFrequencyAccountCount) + ' 個較少使用的科目（' + Ui.PRESCREEN_DEFAULTS.accountMaxEntries + ' 筆以下）' };
      },
      detail: function (p) {
        if (!p) { return null; }
        if (Number(p.rareAccounts.distinctAccountCount) > 0) {
          var rows = (p.rareAccounts.accounts || []).map(function (a) {
            return [
              a.accountCode || '',
              a.accountName || '',
              Number(a.entryCount).toLocaleString(),
              Ui.money(a.debitTotal),
              Ui.money(a.creditTotal)
            ];
          });
          return { kind: 'table', columns: ['科目編號', '科目名稱', '分錄筆數', '借方', '貸方'], numeric: [2, 3, 4], rows: rows };
        }
        return null;
      }
    },
    {
      title: '週末過帳／核准之分錄',
      desc: '總帳日期或傳票核准日落在設定週末日的分錄（補班日仍納入）。',
      status: function (p) { return p ? pairCountStatus(p.weekendActivity) : null; },
      detail: function (p) { return p ? activityDetail(p.weekendActivity, 'weekendPosting', 'weekendApproval') : null; }
    },
    {
      title: '假日過帳／核准之分錄',
      desc: '總帳日期或傳票核准日落在國定假日的分錄（需先匯入假日清單）。',
      status: function (p) { return p ? pairCountStatus(p.holidayActivity) : null; },
      detail: function (p) { return p ? activityDetail(p.holidayActivity, 'holidayPosting', 'holidayApproval') : null; }
    },
    {
      title: '空白摘要',
      desc: Ui.prescreenConditionDescription('blankDescription'),
      status: function (p) { return p ? hitCountStatus(p.blankDescription) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (Number(p.blankDescription.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '符合條件的分錄', key: 'blankDescription' }] };
        }
        return null;
      }
    },
    {
      title: '非授權編製人員之分錄',
      desc: Ui.prescreenConditionDescription('nonAuthorizedPreparer'),
      status: function (p) { return p ? hitCountStatus(p.nonAuthorizedPreparer) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.nonAuthorizedPreparer.naReason) { return { kind: 'reason', text: p.nonAuthorizedPreparer.naReason }; }
        if (Number(p.nonAuthorizedPreparer.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '符合條件的分錄', key: 'nonAuthorizedPreparer' }] };
        }
        return null;
      }
    },
    {
      title: Ui.prescreenConditionLabel('lowFrequencyPreparer'),
      desc: Ui.prescreenConditionDescription('lowFrequencyPreparer'),
      status: function (p) { return p ? hitCountStatus(p.lowFrequencyPreparer) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.lowFrequencyPreparer.naReason) { return { kind: 'reason', text: p.lowFrequencyPreparer.naReason }; }
        if (Number(p.lowFrequencyPreparer.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '符合條件的分錄', key: 'lowFrequencyPreparer' }] };
        }
        return null;
      }
    },
    {
      title: Ui.prescreenConditionLabel('lowFrequencyAccount'),
      desc: Ui.prescreenConditionDescription('lowFrequencyAccount'),
      status: function (p) { return p ? hitCountStatus(p.lowFrequencyAccount) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (Number(p.lowFrequencyAccount.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '符合條件的分錄', key: 'lowFrequencyAccount' }] };
        }
        return null;
      }
    }
  ];

  // RuleCatalog 的兩條 Aggregate 規則在步驟四是主要呈現；其餘 RowTag 規則降為可展開的
  // 逐筆輔助訊號。只重排既有 response，沒有新增計算或改變命中集合。
  var PRESCREEN_AGGREGATE_ITEMS = PRESCREEN_ITEMS.slice(4, 6);
  var PRESCREEN_SIGNAL_ITEMS = PRESCREEN_ITEMS.slice(0, 4).concat(PRESCREEN_ITEMS.slice(6));

  function render(container, state) {
    if (!state.project) {
      container.innerHTML = Ui.noProjectPanel('資料驗證與測試');
      Ui.bindNoProjectPanel(container);
      return;
    }

    var ready = !!state.mapping.gl.committed;
    var v = state.lastRuns.validate;
    var p = state.lastRuns.prescreen;
    var eligibility = Ui.completenessEligibility(v);

    container.innerHTML =
      '<div class="panel panel--wide" data-motion-key="validate-panel">' +
        '<h2 class="panel__title">資料驗證與測試</h2>' +
        Ui.downstreamResetNoticeHtml(state) +
        (ready ? '' :
          '<p class="panel__warn">GL 尚未確認配對，無法執行；請先回「欄位配對」按「確認配對」。</p>') +
        statsBarHtml(v) +
        validationCardHtml(v, ready, state) +
        '<section class="rule-card account-preparation" aria-labelledby="account-preparation-title">' +
          '<div class="rule-card__head"><h3 class="rule-card__title" id="account-preparation-title">科目配對</h3>' +
            '<button type="button" class="btn btn--ghost" data-action="toggle-taxonomy" data-focus-key="account-settings" aria-expanded="' + taxonomyOpen + '">' +
              (taxonomyOpen ? '返回科目配對' : '分類設定') + '</button></div>' +
          // 配對方式的頁籤與內容包成一塊：和分類設定互換時整塊原地交叉淡化；換頁籤時只有內容交叉淡化，
          // 高度平順改變，下面的預篩選跟著移動（app.js 的 JetMotion，使用者 2026-10-12）。
          (taxonomyOpen ? taxonomyCardHtml(state) :
            '<div class="account-methods-block" data-motion-key="account-methods">' +
            '<div class="account-methods" role="tablist" aria-label="科目配對方式">' +
              '<button type="button" data-action="select-account-jet" data-focus-key="account-method-jet"' +
                Ui.tabAttributes(!accountExcelOpen, 'account-method-panel', 'account-method-jet') + '>在 JET 配對</button>' +
              '<button type="button" data-action="toggle-account-excel" data-focus-key="account-method-excel"' +
                Ui.tabAttributes(accountExcelOpen, 'account-method-panel', 'account-method-excel') + '>用 Excel 配對</button>' +
            '</div><div role="tabpanel" id="account-method-panel" data-motion-key="account-method-' + (accountExcelOpen ? 'excel' : 'jet') +
              '" aria-labelledby="account-method-' + (accountExcelOpen ? 'excel' : 'jet') + '">' +
              (accountExcelOpen ? accountMappingCardHtml(state.importState.accountMapping, ready)
                : accountTemplateJetStatusHtml() + Ui.accountMappingEditor.html(state, ready)) + '</div></div>') +
        '</section>' +
        prescreenCardHtml(p, v, ready, state, eligibility) +
        Ui.stepFooterHtml(state) +
      '</div>';

    bind(container, ready);
  }

  function statsBarHtml(v) {
    var stats = v && v.stats ? v.stats : null;
    function card(label, value, modifier) {
      return (
        '<div class="stat-card' + (modifier ? ' stat-card--' + modifier : '') + '">' +
          '<span class="stat-card__value">' + value + '</span>' +
          '<span class="stat-card__label">' + label + '</span>' +
        '</div>'
      );
    }

    return (
      '<div class="stats-bar stats-bar--validation">' +
        card('GL 分錄數', stats ? countText(stats.glRowCount) : '—') +
        card('傳票數', stats ? countText(stats.voucherCount) : '—') +
        card('借貸淨額（應為 0）', stats ? Ui.moneyDifference(stats.net) : '—') +
      '</div>'
    );
  }

  // 規則狀態徽章：未執行 → 灰；通過/未發現 → 綠；風險候選 → 黃；
  // 需要處理 → 紅；彙總參考 → 藍；無法執行 → 虛線（tooltip 給原因）。
  function ruleBadge(status) {
    if (!status) {
      return '<span class="rule-status rule-status--idle">未執行</span>';
    }
    return '<span class="rule-status rule-status--' + status.tone + '"' +
      (status.tip ? ' title="' + Ui.esc(status.tip) + '"' : '') + '>' + Ui.esc(status.text) + '</span>';
  }

  function ruleListHtml(items, runData, scope) {
    var view = detailView(detailRunKind(scope));
    return '<ul class="rule-list">' + items.map(function (item, idx) {
      // status 為 null = 未執行（無 runData 或 resume 舊結果缺此規則鍵）→ 不算 detail,
      // 避免 detail 去取缺漏子物件而丟例外拖垮整步;詳情只在規則確有結果時才存在。
      var status = item.status(runData);
      var detail = (status && item.detail) ? item.detail(runData) : null;
      var expanded = !!view.expanded[scope + '-' + idx];
      var toggleBtn = detail
        ? '<button type="button" class="btn btn--ghost btn--tiny rule-item__toggle jet-disclosure"' +
            ' data-action="toggle-detail" data-scope="' + Ui.esc(scope) + '" data-idx="' + idx + '"' +
            Ui.disclosureAttributes(expanded, 'rule-detail-' + scope + '-' + idx) + '>檢視</button>'
        : '';
      var detailContainer = detail
        ? '<div class="rule-detail" id="' + Ui.esc('rule-detail-' + scope + '-' + idx) + '" data-bind="rule-detail-' + Ui.esc(scope) + '-' + idx + '" data-collapsible' + (expanded ? '' : ' hidden') + '>' +
            detailHtml(detail, scope + '-' + idx) +
          '</div>'
        : '';
      return (
        '<li class="rule-item">' +
          '<div class="rule-item__main">' +
            '<span class="rule-item__title">' + Ui.esc(item.title) + '</span>' +
            '<span class="rule-item__desc">' + Ui.esc(item.desc) + '</span>' +
          '</div>' +
          '<div class="rule-item__aside">' + ruleBadge(status) + toggleBtn + '</div>' +
          detailContainer +
        '</li>'
      );
    }).join('') + '</ul>';
  }

  function lastRunLineHtml(runData) {
    if (!runData || !runData.resultRef) { return ''; }
    var time = new Date(runData.resultRef.generatedUtc);
    return '<p class="rule-card__meta">上次執行：' +
      time.toLocaleString('zh-Hant', { hour12: false }) + '</p>';
  }

  function validationArtifactsHtml(state, v) {
    if (!v || !v.resultRef || !v.resultRef.runId) { return ''; }
    var artifacts = Ui.currentReportArtifacts(
      state,
      ['validationReport', 'infReport'],
      { validationRunId: v.resultRef.runId });
    var complete = artifacts.length === 2;
    return (
      '<div class="report-output" data-motion-key="validation-output">' +
        '<div class="report-output__head">' +
          '<div>' +
            '<h4 class="report-output__title">驗證報告與抽樣清單</h4>' +
          '</div>' +
          '<div class="report-output__actions">' + Ui.reportFolderButtonHtml(artifacts) +
            '<button type="button" class="btn btn--ghost" data-action="export-validation-artifacts">' +
            (complete ? '重新產生兩份報告' : '產生報告與抽樣清單') + '</button></div>' +
        '</div>' +
        validationOutputStatusHtml('reports') +
        Ui.reportArtifactListHtml(artifacts, '驗證已完成，報告尚未產生。', { motion: 'validation' }) +
      '</div>'
    );
  }

  function validationOutputStatusHtml(kind) {
    var state = Store.getState();
    var output = state.validationOutput[kind];
    var validation = state.lastRuns.validate;
    if (!output || (kind !== 'template' && (!validation || output.runId !== validation.resultRef.runId))) { return ''; }
    var statusClass = output.status === 'failed' ? 'form-notice' : 'validation-output-status';
    return '<p class="' + statusClass + '" data-validation-output="' + kind + '" role="status">' +
      Ui.esc(output.message) + '</p>';
  }

  // 「在 JET 配對」分頁只在科目配對檔正在準備或準備失敗時顯示一行狀態；已建立或已保留時不顯示，
  // 不打擾直接在 JET 配對的審計員。完整狀態與「準備科目配對檔」仍只在「用 Excel 配對」分頁。
  // data-validation-output="template" 保留給 Excel 分頁，這裡另用 data-account-template-status。
  function accountTemplateJetStatusHtml() {
    var output = Store.getState().validationOutput.template;
    if (!output) { return ''; }
    if (output.status === 'pending') {
      return '<p class="validation-output-status" data-account-template-status="pending" role="status">' +
        Ui.esc(output.message) + '</p>';
    }
    if (output.status !== 'failed') { return ''; }
    // 沒有原因時請審計員再試一次，不再說明後端沒有給原因（使用者 2026-10-14 第三輪勾選頁確認 R3-32）。
    return '<p class="form-notice" data-account-template-status="failed" role="status">科目配對檔尚未準備好' +
      (output.reason ? '：' + Ui.esc(output.reason) : '，請再試一次。') + ' ' +
      '<button type="button" class="btn btn--ghost btn--tiny" data-action="retry-account-template-in-excel">' +
        '到「用 Excel 配對」重試</button></p>';
  }

  function validationCardHtml(v, ready, state) {
    return (
      '<section class="rule-card">' +
        '<div class="rule-card__head">' +
          '<h3 class="rule-card__title">資料驗證</h3>' +
          '<button type="button" class="btn" data-action="run-validate"' + (ready ? '' : ' disabled') +
            '>' + (v ? '重新執行驗證' : '執行驗證') + '</button>' +
        '</div>' +
        lastRunLineHtml(v) +
        ruleListHtml(VALIDATION_ITEMS, v, 'v') +
        validationArtifactsHtml(state, v) +
      '</section>'
    );
  }

  // 科目配對匯入區塊：方法論上排在「資料驗證」（含完整性測試）之後、「風險預篩選」之前。
  // 預篩選的「未預期出現之特定借貸組合」依賴科目配對，因此先提供匯入入口。
  //
  // 匯入和重新匯入鈕只看 GL 欄位配對是否已確認(mappingReady)，不需要先執行資料驗證。
  // 完整性有差異仍可篩選；只有缺少、過期或損壞的驗證結果才要求重新驗證，不要求差異歸零。
  // 只要已匯入(含 resume)，就一律顯示已保存的科目數與「預覽」鈕。
  // 前端只做操作引導，未預期借貸組合是否能執行仍由後端預篩選判定。
  // 分類留白的科目在投影時已視為 Others（同 legacy），篩選不受影響；這裡只讓審計員看到哪些科目沒填，
  // 想改分類就到範本 C 欄填好再重新匯入。清單走 query.accountMappingBlankPage 的有界分頁。
  // 「列出科目」打開的清單和下面兩份差異清單一樣記在 mappingDifferenceView：整段重繪後照原樣打開，
  // 已讀的列從同一份分頁狀態接回，不會一重繪就收起來（使用者 2026-10-13 要求展開與收合一致）。
  function blankCategoryNoticeHtml(info) {
    if (!info || !(info.blankCategoryCount > 0)) { return ''; }
    var open = !!mappingDifferenceView(info).expanded.blank;
    return '<p class="import-card__status" data-bind="blank-category-notice">分類留白 ' + Number(info.blankCategoryCount).toLocaleString() +
        ' 筆，視為 Others。可在下方整批調整分類，或修改科目配對檔後重新匯入。 ' +
        '<button type="button" class="btn btn--ghost btn--tiny jet-disclosure" data-action="list-blank-categories"' +
          Ui.disclosureAttributes(open, 'blank-category-list') + '>列出科目</button></p>' +
      '<div class="account-mapping-checks__list" id="blank-category-list" data-bind="blank-category-list" data-motion-key="blank-category-list" data-collapsible' +
        (open ? '' : ' hidden') + '></div>';
  }

  // 清單晚到的第一頁走 JetMotion：清單從「正在讀取資料…」的高度沿彈簧長到列出來的高度，不在展開後才一下跳開
  // （使用者 2026-10-13 要求展開與收合一致）。清單本身有 data-motion-key。整段重繪時接回已讀的列不走這裡，
  // 那時重繪本身就在比對（容器帶 data-motion-rendering）。
  // 清單還在展開的高度轉場中（剛按下「列出科目」，本機第一頁常在轉場結束前就回來，或重新打開時同步接回已讀的列）
  // 直接放進去：[data-collapsible] 的轉場會自己長到新的高度。這時再走 JetMotion，量到的是轉場到一半的高度，
  // 轉場一結束清單會先縮回那個高度再跳開。
  function collapsibleHeightTransitionRunning(list) {
    if (typeof list.getAnimations !== 'function') { return false; }
    return list.getAnimations().some(function (animation) {
      return animation.transitionProperty === 'height' && animation.playState === 'running';
    });
  }

  function morphCheckList(list, fill) {
    if (global.JetMotion && typeof global.JetMotion.morph === 'function' && list.isConnected && !list.hidden &&
        !list.closest('[data-motion-rendering]') && !collapsibleHeightTransitionRunning(list)) {
      global.JetMotion.morph(list, fill);
    } else {
      fill();
    }
  }

  var accountDifferenceView = null;
  function mappingDifferenceView(info) {
    var state = Store.getState();
    if (!accountDifferenceView || accountDifferenceView.project !== state.project ||
        accountDifferenceView.generation !== state.dataGeneration || accountDifferenceView.info !== info) {
      accountDifferenceView = { project: state.project, generation: state.dataGeneration, info: info,
        counts: {}, expanded: {}, pages: {}, countNodes: {} };
    }
    return accountDifferenceView;
  }

  function mappingDifferenceCountText(kind, count) {
    return (kind === 'mappingOnly' ? '本案沒有的科目：' : '配對檔未列的科目：') +
      (count == null ? '尚未核對' : Number(count).toLocaleString() + ' 個');
  }

  function mappingDifferenceNoticeHtml(info) {
    if (!info) { return ''; }
    var view = mappingDifferenceView(info);
    // 使用者 2026-10-10 認為說明過多，兩份清單只留名稱與筆數，不再附說明句。
    return ['mappingOnly', 'unmapped'].map(function (kind) {
      return '<p class="import-card__status"><span data-difference-count="' + kind + '">' +
        mappingDifferenceCountText(kind, view.counts[kind]) + '</span>' +
        ' <button type="button" class="btn btn--ghost btn--tiny jet-disclosure" data-mapping-difference="' + kind + '"' +
        Ui.disclosureAttributes(!!view.expanded[kind], 'difference-list-' + kind) + '>列出科目</button></p>' +
        '<div class="account-mapping-checks__list" id="difference-list-' + kind + '" data-difference-list="' + kind + '" data-motion-key="difference-list-' + kind + '" data-collapsible' + (view.expanded[kind] ? '' : ' hidden') + '></div>';
    }).join('');
  }

  function bindMappingDifferenceLists(container) {
    var view = mappingDifferenceView(Store.getState().importState.accountMapping);
    ['mappingOnly', 'unmapped'].forEach(function (kind) {
      var button = container.querySelector('[data-mapping-difference="' + kind + '"]');
      var list = container.querySelector('[data-difference-list="' + kind + '"]');
      if (!button || !list) { return; }
      view.countNodes[kind] = container.querySelector('[data-difference-count="' + kind + '"]');
      function mount() {
        list.innerHTML = '<ul class="kv-list" data-difference-rows></ul>';
        var rows = list.querySelector('[data-difference-rows]');
        var listed = 0;
        var page = view.pages[kind] || (view.pages[kind] = {});
        Ui.bindPagedTable(list, {
          autoLoad: true, viewState: page,
          fetchPage: function (cursor) {
            return global.JetApi.queryAccountMappingDifferencePage({ kind: kind, cursor: cursor, pageSize: 200 }).then(function (data) {
              if (mappingDifferenceView(Store.getState().importState.accountMapping) === view && view.pages[kind] === page && data.totalCount != null) {
                view.counts[kind] = data.totalCount;
                if (view.countNodes[kind]) { view.countNodes[kind].textContent = mappingDifferenceCountText(kind, data.totalCount); }
              }
              return data;
            });
          },
          appendRows: function (items) {
            function fill() {
              rows.insertAdjacentHTML('beforeend', items.map(function (row) {
                return '<li>' + Ui.esc(row.accountCode || '（空白科目編號）') + (row.accountName ? '　' + Ui.esc(row.accountName) : '') + '</li>';
              }).join(''));
            }
            // 第一次放入列時走 JetMotion；後續換頁直接替換資料。
            if (listed === 0) { morphCheckList(list, fill); } else { fill(); }
            listed += items.length;
          },
          clearRows: function () { rows.innerHTML = ''; }
        });
      }
      if (view.expanded[kind]) { mount(); }
      button.addEventListener('click', function () {
        view.expanded[kind] = !view.expanded[kind];
        list.hidden = !view.expanded[kind]; button.setAttribute('aria-expanded', String(view.expanded[kind]));
        if (view.expanded[kind]) { view.pages[kind] = {}; mount(); }
      });
    });
  }

  function accountMappingCardHtml(info, mappingReady) {
    var status = info
      ? '<p class="import-card__status import-card__status--ok">目前已儲存 ' + info.rowCount + ' 個科目的配對。</p>'
      : '<p class="import-card__status">尚未儲存科目配對。</p>';

    // 已匯入時恆提供「預覽科目配對」(沿用全域資料預覽面板);與門檻無關。
    var previewBtn = info
      ? '<button type="button" class="btn btn--ghost" data-action="preview-account-mapping"' +
          ' title="檢視已匯入的科目配對">預覽</button>'
      : '';

    // 科目配對需要已整理的科目，不依賴驗證結果。
    var importControls = mappingReady
      ? '<button type="button" class="btn' + (info ? ' btn--ghost' : '') +
          '" data-action="import-account-mapping">' +
          (info ? '重新匯入科目配對檔' : '匯入科目配對檔') + '</button>'
      : '';
    // 說明和按鈕分欄：GL 未確認時，原因放在說明欄；已有配對時才提醒重新匯入會覆蓋。
    var importNote = !mappingReady
      ? '<p class="rule-card__gate">GL 尚未確認配對，請先按「確認配對」。</p>'
      : (info ? '<p class="rule-card__sub">重新匯入會覆蓋目前全部配對。</p>' : '');
    var checks = blankCategoryNoticeHtml(info) + mappingDifferenceNoticeHtml(info);

    // 空白範本由目前 GL/TB 科目產生，沒有驗證 run 的依賴。
    var templateBtn = mappingReady
      ? '<button type="button" class="btn btn--ghost" data-action="ensure-account-mapping-template">準備科目配對檔</button>' +
        '<button type="button" class="btn btn--ghost" data-action="open-account-mapping-folder">開啟案件資料夾</button>'
      : '';

    // 兩個步驟各占一列：標題、說明、按鈕左右排開；狀態與清單接在同一列下方（使用者 2026-10-10 選定）。
    return (
      '<section class="account-mapping-workflow" data-bind="account-mapping-card" tabindex="-1">' +
        '<div class="account-mapping-workflow__part">' +
          '<h4 class="rule-card__title">1. 取得配對檔</h4>' +
          '<div class="account-mapping-workflow__text"><p class="rule-card__sub">在「Standardized Account Name*」欄填入分類後存檔；也可匯入前一個案件的配對檔。</p></div>' +
          '<div class="import-card__actions">' + templateBtn + '</div>' +
          validationOutputStatusHtml('template') +
        '</div>' +
        '<div class="account-mapping-workflow__part">' +
          '<h4 class="rule-card__title">2. 匯入配對檔</h4>' +
          '<div class="account-mapping-workflow__text">' + status + importNote + '</div>' +
          '<div class="import-card__actions">' + importControls + previewBtn + '</div>' +
          (checks ? '<div class="account-mapping-checks" data-motion-key="account-mapping-checks">' + checks + '</div>' : '') +
        '</div>' +
        (mappingReady ? '<div class="account-mapping-rebuild">' +
          '<p>重建會清空檔案內的分類。</p>' +
          '<button type="button" class="btn btn--ghost" data-action="download-account-mapping-template">重建科目配對檔</button></div>' : '') +
      '</section>'
    );
  }

  /* ---- 科目分類（taxonomy）編輯器 --------------------------------------------- */

  // 目前顯示用的分類清單：優先使用本地草稿，否則鏡射後端 snapshot。
  // 後端 revision 換版時丟棄草稿，避免把舊版覆寫回較新的分類設定。
  function taxonomyRows(state) {
    var snapshot = state.taxonomy;
    var revision = snapshot ? snapshot.revision : null;
    if (taxonomyDraft && taxonomyDraftRevision === revision) { return taxonomyDraft; }
    taxonomyDraft = null;
    if (taxonomyDraftRevision !== revision) { taxonomyRemoval = null; }
    taxonomyDraftRevision = revision;
    return Ui.taxonomyCategories(state).map(function (category) {
      return {
        rowId: category.categoryId,
        categoryId: category.categoryId,
        label: category.label,
        semanticRole: category.semanticRole,
        parentCategoryId: category.parentCategoryId || null,
        isBuiltIn: category.isBuiltIn
      };
    });
  }

  function startTaxonomyDraft(state) {
    if (!taxonomyDraft) {
      taxonomyDraft = taxonomyRows(state).map(function (row) { return Object.assign({}, row); });
      taxonomyDraftRevision = state.taxonomy ? state.taxonomy.revision : null;
    }
    return taxonomyDraft;
  }

  // 前端引導檢查：顯示名稱必填且不重複（不分大小寫）。長度與身分規則仍由系統端裁定。
  function taxonomyProblems(rows) {
    var problems = [];
    if (rows.some(function (row) { return !row.label || !row.label.trim(); })) {
      problems.push('每個分類都需要顯示名稱');
    }
    var seen = {};
    var duplicated = rows.some(function (row) {
      var key = String(row.label || '').trim().toUpperCase();
      if (!key) { return false; }
      if (seen[key]) { return true; }
      seen[key] = true;
      return false;
    });
    if (duplicated) { problems.push('顯示名稱不可重複'); }
    if (rows.some(function (row) {
      return (row.label || '').trim().length > Ui.TAXONOMY_MAX_LABEL_LENGTH;
    })) {
      problems.push('顯示名稱最多 ' + Ui.TAXONOMY_MAX_LABEL_LENGTH + ' 個字');
    }
    return problems;
  }

  function taxonomyCardHtml(state) {
    var rows = taxonomyRows(state);
    var problems = taxonomyProblems(rows);
    var dirty = !!taxonomyDraft;

    var tree = Ui.taxonomyTree({ taxonomy: { categories: rows } });
    // 上下層的連接線：每一層一格。上層後面還有同一層的分類時畫直線；自己這一層畫 ├，是最後一個時畫 └。
    // 只算畫面要畫的線，不改分類資料。
    function treeGuidesHtml(index) {
      var depth = tree[index].depth;
      if (!depth) { return ''; }
      function continues(level) {
        for (var next = index + 1; next < tree.length; next += 1) {
          if (tree[next].depth <= level) { return tree[next].depth === level; }
        }
        return false;
      }
      var cells = '';
      for (var level = 1; level < depth; level += 1) { cells += '<i' + (continues(level) ? ' class="is-through"' : '') + '></i>'; }
      return '<span class="taxonomy-row__tree" aria-hidden="true">' + cells +
        '<i class="' + (continues(depth) ? 'is-branch' : 'is-last') + '"></i></span>';
    }
    var items = tree.map(function (row, index) {
      var roleOptions = Ui.ACCOUNT_TAXONOMY_ROLES.map(function (role) {
        return '<option value="' + role.value + '"' +
          (row.semanticRole === role.value ? ' selected' : '') + '>' + role.label + '</option>';
      }).join('');
      var descendants = Ui.taxonomySelectionCategories({ taxonomy: { categories: rows } }, [row.categoryId], 'subtree');
      return '<li class="taxonomy-row' + (row.isBuiltIn ? ' is-builtin' : '') + '" data-motion-key="' + Ui.esc('taxonomy-' + row.rowId) + '" data-taxonomy-depth="' + row.depth +
        '" style="--taxonomy-depth:' + row.depth + '">' + treeGuidesHtml(index) +
        '<div class="taxonomy-row__field"><label for="taxonomy-label-' + index + '">分類名稱</label>' +
        '<input class="form__input taxonomy-row__label" type="text" id="taxonomy-label-' + index + '" aria-describedby="taxonomy-name-help"' +
          ' data-taxonomy-label="' + Ui.esc(row.rowId) + '" data-focus-key="taxonomy-label-' + Ui.esc(row.rowId) + '" value="' + Ui.esc(row.label) +
          '" maxlength="' + Ui.TAXONOMY_MAX_LABEL_LENGTH + '"></div>' +
        '<div class="taxonomy-row__field taxonomy-row__advanced" data-motion="fade" data-motion-key="' + Ui.esc('taxonomy-role-' + row.rowId) + '"' + (taxonomyAdvanced ? '' : ' hidden') + '><label for="taxonomy-role-' + index + '">分類用途</label>' +
        '<select id="taxonomy-role-' + index + '" aria-describedby="taxonomy-role-help" data-taxonomy-role="' + Ui.esc(row.rowId) + '"' +
          ' data-focus-key="taxonomy-role-' + Ui.esc(row.rowId) + '"' +
          (row.isBuiltIn ? ' disabled aria-disabled="true"' : '') + '>' + roleOptions + '</select></div>' +
        '<label class="taxonomy-row__field taxonomy-row__advanced" data-motion="fade" data-motion-key="' + Ui.esc('taxonomy-parent-' + row.rowId) + '"' + (taxonomyAdvanced ? '' : ' hidden') + '>上層分類<select aria-describedby="taxonomy-parent-help" data-taxonomy-parent="' + Ui.esc(row.rowId) + '" data-focus-key="taxonomy-parent-' + Ui.esc(row.rowId) + '"><option value="">無（最上層）</option>' +
          tree.filter(function (candidate) { return !descendants.some(function (descendant) { return descendant.categoryId === candidate.categoryId; }); }).map(function (candidate) {
            return '<option value="' + Ui.esc(candidate.categoryId) + '"' + (row.parentCategoryId === candidate.categoryId ? ' selected' : '') + '>' +
              Ui.esc('　'.repeat(candidate.depth) + (candidate.depth ? '└ ' : '') + (candidate.label || '尚未命名的分類')) + '</option>';
          }).join('') + '</select></label>' +
        (row.isBuiltIn ? '<span class="taxonomy-row__badge" title="JET 內建的分類，可以改名，不能移除">內建</span>' : '') +
        '<div class="taxonomy-row__actions">' +
        (row.isBuiltIn ? '' : '<button type="button" class="btn btn--ghost btn--tiny" data-taxonomy-remove="' + Ui.esc(row.rowId) + '">移除分類</button>') + '</div>' +
        '</li>';
    }).join('');

    return (
      '<section class="account-classification' + (taxonomyAdvanced ? ' is-advanced' : '') + '" data-bind="account-taxonomy-card" data-motion-key="account-taxonomy">' +
        '<div class="rule-card__head"><h4 class="rule-card__title">分類設定</h4>' +
        '<button type="button" class="btn btn--ghost btn--tiny jet-disclosure" data-action="toggle-taxonomy-advanced" data-focus-key="taxonomy-advanced"' +
          Ui.disclosureAttributes(taxonomyAdvanced, 'taxonomy-guide') + '>進階設定</button></div>' +
        '<dl class="taxonomy-guide" id="taxonomy-guide" data-motion-key="taxonomy-guide"' + (taxonomyAdvanced ? '' : ' hidden') + '>' +
          '<div><dt>分類名稱</dt><dd id="taxonomy-name-help">用於配對檔與篩選條件，可依案件命名。</dd></div>' +
          '<div><dt>分類用途</dt><dd id="taxonomy-role-help">自訂分類需要參與收入與現金等借貸組合檢查時才設定。Cash 已對應現金，無須另設。</dd></div>' +
          '<div><dt>上層分類</dt><dd id="taxonomy-parent-help">需要將多個分類一起篩選時，可歸入同一上層。新增下層分類時，分類用途預設沿用上層，之後可以自行修改。</dd></div>' +
        '</dl>' +
        taxonomyRemovalHtml(rows) +
        '<ul class="taxonomy-list" data-preserve-scroll="taxonomy-list" data-motion-list="taxonomy">' + items + '</ul>' +
        '<p class="form-notice" data-bind="taxonomy-problems" data-collapsible' + (problems.length ? '' : ' hidden') + '>' +
          (problems.length ? '尚需補齊：' + problems.map(Ui.esc).join('、') : '') + '</p>' +
        '<div class="import-card__actions">' +
          '<button type="button" class="btn btn--ghost btn--add" data-action="add-taxonomy-category" data-focus-key="taxonomy-add">新增分類</button>' +
          '<button type="button" class="btn" data-action="save-taxonomy"' +
            (dirty && problems.length === 0 ? '' : ' disabled') + '>儲存分類設定</button>' +
          '<button type="button" class="btn btn--ghost" data-action="reset-taxonomy" data-collapsible-inline' +
            (dirty ? '' : ' hidden') + '>取消變更</button>' +
        '</div>' +
        '<p class="rule-card__gate" data-bind="taxonomy-dirty-notice" data-collapsible' + (dirty ? '' : ' hidden') +
          '>分類設定尚未儲存。</p>' +
      '</section>'
    );
  }

  function taxonomyRemovalHtml(rows) {
    var removed = taxonomyRemoval && rows.find(function (row) { return row.rowId === taxonomyRemoval; });
    if (!removed) { return ''; }
    var children = rows.filter(function (row) { return row.parentCategoryId === removed.categoryId; });
    var parent = rows.find(function (row) { return row.categoryId === removed.parentCategoryId; });
    // 確認後果而不是錯誤，用「需要注意」的琥珀色（使用者 2026-10-09 裁定）。
    return '<div class="form-notice form-notice--warning" data-motion-key="taxonomy-removal" role="alert">移除「' + Ui.esc(removed.label) + '」後，它下面的「' +
      children.map(function (row) { return Ui.esc(row.label || '尚未命名的分類'); }).join('、') + '」會改放到' +
      (parent ? '「' + Ui.esc(parent.label) + '」底下' : '最上層') + '。' +
      '<button type="button" class="btn btn--ghost btn--tiny" data-action="confirm-taxonomy-remove">確認移除分類</button>' +
      '<button type="button" class="btn btn--ghost btn--tiny" data-action="cancel-taxonomy-remove">先不移除</button></div>';
  }

  function removeTaxonomyRow(rowId) {
    var rows = startTaxonomyDraft(Store.getState());
    var removed = rows.find(function (row) { return row.rowId === rowId; });
    if (!removed) { return; }
    taxonomyDraft = rows.filter(function (row) { return row !== removed; }).map(function (row) {
      return row.parentCategoryId === removed.categoryId
        ? Object.assign({}, row, { parentCategoryId: removed.parentCategoryId || null }) : row;
    });
    taxonomyRemoval = null; Store.touch();
  }

  function bindTaxonomyCard(container) {
    var card = container.querySelector('[data-bind="account-taxonomy-card"]');
    if (!card) { return; }

    card.querySelectorAll('[data-taxonomy-label]').forEach(function (input) {
      input.addEventListener('input', function () {
        var rowId = input.getAttribute('data-taxonomy-label');
        var rows = startTaxonomyDraft(Store.getState());
        rows.forEach(function (row) {
          if (row.rowId === rowId) { row.label = input.value; }
        });
        // 草稿只存在此編輯器；就地更新衍生提示，不換掉正在組字或移動游標的 input。
        // 保存、取消、新增與移除仍走完整重繪，保存前也會重新檢查同一份草稿。
        // 提示與「取消變更」原地收放（data-collapsible、data-collapsible-inline）；收起時保留原來的字，
        // 才看得到它收回去，不會先變成空白再消失（使用者 2026-10-13 要求展開與收合一致）。
        var problems = taxonomyProblems(rows);
        var notice = card.querySelector('[data-bind="taxonomy-problems"]');
        if (problems.length) { notice.textContent = '尚需補齊：' + problems.join('、'); }
        notice.hidden = problems.length === 0;
        card.querySelector('[data-action="save-taxonomy"]').disabled = problems.length > 0;
        card.querySelector('[data-action="reset-taxonomy"]').hidden = false;
        card.querySelector('[data-bind="taxonomy-dirty-notice"]').hidden = false;
      });
    });

    card.querySelectorAll('[data-taxonomy-role]').forEach(function (select) {
      select.addEventListener('change', function () {
        var rowId = select.getAttribute('data-taxonomy-role');
        var rows = startTaxonomyDraft(Store.getState());
        rows.forEach(function (row) {
          if (row.rowId === rowId && !row.isBuiltIn) { row.semanticRole = select.value; row.roleExplicit = true; }
        });
        Store.touch();
      });
    });

    card.querySelectorAll('[data-taxonomy-parent]').forEach(function (select) {
      select.addEventListener('change', function () {
        var rows = startTaxonomyDraft(Store.getState());
        var row = rows.find(function (item) { return item.rowId === select.getAttribute('data-taxonomy-parent'); });
        if (row) {
          row.parentCategoryId = select.value || null;
          var parent = rows.find(function (item) { return item.categoryId === row.parentCategoryId; });
          if (row.isNew && !row.roleExplicit) { row.semanticRole = parent ? parent.semanticRole : 'others'; }
          Store.touch();
        }
      });
    });

    card.querySelectorAll('[data-taxonomy-remove]').forEach(function (button) {
      button.addEventListener('click', function () {
        var rowId = button.getAttribute('data-taxonomy-remove');
        var rows = taxonomyRows(Store.getState());
        var removed = rows.find(function (row) { return row.rowId === rowId; });
        if (removed && rows.some(function (row) { return row.parentCategoryId === removed.categoryId; })) {
          taxonomyRemoval = rowId; Store.touch();
        } else { removeTaxonomyRow(rowId); }
      });
    });
    var confirmRemove = card.querySelector('[data-action="confirm-taxonomy-remove"]');
    if (confirmRemove) {
      confirmRemove.addEventListener('click', function () {
        // 確認移除後，那一列先輕微快速地晃一下再收起來，讓使用者知道哪一個分類被拿掉（使用者 2026-10-13）。
        // 確認提示本身只收起來、不晃；直接按「移除分類」（沒有下層）的路徑由 app.js 統一處理。
        if (taxonomyRemoval && global.JetMotion && typeof global.JetMotion.removing === 'function') {
          global.JetMotion.removing({ keys: ['taxonomy-' + taxonomyRemoval] });
        }
        removeTaxonomyRow(taxonomyRemoval);
      });
    }
    var cancelRemove = card.querySelector('[data-action="cancel-taxonomy-remove"]');
    if (cancelRemove) { cancelRemove.addEventListener('click', function () { taxonomyRemoval = null; Store.touch(); }); }

    var addBtn = card.querySelector('[data-action="add-taxonomy-category"]');
    if (addBtn) {
      addBtn.addEventListener('click', function () {
        var rows = startTaxonomyDraft(Store.getState());
        taxonomyDraftSeq++;
        rows.push({
          rowId: 'draft-' + taxonomyDraftSeq,
          categoryId: 'draft-' + taxonomyDraftSeq,
          label: '',
          semanticRole: 'others',
          isNew: true,
          parentCategoryId: null,
          isBuiltIn: false
        });
        Store.touch();
        var rowId = 'draft-' + taxonomyDraftSeq;
        if (global.JetFocus) { global.JetFocus.defer(function () {
          var target = document.querySelector('[data-taxonomy-label="' + rowId + '"]');
          if (target) { target.scrollIntoView({ block: 'nearest', inline: 'nearest' }); }
          return target;
        }); }
      });
    }

    var resetBtn = card.querySelector('[data-action="reset-taxonomy"]');
    if (resetBtn) {
      resetBtn.addEventListener('click', function () {
        taxonomyDraft = null;
        taxonomyRemoval = null;
        Store.touch();
      });
    }

    var saveBtn = card.querySelector('[data-action="save-taxonomy"]');
    if (saveBtn) {
      saveBtn.addEventListener('click', function () {
        var state = Store.getState();
        var rows = taxonomyRows(state);
        if (taxonomyProblems(rows).length) { return; }
        Ui.run('儲存分類設定', function () {
          // 新分類以 draft-N 互相參照；後端同一次保存時配置正式 ID，不把暫時 ID 留在資料庫。
          var categories = rows.map(function (row, index) {
            var item = {
              label: String(row.label || '').trim(),
              ordinal: index,
              semanticRole: row.semanticRole,
              parentCategoryId: row.parentCategoryId || null
            };
            if (row.categoryId) { item.categoryId = row.categoryId; }
            return item;
          });
          return global.JetApi.accountTaxonomySave({
            revision: state.taxonomy ? state.taxonomy.revision : 1,
            categories: categories
          }).then(function (data) {
            taxonomyDraft = null;
            taxonomyRemoval = null;
            taxonomyDraftRevision = data.revision;
            // 儲存後新分類換成正式 ID，畫面上還是同一列；直接換，不當成移除再加入。
            var apply = function () { Store.setTaxonomyAfterSave(data); Store.applyMutationEffects(data); };
            if (global.JetMotion) { global.JetMotion.still(apply); } else { apply(); }
            return data;
          });
        }, { completionMessage: function (data) {
          // 完成句由 Ui.run 接上耗時後記一則（使用者 2026-10-13 裁定成功時只留一則）。
          // 句子照使用者 2026-10-13 勾選頁確認 30。
          return data ? '科目分類已更新，共 ' + data.categories.length + ' 類；預篩選要重新執行。' : '';
        } });
      });
    }
  }

  function exportValidationArtifacts(runData) {
    var runId = runData && runData.resultRef ? runData.resultRef.runId : null;
    if (!runId) { return Promise.reject(new Error('找不到可匯出的驗證結果。')); }
    return performValidationOutput('reports', runId, function () {
      return global.JetApi.exportValidationArtifacts({ runId: runId });
    }, function (data) {
      Store.applyReportExport(data);
      return '驗證報告與抽樣清單已產生，可從下方清單查看。';
    });
  }

  // 單獨產生驗證報告時，這句是整個操作的完成句；和執行驗證一起產生時，是驗證完成前的一則紀錄。
  function validationArtifactsMessage(data) {
    return data ? '已在案件資料夾產生驗證階段的 ' + (data.artifacts || []).length + ' 份報告。' : '';
  }

  function requireValidationOutputActive(projectId, runId, requiresValidation) {
    var state = Store.getState();
    var validation = state.lastRuns.validate;
    if (state.cancellationRequested || !state.project || state.project.projectId !== projectId
        || (requiresValidation && (!validation || validation.resultRef.runId !== runId))) {
      var error = new Error('後續檔案產生已取消，已完成的驗證與檔案會保留。');
      error.code = 'operation_cancelled';
      error.completedResultsPreserved = true;
      throw error;
    }
  }

  function performValidationOutput(kind, runId, action, onSuccess) {
    var start = Store.getState(), project = start.project, generation = start.dataGeneration;
    var projectId = project.projectId;
    requireValidationOutputActive(projectId, runId, kind !== 'template');
    var pending = { runId: runId, status: 'pending', message: kind === 'template' ? '正在準備科目配對範本…' : '正在產生報告與抽樣清單…' };
    Store.setValidationOutput(kind, pending);
    function active() {
      var current = Store.getState(), run = current.lastRuns.validate;
      return current.project === project && current.dataGeneration === generation && current.validationOutput[kind] === pending &&
        (kind === 'template' || run && run.resultRef && run.resultRef.runId === runId);
    }
    return action().then(function (data) {
      // 已完成的 action 結果照實顯示；取消只影響下一個尚未開始的 action。
      if (!active()) { return null; }
      Store.setValidationOutput(kind, { runId: runId, status: 'ready', message: onSuccess(data) });
      return data;
    }).catch(function (error) {
      if (active()) {
        Store.setValidationOutput(kind, { runId: runId, status: 'failed',
          reason: Ui.errorText(error, ''),
          message: (kind === 'template' ? '範本尚未產生：' : '報告尚未產生：') + Ui.errorText(error) + ' 可使用此區按鈕重試。' });
      } else { return null; }
      throw error;
    });
  }

  // 報告與科目配對檔失敗時，區塊內已寫「報告尚未產生」「範本尚未產生」與原因，就不再另外顯示步驟提示框。
  function reportsFailureShown() { return outputFailureShown('reports'); }
  function templateFailureShown() { return outputFailureShown('template'); }
  function outputFailureShown(kind) {
    var output = Store.getState().validationOutput[kind];
    return !!output && output.status === 'failed';
  }

  function exportAccountMappingTemplate(runData, onlyIfMissing) {
    var runId = runData && runData.resultRef ? runData.resultRef.runId : null;
    return performValidationOutput('template', runId, function () {
      return global.JetApi.exportAccountMappingTemplate({ runId: runId, onlyIfMissing: onlyIfMissing });
    }, function (data) {
      return data.disposition === 'kept'
        ? '已保留案件資料夾的 ' + data.fileName + '，不改動其中已填寫的分類。'
        : '已建立 ' + data.fileName + '（' + data.rowCount + ' 個科目）。填妥科目分類並存檔後，即可匯入。';
    });
  }

  function exportValidationOutputs(runData) {
    var start = Store.getState(), project = start.project, generation = start.dataGeneration, failures = [];
    function active() {
      var current = Store.getState();
      return current.project === project && current.dataGeneration === generation && current.lastRuns.validate === runData;
    }
    function preserveFailure(error) {
      // 走到這裡時資料驗證已完成並保存；產生報告時被取消，訊息也要說已完成的部分仍保留
      // （低嚴重度事項 F5，使用者 2026-10-19 要求處理）。
      if (error && error.code === 'operation_cancelled') {
        error.completedResultsPreserved = true;
        throw error;
      }
      failures.push(error);
    }
    return exportValidationArtifacts(runData).then(function (data) {
      var sentence = validationArtifactsMessage(data);
      if (sentence) { Store.addMessage(sentence, 'info'); }
    }).catch(preserveFailure).then(function () {
      if (!active()) { return null; }
      return exportAccountMappingTemplate(runData, true).catch(preserveFailure);
    }).then(function () {
      if (!active()) { return null; }
      if (failures.length) {
        throw new Error('資料驗證已完成，部分檔案尚未產生。已完成的檔案會保留，請在對應區塊重試。');
      }
      return runData;
    });
  }

  function exportPrescreenReport(runData) {
    var runId = runData && runData.resultRef ? runData.resultRef.runId : null;
    if (!runId) { return Promise.reject(new Error('找不到可匯出的預篩選結果。')); }
    var state = Store.getState(), project = state.project, generation = state.dataGeneration;
    function active() {
      var current = Store.getState(), run = current.lastRuns.prescreen;
      return current.project === project && current.dataGeneration === generation && run && run.resultRef && run.resultRef.runId === runId;
    }
    return global.JetApi.exportPrescreenReport({ runId: runId }).then(function (data) {
      if (!active()) { return null; }
      Store.applyReportExport(data);
      return data;
    }).catch(function (error) {
      if (!active()) { return null; }
      throw error;
    });
  }

  // 科目配對區塊綁定:沿用既有 action / store / 資料預覽契約,僅位置從 import-step 移來。
  // 已匯入即可預覽；匯入鈕只在 GL 欄位配對已確認時才會渲染出來，
  // 此處用存在性檢查綁定，所以 bind 不必重複判斷條件。
  function bindAccountMappingCard(container) {
    bindMappingDifferenceLists(container);
    var folderBtn = container.querySelector('[data-action="open-account-mapping-folder"]');
    if (folderBtn) {
      folderBtn.addEventListener('click', function () {
        Ui.run('開啟案件資料夾', function () {
          return global.JetApi.hostOpenFolder({ target: 'projectFolder' });
        });
      });
    }
    var blankBtn = container.querySelector('[data-action="list-blank-categories"]');
    var blankList = container.querySelector('[data-bind="blank-category-list"]');
    if (blankBtn && blankList) {
      var blankView = mappingDifferenceView(Store.getState().importState.accountMapping);
      // 第一次打開才讀第一頁；之後收起再打開或整段重繪，都從 blankView.pages.blank 接回已讀的列，不重讀。
      var mountBlank = function () {
        blankList.dataset.loaded = '1';
        blankList.innerHTML = '<ul class="kv-list" data-bind="blank-category-rows"></ul>';
        var rowsEl = blankList.querySelector('[data-bind="blank-category-rows"]');
        var listed = 0;
        // 掛上時從 blankView.pages.blank 同步接回的已讀列直接放進去：這時清單剛取消 hidden，
        // 走 JetMotion 會在列放進去前先量高度，展開區反而朝空清單的高度長。只有晚到的第一頁才走 JetMotion。
        var mounting = true;
        function appendRows(rows) {
          function fill() {
            rowsEl.insertAdjacentHTML('beforeend', rows.map(function (row) {
              return '<li>' + Ui.esc(row.accountCode) + (row.accountName ? '　' + Ui.esc(row.accountName) : '') + '</li>';
            }).join(''));
          }
          if (listed === 0 && !mounting) { morphCheckList(blankList, fill); } else { fill(); }
          listed += rows.length;
        }
        try {
          Ui.bindPagedTable(blankList, {
            autoLoad: true,
            viewState: blankView.pages.blank || (blankView.pages.blank = {}),
            fetchPage: function (cursor) { return global.JetApi.queryAccountMappingBlankPage({ cursor: cursor, pageSize: 200 }); },
            appendRows: appendRows,
            clearRows: function () { rowsEl.innerHTML = ''; }
          });
        } finally {
          mounting = false;
        }
      };
      if (blankView.expanded.blank) { mountBlank(); }
      blankBtn.addEventListener('click', function () {
        if (!blankList.hidden) {
          blankView.expanded.blank = false;
          blankList.hidden = true; blankBtn.setAttribute('aria-expanded', 'false'); return;
        }
        blankView.expanded.blank = true;
        blankList.hidden = false; blankBtn.setAttribute('aria-expanded', 'true');
        if (!blankList.dataset.loaded) { mountBlank(); }
      });
    }

    var previewBtn = container.querySelector('[data-action="preview-account-mapping"]');
    if (previewBtn && Ui.openDataPreview) {
      previewBtn.addEventListener('click', function () {
        Ui.openDataPreview('accountMappings');
      });
    }

    // 後端以目前科目產生工作檔；不以驗證結果限制使用者準備或重建配對檔。
    var templateBtn = container.querySelector('[data-action="download-account-mapping-template"]');
    if (templateBtn) {
      templateBtn.addEventListener('click', function () {
        // 操作名稱照按鈕（使用者 2026-10-13 勾選頁確認 L27、L28）；失敗原因寫在區塊內的狀態，不另外在步驟上方顯示。
        Ui.run('重建科目配對檔', function () {
          return exportAccountMappingTemplate(Store.getState().lastRuns.validate, false);
        }, { logCompletion: true, inlineError: templateFailureShown });
      });
    }

    var ensureTemplateBtn = container.querySelector('[data-action="ensure-account-mapping-template"]');
    if (ensureTemplateBtn) {
      ensureTemplateBtn.addEventListener('click', function () {
        Ui.run('準備科目配對檔', function () {
          return exportAccountMappingTemplate(Store.getState().lastRuns.validate, true);
        }, { inlineError: templateFailureShown });
      });
    }

    var btn = container.querySelector('[data-action="import-account-mapping"]');
    if (!btn) { return; }

    btn.addEventListener('click', function () {
      // 匯入科目配對檔會取代第四步的科目分類；還有沒儲存的分類時，選檔前先問，沒有時照舊直接選檔。
      if (!Ui.pendingAccountCategoryCount()) { importAccountMappingFile(); return; }
      Ui.confirmPendingAccountCategories('匯入科目配對檔後，檔案內容會取代第四步的科目分類，這些改動不會保留。', '繼續匯入', { saveable: false })
        .then(function (proceed) { if (proceed) { importAccountMappingFile(); } });
    });

    function importAccountMappingFile() {
      Ui.run('匯入科目配對檔', function () {
        return global.JetApi.hostSelectFile({
          title: '選擇科目配對檔（科目編號、科目名稱、科目分類）',
          extensions: ['.xlsx', '.csv']
        }).then(function (file) {
          if (!file.filePath) { return; }
          return global.JetApi.importAccountMappingFromFile({
            filePath: file.filePath,
            fileName: file.fileName
          }).then(function (data) {
            Store.setAccountMappingState({
              batchId: data.batchId,
              rowCount: data.rowCount,
              fileName: data.fileName,
              importedUtc: data.importedUtc,
              hasAnyCategory: data.hasAnyCategory,
              hasRevenue: data.hasRevenue,
              hasCounterpart: data.hasCounterpart,
              blankCategoryCount: data.blankCategoryCount
            });
            if (data.columnMappingWarning) { Store.addMessage(data.columnMappingWarning, 'warn'); }
            // 文字檔欄位內含換行的提醒，和 GL、TB 匯入同一句、同一種顯示方式（低嚴重度事項，使用者 2026-10-19 要求處理）。
            (data.warnings || []).forEach(function (warning) { Store.addMessage(warning, 'warn'); });
            Store.applyMutationEffects(data);
            var differences = mappingDifferenceView(Store.getState().importState.accountMapping);
            differences.counts = { mappingOnly: data.mappingOnlyCount, unmapped: data.unmappedCount };
            Store.touch();
            return data;
          });
        });
      }, {
        // 完成句由 Ui.run 接上耗時後記一則；沒有選檔時不記（使用者 2026-10-13 裁定成功時只留一則）。
        completionMessage: function (data) {
          // 句子照使用者 2026-10-13 勾選頁確認 33；沿用前一個案件的說明移到「1. 取得配對檔」。
          return data ? '科目配對匯入完成，共 ' + data.rowCount + ' 個科目。' : '';
        }
      });
    }
  }

  // p = prescreen.run 結果（驅動 PRESCREEN_ITEMS）；v = validate.run 結果（驅動移入的空值兩子項）。
  // 空值兩子項雖列在此卡，資料仍來自 validate.run，故獨立用 scope 'pn' 渲染、傳入 v（不與 'p' 混用）。
  function prescreenArtifactHtml(state, p, positioning) {
    if (!p || !p.resultRef || !p.resultRef.runId) { return ''; }
    var artifacts = Ui.currentReportArtifacts(
      state, ['prescreenReport'], { prescreenRunId: p.resultRef.runId });
    return (
      '<div class="report-output" data-motion-key="prescreen-output">' +
        '<div class="report-output__head">' +
          '<div><h4 class="report-output__title">預篩選報告</h4></div>' +
          '<div class="report-output__actions">' + Ui.reportFolderButtonHtml(artifacts) +
            '<button type="button" class="btn btn--ghost" data-action="export-prescreen-report">' +
            (artifacts.length ? '重新產生報告' : '產生報告') + '</button></div>' +
        '</div>' +
        Ui.reportArtifactListHtml(artifacts, '預篩選已完成，報告尚未產生。', { motion: 'prescreen' }) +
      '</div>'
    );
  }

  function prescreenCardHtml(p, v, ready, state, eligibility) {
    var canRun = ready && eligibility.isEligible;
    var positioning = Ui.prescreenPositioningCopy(p);
    var gate = ready && !eligibility.isEligible
      ? '<p class="rule-card__gate">' + Ui.esc(eligibility.reason) + '</p>'
      : '';
    return (
      '<section class="rule-card rule-card--prescreen">' +
        '<div class="rule-card__head">' +
          '<h3 class="rule-card__title" id="prescreen-primary-title">預篩選</h3>' +
          '<button type="button" class="btn" data-action="run-prescreen"' + (canRun ? '' : ' disabled') +
            '>' + (p ? '重新執行預篩選' : '執行預篩選') + '</button>' +
        '</div>' +
        gate +
        lastRunLineHtml(p) +
        '<section class="prescreen-primary" aria-labelledby="prescreen-primary-title">' +
          ruleListHtml(PRESCREEN_AGGREGATE_ITEMS, p, 'pa') +
        '</section>' +
        '<details class="prescreen-signals"' + (prescreenSignalsOpen ? ' open' : '') + '>' +
          '<summary class="prescreen-signals__summary">' +
            '<span>篩選結果</span><span class="prescreen-signals__hint">展開查看</span>' +
          '</summary>' +
          '<div class="prescreen-signals__content">' +
            '<p class="prescreen-signals__guidance">' + Ui.esc(positioning.signalGuidance) + '</p>' +
            ruleListHtml(PRESCREEN_SIGNAL_ITEMS, p, 'ps') +
            '<h5 class="prescreen-signals__secondary-title">資料缺漏輔助檢視</h5>' +
            ruleListHtml(NULL_PRESCREEN_ITEMS, v, 'pn') +
          '</div>' +
        '</details>' +
        prescreenArtifactHtml(state, p, positioning) +
      '</section>'
    );
  }

  // 以後端欄位定義渲染結果表，所有頁面沿用同一份欄位定義。
  // 欄序、欄名與每格的取值鍵都來自 response，畫面不重建這份 schema。
  function renderDynamicPage(body, data, spec) {
    var columns = (data && data.columns) || [];
    var cells = Ui.dynamicColumnCells(columns);
    var pageKey = detailPageKey(body, 'dynamic-' + body.parentElement.getAttribute('data-dynamic-page'));
    body.innerHTML =
      Ui.pageSearchHtml('依傳票號碼查看') +
      '<div class="preview-table__wrap" data-preserve-scroll="' + Ui.esc(pageKey) + '">' +
        '<table class="preview-table"><thead>' + Ui.dynamicColumnHeadHtml(columns) + '</thead>' +
        '<tbody data-dynamic-body></tbody></table>' +
      '</div>';

    var tbody = body.querySelector('[data-dynamic-body]');
    Ui.appendRowsToTbody(tbody, (data && data.rows) || [], cells);

    // 首擊或換排序、搜尋時先清掉這裡的首屏列，再自資料庫端的第一頁接續，避免兩套順序混排。
    Ui.bindPagedTable(body, {
      initialPage: data,
      sourceKind: 'validate',
      viewState: detailPageState('validate', pageKey),
      fetchPage: function (cursor, sort, search) { return spec.fetchPage(cursor, sort, search); },
      appendRows: function (rows) { Ui.appendRowsToTbody(tbody, rows, cells); },
      clearRows: function () { tbody.innerHTML = ''; },
      table: body.querySelector('.preview-table'),
      search: body.querySelector('[data-page-search]')
    });
  }

  function bind(container, ready) {
    Ui.accountMappingEditor.bind(container);
    [['toggle-taxonomy', function () { taxonomyOpen = !taxonomyOpen; }],
      ['toggle-taxonomy-advanced', function () { taxonomyAdvanced = !taxonomyAdvanced; }],
      ['select-account-jet', function () { accountExcelOpen = false; }],
      ['toggle-account-excel', function () { accountExcelOpen = true; }]].forEach(function (item) {
      var button = container.querySelector('[data-action="' + item[0] + '"]');
      if (button) { button.addEventListener('click', function () { item[1](); Store.touch(); }); }
    });
    // 兩個配對方式頁籤的左右鍵、Home、End 由 ui-core.js 的共用頁籤處理，換到那一頁等於按下它。
    Ui.bindTabs(container);
    // 「在 JET 配對」分頁的失敗狀態：切到「用 Excel 配對」，焦點交給同一區的「準備科目配對檔」。
    var retryTemplateInExcel = container.querySelector('[data-action="retry-account-template-in-excel"]');
    if (retryTemplateInExcel) {
      retryTemplateInExcel.addEventListener('click', function () {
        accountExcelOpen = true;
        Store.touch();
        var content = Ui.$('content');
        var target = content && (content.querySelector('[data-action="ensure-account-mapping-template"]') ||
          content.querySelector('#account-method-excel'));
        if (target) { target.focus(); }
      });
    }
    Ui.bindReportArtifacts(container);
    Ui.bindStepFooter(container);
    var signals = container.querySelector('.prescreen-signals');
    if (signals) { signals.addEventListener('toggle', function () { prescreenSignalsOpen = signals.open; }); }

    bindTaxonomyCard(container);
    bindAccountMappingCard(container);

    var validationExportBtn = container.querySelector('[data-action="export-validation-artifacts"]');
    if (validationExportBtn) {
      validationExportBtn.addEventListener('click', function () {
        Ui.run('產生驗證階段報告', function () {
          return exportValidationArtifacts(Store.getState().lastRuns.validate);
        }, { completionMessage: validationArtifactsMessage, inlineError: reportsFailureShown });
      });
    }

    var prescreenExportBtn = container.querySelector('[data-action="export-prescreen-report"]');
    if (prescreenExportBtn) {
      prescreenExportBtn.addEventListener('click', function () {
        Ui.run('產生預篩選報告', function () {
          return exportPrescreenReport(Store.getState().lastRuns.prescreen);
        }, { completionMessage: function (data) { return data ? '已在案件資料夾產生預篩選報告。' : ''; } });
      });
    }

    // 每一種明細各有自己的查詢、排序、搜尋與游標，不再混用兩類空值資料。
    container.querySelectorAll('[data-page-query]').forEach(function (tbody) {
      var id = tbody.getAttribute('data-page-query');
      var spec = DETAIL_PAGE_SPECS[id];
      if (!spec) { return; }
      var detail = tbody.closest('.rule-detail');
      if (!detail) { return; }
      var controller = Ui.bindPagedTable(detail, {
          sourceKind: 'validate',
          viewState: detailPageState('validate', detailPageKey(detail, id)),
          fetchPage: spec.fetchPage,
          appendRows: function (rows) { Ui.appendRowsToTbody(tbody, rows, spec.columns); },
          clearRows: function () { tbody.innerHTML = ''; },
          table: tbody.closest('table'),
          scrollElement: tbody.closest('.preview-table__wrap'),
          search: detail.querySelector('[data-page-search]')
      });
      detail._loadPage = controller.ensureLoaded;
    });

    // 「檢視 / 收合」切換。
    // 對 prescreenPreview 類型：首次展開時觸發惰性抓取；之後只切 hidden。
    // 一般明細表首次展開也會透過 _loadPage 讀取第一頁；reason 只顯示文字。
    container.querySelectorAll('[data-action="toggle-detail"]').forEach(function (btn) {
      var scope = btn.getAttribute('data-scope');
      var idx = btn.getAttribute('data-idx');
      var panel = container.querySelector('[data-bind="rule-detail-' + scope + '-' + idx + '"]');
      if (!panel) { return; }
      function showExpandedDetail() {
        // 只在展開時、且面板含 prescreenPreview 子區塊時觸發惰性抓取。
        if (panel.hidden) { return; }
        if (panel._loadPage) { panel._loadPage(); }

        // 從當前 store 狀態取 runId，作為快取鍵的一部分。
        var state = Store.getState();
        var presRun = state.lastRuns.prescreen;
        var runId = presRun && presRun.resultRef ? presRun.resultRef.runId : null;

        var validationRun = state.lastRuns.validate;
        var validationRunId = validationRun && validationRun.resultRef
          ? validationRun.resultRef.runId : null;

        panel.querySelectorAll('[data-dynamic-page]').forEach(function (block) {
          var pageKey = block.getAttribute('data-dynamic-page');
          var spec = DYNAMIC_PAGE_SPECS[pageKey];
          if (!spec) { return; }
          var cacheKey = (validationRunId || 'unknown') + '|' + pageKey;
          var body = block.querySelector('.rule-detail__preview-body');

          loadDetailPreview(body, cacheKey, 'validate', validationRun,
            function () { return spec.fetchPage(null); },
            function (data) { renderDynamicPage(body, data, spec); });
        });

        panel.querySelectorAll('[data-prescreen-key]').forEach(function (block) {
          var key = block.getAttribute('data-prescreen-key');
          var cacheKey = (runId || 'unknown') + '|' + key;

          var bodyEl = block.querySelector('.rule-detail__preview-body');
          loadDetailPreview(bodyEl, cacheKey, 'prescreen', presRun,
            function () { return global.JetApi.queryPrescreenPage({ ruleKey: key, pageSize: Ui.TABLE_PAGE_SIZE }); },
            function (data) {
              bodyEl.innerHTML = prescreenPageHtml(data, detailPageKey(block, 'prescreen-' + key));
              bindPrescreenTable(block, key, data);
            });
        });
      }
      btn.addEventListener('click', function () {
        panel.hidden = !panel.hidden;
        detailView(detailRunKind(scope)).expanded[scope + '-' + idx] = !panel.hidden;
        btn.setAttribute('aria-expanded', String(!panel.hidden));
        showExpandedDetail();
      });
      if (!panel.hidden) { showExpandedDetail(); }
    });

    if (!ready) { return; }

    container.querySelector('[data-action="run-validate"]').addEventListener('click', function () {
      Ui.run('執行驗證並產生報告', function () {
        return global.JetApi.validateRun({}).then(function (data) {
          Store.setLastRun('validate', data);
          return exportValidationOutputs(data);
        });
      }, { completionMessage: function (data) {
        // 驗證結果的完成句由 Ui.run 接上整個操作的耗時後記一則（使用者 2026-10-13 裁定成功時只留一則）；
        // 報告與範本是這個操作的一部分，各自的紀錄照舊。
        // 各項筆數已寫在下方各項結果，完成句只留一句（使用者 2026-10-13 勾選頁確認 35）。
        return data ? '資料驗證完成' : '';
      } });
    });

    container.querySelector('[data-action="run-prescreen"]').addEventListener('click', function () {
      var eligibility = Ui.completenessEligibility(Store.getState().lastRuns.validate);
      if (!eligibility.isEligible) {
        // 句子照使用者 2026-10-13 勾選頁確認 36。
        Store.addMessage('預篩選尚無法執行：' + eligibility.reason, 'warn');
        return;
      }
      if (!Ui.pendingAccountCategoryCount()) { runPrescreen(); return; }
      Ui.confirmPendingAccountCategories('預篩選只會使用已儲存的分類。', '先不儲存，直接執行')
        .then(function (proceed) { if (proceed) { runPrescreen(); } });
    });

    function runPrescreen() {
      Ui.run('執行預篩選', function () {
        return global.JetApi.prescreenRun({}).then(function (data) {
          Store.setLastRun('prescreen', data);
          return data;
        });
      }, { completionMessage: function (data) {
        // 完成句由 Ui.run 接上耗時後記一則（使用者 2026-10-13 裁定成功時只留一則）。
        // 各項筆數已寫在下方各項結果，完成句只留一句（使用者 2026-10-13 勾選頁確認 37）。
        return data ? '預篩選已更新' : '';
      } });
    }
  }

  Ui.registerStep('validate', render);
})(window);
