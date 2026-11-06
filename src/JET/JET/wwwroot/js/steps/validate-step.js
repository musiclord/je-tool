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
  var differencePrecisionNote = '差額顯示為「不足 0.01」時，完整數字請見驗證報告。';

  function withControlTotals(validation, detail) {
    var totals = validation.completenessTest.storedTotals || {}, source = totals.eligibleSource || {}, target = totals.gl || {};
    var control = {
      kind: 'table',
      // 兩欄都是 JET 自己算的數字，一欄在確認配對時算出，一欄在存下後重新計算；不比對來源檔本身。
      columns: ['核對項目', '確認配對時算出', '存下後重新計算'],
      numeric: [1, 2],
      rows: [['分錄筆數', Ui.countText(source.rowCount), Ui.countText(target.rowCount)],
        ['借方合計', Ui.money(source.totalDebit), Ui.money(target.totalDebit)],
        ['貸方合計', Ui.money(source.totalCredit), Ui.money(target.totalCredit)]]
    };
    return { kind: 'sections', sections: detail ? [control, detail] : [control] };
  }

  // 完整性測試的第一個檢查：存下的分錄筆數與借貸合計，和確認配對時算出的是否一致。後端 storedTotals 缺漏時所有值為 null，不算不符。
  function controlTotalsMismatch(validation) {
    var storedTotals = validation.completenessTest.storedTotals;
    return !!storedTotals && (storedTotals.rowCountMatch === false || storedTotals.amountMatch === false);
  }

  // 命中型規則（row-tag）：命中是「值得留意」而非「錯誤」。
  // 新的預篩選結果一定帶每條規則的結果，只為舊結果缺規則鍵留下的「未執行」已拿掉（裁定 2026-11-02-10）。
  function hitCountStatus(rule, tip) {
    if (rule.naReason) { return naStatus(rule); }
    if (rule.count == null) { return null; }
    var n = Number(rule.count);
    return n > 0
      ? { tone: 'hit', text: Ui.countText(n) + ' 筆', tip: tip }
      : { tone: 'ok', text: '未發現', tip: tip };
  }

  // 過帳／核准成對計數（週末／假日規則）。naReason 只在整張卡無法執行時才有；只有核准側沒有檢查時
  // approvalCount 為 null，提示用後端的 approvalNaReason，說明只檢查了總帳日期。
  function pairCountStatus(rule) {
    if (rule.naReason) { return naStatus(rule); }
    if (rule.postingCount == null && rule.approvalCount == null) { return null; }
    // 狀態寫欄位名稱，不寫「過帳」「核准」（裁定 2026-11-03-10）；核准側沒有檢查時只寫「—」。
    var approval = rule.approvalCount == null ? '傳票核准日 —' : '傳票核准日 ' + Ui.countText(rule.approvalCount) + ' 筆';
    var any = Number(rule.postingCount) > 0 || Number(rule.approvalCount) > 0;
    return {
      tone: any ? 'hit' : 'ok',
      text: '總帳日期 ' + Ui.countText(rule.postingCount) + ' 筆，' + approval,
      tip: rule.approvalNaReason || undefined
    };
  }

  // 週末與假日卡的明細用同一個方法：整張卡無法執行時只顯示後端的原因；只有核准側沒有檢查時，
  // 照常列出總帳日期那一側的分錄，前面放後端那句「只檢查了總帳日期」的說明。
  function activityDetail(activity, postingKey, approvalKey) {
    if (activity.naReason) { return { kind: 'reason', text: activity.naReason }; }
    var previews = [];
    if (Number(activity.postingCount) > 0) { previews.push({ label: '依總帳日期', key: postingKey }); }
    if (activity.approvalCount != null && Number(activity.approvalCount) > 0) { previews.push({ label: '依傳票核准日', key: approvalKey }); }
    var sections = [];
    if (activity.approvalNaReason) { sections.push({ kind: 'reason', text: activity.approvalNaReason }); }
    if (previews.length > 0) { sections.push({ kind: 'prescreenPreview', previews: previews }); }
    if (sections.length === 0) { return null; }
    return sections.length === 1 ? sections[0] : { kind: 'sections', sections: sections };
  }

  // 預篩選命中首頁快取：以 resultId + '|' + prescreenKey 為鍵；完整明細仍由使用者以 keyset 逐頁載入。
  // 每次新的 run（新 resultId）自然不命中快取，重新抓取。
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
    if (!view || view.run !== state.results[runKind]) {
      view = { run: state.results[runKind], expanded: {}, pages: {} };
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
      return latest.project === project && latest.dataGeneration === generation && latest.results[runKind] === run;
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
  // 不在展開後才一下跳開（裁定 2026-10-13-11）。body 自己有 data-motion-key（detailBodyKey）。
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
  var categoryDraft = null;
  var categoryDraftRevision = null;
  var categoryOpen = false;
  var categoryAdvanced = false;
  var accountExcelOpen = false;
  // 「用 Excel 配對」選了有多個工作表的 Excel 檔時，先在托盤裡選工作表再匯入（裁定 2026-11-05-07）：
  // { projectId, filePath, fileName, sheets, sheetName }；取消、匯入或換案件後清掉。
  var accountSheetPick = null;
  // 工作底稿的科目配對工作表，和 WorkingPaperSheetCatalog.AccountMapping 同名；檔案裡有它時預先選它。
  var WORKING_PAPER_ACCOUNT_MAPPING_SHEET = '自動化工具-科目配對資訊';
  // JET 科目配對範本的主工作表（AccountMappingTemplateWriter）與報告共用的隱藏中繼資料工作表（ReportWorkbookMetadataFormat.WorksheetName）。
  var ACCOUNT_MAPPING_TEMPLATE_SHEET = 'AccountMapping';
  var REPORT_METADATA_SHEET = 'JET_Metadata';
  // 新增分類的本地識別（尚未取得後端 stable ID 前只用於 DOM 對位；絕不送進 payload）。
  var categoryDraftSeq = 0;
  var categoryRemoval = null;
  // 預篩選「篩選結果」有沒有展開；重繪時照原樣畫出來，不每次都收起。
  var prescreenSignalsOpen = false;

  Ui.registerWorkflowReset(function () {
    prescreenPreviewCache = {};
    dynamicPageCache = {};
    detailPreviewProject = null;
    detailPreviewGeneration = null;
    detailPreviewRuns = {};
    detailViews = { project: null, generation: null, runs: {} };
    categoryDraft = null;
    categoryDraftRevision = null;
    categoryOpen = false;
    categoryAdvanced = false;
    categoryRemoval = null;
    accountExcelOpen = false;
    prescreenSignalsOpen = false;
  });

  var PRESCREEN_PAGE_COLUMNS = [
    function (row) { return row.voucherNumber || ''; },
    function (row) { return row.lineItem || '—'; },
    function (row) { return row.glDate || ''; },
    function (row) { return (row.accountNumber || '') + ' ' + (row.accountName || ''); },
    function (row) { return row.description || ''; },
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
      // 金額與筆數的資料格靠右（裁定 2026-10-13-05），表頭統一靠左（裁定 2026-10-16-01）；detail.numeric 列出是數字的欄。
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
  // 目前 num() 的呼叫點全為金額（tbAmount/glAmount/diff/debit/credit）；若日後拿它顯示計數，請改用 Ui.countText。
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
        function (r) { return r.accountNumber || ''; },
        function (r) { return r.accountName || ''; },
        amountColumn(function (r) { return num(r.tbAmount); }),
        amountColumn(function (r) { return num(r.glAmount); }),
        amountColumn(function (r) { return Ui.moneyDifference(r.diff); })
      ]
    },
    voucherBalance: {
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryVoucherBalancePage({ cursor: cursor, pageSize: 200, sort: sort || null, search: search || null });
      },
      columns: [
        function (r) { return r.voucherNumber || ''; },
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
  function blankPageSpec(category, categoryLabel) {
    return {
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryBlankRecordsPage({ category: category, cursor: cursor, pageSize: 200, sort: sort || null, search: search || null });
      },
      columns: [
        function (r) { return r.voucherNumber || ''; },
        function (r) { return r.accountNumber || ''; },
        function (r) { return r.glDate || ''; },
        function (r) { return r.description || ''; },
        function () { return categoryLabel; }
      ]
    };
  }

  // 後端 blankRecordsPage 白名單四 category → 「異常類別」顯示字(對齊 issuesCn map)。
  // 空白總帳日期已改由總帳日期空白承載，不再屬於資料缺漏輔助檢視。
  DETAIL_PAGE_SPECS['null-approvalDateNotInPeriod'] = blankPageSpec('approvalDateNotInPeriod', '核准日不在期間');
  DETAIL_PAGE_SPECS['null-blankVoucherNumber'] = blankPageSpec('blankVoucherNumber', '空白傳票號碼');
  DETAIL_PAGE_SPECS['null-blankAccountNumber'] = blankPageSpec('blankAccountNumber', '空白科目編號');

  // 總帳日期空白全量明細（query.sourceQualityPage）：不套查核期間述詞，因此仍看得到
  // 被期間界定排除的空白總帳日期；欄序與下方 detail 的固定欄一致。
  DETAIL_PAGE_SPECS.sourceQuality = {
    fetchPage: function (cursor, sort, search) {
      return global.JetApi.querySourceQualityPage({ cursor: cursor, pageSize: 200, sort: sort || null, search: search || null });
    },
    columns: [
      function (r) { return sourceQualityCategoryLabel(r.category); },
      function (r) { return r.sourceLabel || ''; },
      function (r) { return r.sourceRowNumber == null ? '' : String(r.sourceRowNumber); },
      function (r) { return r.voucherNumber || ''; },
      function (r) { return r.accountNumber || ''; },
      function (r) { return r.description || ''; }
    ]
  };

  // 總帳日期空白 finding 類別 → 顯示字。目前後端只回一種類別，未登錄值原樣顯示。
  function sourceQualityCategoryLabel(category) {
    return category === 'blankGlDate' ? '總帳日期空白' : (category || '');
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

  // 動態欄結果表的首頁快取（以 resultId + pageKey 為鍵；新 run 自然不命中）。
  var dynamicPageCache = {};

  // issues 陣列（英文鍵）→ 中文顯示，以「、」接合。
  function issuesCn(issues) {
    // 空白總帳日期已移出資料缺漏輔助檢視（改由總帳日期空白承載），故不在這份封閉類別內。
    var map = {
      accountNumber: '空白科目編號', voucherNumber: '空白傳票號碼', description: '空白摘要',
      approvalDate: '核准日不在期間'
    };
    return (issues || []).map(function (k) { return map[k] || k; }).join('、');
  }

  // 資料缺漏輔助檢視在後端是單一規則（blank_records_test）；UI 依異常類別拆成四個子項呈現。
  // 每個子項讀對應子計數作徽章，詳情用 blankRows 的 issues 標籤過濾出該類列
  // 摘要樣本只供尚未展開的初始結構；展開後直接讀取該類別第一頁，不以樣本判斷是否有明細。
  // categories 只放一個 blankRecordsPage 的 category 白名單值，對應 DETAIL_PAGE_SPECS 的 null-<category>。
  // naReasonOf：後端判定這一類無法檢查時回傳它寫的原因（例如傳票核准日不可用），畫面只顯示這一句，不寫「通過」。
  function blankSubItem(title, desc, issueKeys, countOf, categories, naReasonOf) {
    function naReason(v) { return naReasonOf ? naReasonOf(v.blankRecordsTest) : null; }
    return {
      title: title,
      desc: desc,
      status: function (v) {
        if (!v) { return null; }
        if (naReason(v)) { return naStatus({ naReason: naReason(v) }); }
        var n = countOf(v.blankRecordsTest);
        return n > 0
          ? { tone: 'alert', text: Ui.countText(n) + ' 筆異常' }
          : { tone: 'ok', text: '通過' };
      },
      detail: function (v) {
        if (v && naReason(v)) { return { kind: 'reason', text: naReason(v) }; }
        if (!v || countOf(v.blankRecordsTest) <= 0) { return null; }
        var rows = (v.blankRecordsTest.blankRows || []).filter(function (r) {
          return (r.issues || []).some(function (k) { return issueKeys.indexOf(k) >= 0; });
        }).map(function (r) {
          return [r.voucherNumber || '', r.accountNumber || '', r.glDate || '', r.description || '', issuesCn(r.issues)];
        });
        return {
          kind: 'table',
          columns: ['傳票號碼', '科目編號', '總帳日期', '摘要', '異常類別'],
          sortAction: 'query.blankRecordsPage', sortKeys: ['voucherNumber', 'accountNumber', 'glDate', 'description', null],
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
      // 照實說明第一項比對的對象。最後一句原本照使用者原句逐字保留，
      // 裁定 2026-10-30-20 改成要確認什麼，不寫「可能」。
      desc: '先確認 JET 存下的分錄筆數與借貸合計，和確認欄位配對時算出的一樣。這一步不和來源檔自己的合計比對。再比對各科目的總帳本期借貸淨額與年度變動金額。金額不符時，要確認 GL 分錄是否完整。',
      status: function (v) {
        if (!v) { return null; }
        var eligibility = Ui.completenessEligibility(v);
        var n = Number(v.completenessTest.diffAccountCount);
        if (!eligibility.isEligible || eligibility.warning) {
          if (v.completenessTest.naReason) {
            return { tone: 'na', text: '無法執行', tip: eligibility.reason || eligibility.warning };
          }
          var text = '未通過';
          if (n > 0) { text = Ui.countText(n) + ' 個科目不符'; }
          else if (controlTotalsMismatch(v)) { text = '存入筆數與合計不一致'; }
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
              d.accountNumber || '',
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
            sortAction: 'query.completenessDiffPage', sortKeys: ['accountNumber', 'accountName', 'tbAmount', 'glAmount', 'diff'],
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
      // 判讀句原本照使用者原件寫，裁定 2026-10-30-20 改成要確認什麼，不寫「可能」。
      desc: '依傳票號碼彙總借方與貸方金額，列出不相等的傳票。借貸不平時，要確認傳票號碼與金額欄位是否正確。',
      status: function (v) {
        if (!v) { return null; }
        var n = Number(v.voucherBalanceTest.unbalancedVoucherCount);
        return n > 0
          ? { tone: 'alert', text: Ui.countText(n) + ' 張傳票不平' }
          : { tone: 'ok', text: '通過' };
      },
      detail: function (v) {
        if (!v) { return null; }
        if (Number(v.voucherBalanceTest.unbalancedVoucherCount) > 0) {
          var rows = (v.voucherBalanceTest.unbalancedVouchers || []).map(function (d) {
            return [
              d.voucherNumber || '',
              Ui.money(d.debit),
              Ui.money(d.credit),
              Ui.moneyDifference(d.diff)
            ];
          });
          return {
            kind: 'table',
            columns: ['傳票號碼', '借方', '貸方', '差額'],
            numeric: [1, 2, 3],
            sortAction: 'query.voucherBalancePage', sortKeys: ['voucherNumber', 'debit', 'credit', 'diff'],
            searchLabel: '依傳票號碼查看',
            rows: rows,
            prefix: differencePrecisionNote,
            pageQuery: 'voucherBalance'
          };
        }
        return null;
      }
    },
    {
      title: '資料可靠性測試',
      desc: '抽出分錄樣本，供核對傳票附件，確認摘要、日期等篩選欄位是否正確。需先確認攸關資料元素的可靠性後，再到第五步進階條件篩選。',
      status: function (v) {
        if (!v) { return null; }
        if (v.infSamplingTest.naReason) { return naStatus(v.infSamplingTest); }
        return { tone: 'info', text: '已抽出 ' + Ui.countText(v.infSamplingTest.sampleSize) + ' 筆' };
      },
      detail: function (v) {
        // INF 抽樣明細的欄位由系統端決定（固定欄 + 已提交的攸關資料元素欄位），
        // 因此展開時才連同欄位定義一起取回，畫面不預先假設欄序或欄名。
        if (!v || v.infSamplingTest.naReason) { return null; }
        if (Number(v.infSamplingTest.sampleSize) <= 0) { return null; }
        return { kind: 'dynamicPage', pageKey: 'inf' };
      }
    },
    // 傳票核准日不可用時後端不檢查，筆數是 null 並寫原因（裁定 2026-10-23-01）。
    // 核准日空白的分錄照舊版也算不在期間（裁定 2026-11-05-38）。
    blankSubItem('核准日不在期間', '傳票核准日落在查核期間之外或空白的分錄。', ['approvalDate'],
      function (brt) { return Number(brt.approvalDateNotInPeriodCount); }, ['approvalDateNotInPeriod'],
      function (brt) { return brt.approvalDateNotInPeriodNaReason || null; }),
    {
      // 總帳日期空白是 validate.run 的獨立區塊，不是第五條資料驗證規則：它看的是 PBC-GL，
      // 不套查核期間，因此筆數不與資料缺漏輔助檢視的四類相加。
      title: '總帳日期空白',
      desc: '總帳日期空白的分錄，無法判斷是否在查核期間內，因此未納入本次測試。',
      status: function (v) {
        if (!v || !v.sourceQuality) { return null; }
        var n = Number(v.sourceQuality.findingCount);
        return n > 0
          ? { tone: 'alert', text: Ui.countText(n) + ' 筆待確認' }
          : { tone: 'ok', text: '未發現' };
      },
      detail: function (v) {
        if (!v || !v.sourceQuality || Number(v.sourceQuality.findingCount) <= 0) { return null; }
        var rows = (v.sourceQuality.sampleRows || []).map(function (r) {
          return [
            sourceQualityCategoryLabel(r.category),
            r.sourceLabel || '',
            r.sourceRowNumber == null ? '' : String(r.sourceRowNumber),
            r.voucherNumber || '',
            r.accountNumber || '',
            r.description || ''
          ];
        });
        return {
          kind: 'table',
          columns: ['類別', '來源檔', '來源列號', '傳票號碼', '科目編號', '摘要'],
          sortAction: 'query.sourceQualityPage', sortKeys: [null, null, 'sourceRowNumber', 'voucherNumber', 'accountNumber', 'description'],
          searchLabel: '依傳票號碼查看',
          rows: rows,
          prefix: '這些分錄保留在 PBC-GL 中，並列入 GL(未納入測試)。',
          pageQuery: 'sourceQuality'
        };
      }
    }
  ];

  // 資料缺漏輔助檢視的「空白傳票號碼」「空白科目編號」兩個子項列在「風險預篩選」卡的資料缺漏輔助檢視
  // （呈現位置移動，資料來源不變）。兩者仍是後端單一 blank_records_test 規則的子計數，
  // 由 validate.run 產生，因此它們的 runData 一律吃 state.results.validate（不是 prescreen）。
  // 空白摘要只留預篩選訊號那一處，驗證報告照舊由後端寫出；
  // 「核准日不在期間」留在資料驗證卡，因為它是資料品質面（期間界定）而非風險候選面。
  var BLANK_PRESCREEN_ITEMS = [
    blankSubItem('空白傳票號碼', '傳票號碼空白的分錄。與空白科目編號分開列示；傳票號碼與科目編號都空白的分錄，兩份明細都會列出。', ['voucherNumber'],
      function (brt) { return Number(brt.blankVoucherNumberCount); }, ['blankVoucherNumber']),
    blankSubItem('空白科目編號', '科目編號空白的分錄。各類筆數分別計算，不將兩類相加當作分錄總數。', ['accountNumber'],
      function (brt) { return Number(brt.blankAccountNumberCount); }, ['blankAccountNumber'])
  ];

  var PRESCREEN_ITEMS = [
    {
      title: '期末財報準備日起核准之分錄',
      desc: Ui.prescreenConditionDescription('prepPeriodApproval'),
      status: function (p) { return p ? hitCountStatus(p.prepPeriodApproval) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.prepPeriodApproval.naReason) { return { kind: 'reason', text: p.prepPeriodApproval.naReason }; }
        if (Number(p.prepPeriodApproval.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '符合條件的分錄', key: 'prepPeriodApproval' }] };
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
        if (p.preparerSummary.naReason) { return naStatus(p.preparerSummary); }
        // preparers 最多 50 列，人數改用後端的完整人數（空白人員算一組，與流程總覽同一個數字）。
        // 新的預篩選結果一定帶這個數字，只為舊結果留下的「請重新執行預篩選」已拿掉（裁定 2026-11-02-06）。
        return { tone: 'info', text: Ui.countText(p.preparerSummary.totalPreparerCount) + ' 位人員' };
      },
      detail: function (p) {
        if (!p) { return null; }
        if (p.preparerSummary.naReason) { return { kind: 'reason', text: p.preparerSummary.naReason }; }
        if (p.preparerSummary.preparers && p.preparerSummary.preparers.length > 0) {
          var rows = p.preparerSummary.preparers.map(function (c) {
            return [
              c.createdBy == null || !String(c.createdBy).trim() ? '（空白）' : c.createdBy,
              Ui.countText(c.entryCount),
              Ui.money(c.debitTotal),
              Ui.money(c.creditTotal),
              Ui.countText(c.manualCount)
            ];
          });
          var total = p.preparerSummary.totalPreparerCount;
          var prefix = total != null && Number(total) > rows.length
            ? '共 ' + Ui.countText(total) + ' 位人員，這裡列出分錄筆數最多的 ' + Ui.countText(rows.length) + ' 位。'
            : null;
          return { kind: 'table', prefix: prefix, columns: ['傳票建立人員', '分錄筆數', '借方', '貸方', '人工分錄筆數'], numeric: [1, 2, 3, 4], rows: rows };
        }
        return null;
      }
    },
    {
      title: '較少使用之科目',
      desc: '按科目編號統計分錄筆數並由低到高排列，作為了解查核期間分錄分布與少用科目的彙總參考。',
      // 照舊版只列依筆數排序的彙總，不另外數 11 筆以下的科目（裁定 2026-10-29-27）；狀態寫查核期間的科目數，和「依分錄編製者彙總」寫人數一致。
      status: function (p) {
        if (!p) { return null; }
        return { tone: 'info', text: Ui.countText(p.rareAccounts.distinctAccountCount) + ' 個科目' };
      },
      detail: function (p) {
        if (!p) { return null; }
        if (Number(p.rareAccounts.distinctAccountCount) > 0) {
          var rows = (p.rareAccounts.accounts || []).map(function (a) {
            return [
              a.accountNumber || '',
              a.accountName || '',
              Ui.countText(a.entryCount),
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
      desc: '總帳日期或傳票核准日落在第二步設定的每週休息日的分錄（補班日仍納入）。',
      status: function (p) { return p ? pairCountStatus(p.weekendActivity) : null; },
      detail: function (p) { return p ? activityDetail(p.weekendActivity, 'weekendPosting', 'weekendApproval') : null; }
    },
    {
      title: '假日過帳／核准之分錄',
      desc: '總帳日期或傳票核准日落在國定假日的分錄，依第二步上傳的國定假日清單。',
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
    var v = state.results.validate;
    var p = state.results.prescreen;
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
            '<button type="button" class="btn btn--ghost" data-action="toggle-category" data-focus-key="account-settings" aria-expanded="' + categoryOpen + '">' +
              (categoryOpen ? '返回科目配對' : '分類設定') + '</button></div>' +
          // 配對方式的頁籤與內容包成一塊：和分類設定互換時整塊原地交叉淡化；換頁籤時只有內容交叉淡化，
          // 高度平順改變，下面的預篩選跟著移動，切換時不閃（app.js 的 JetMotion，裁定 2026-10-12-02）。
          (categoryOpen ? categoryCardHtml(state) :
            '<div class="account-methods-block" data-motion-key="account-methods">' +
            '<div class="account-methods" role="tablist" aria-label="科目配對方式">' +
              '<button type="button" data-action="select-account-jet" data-focus-key="account-method-jet"' +
                Ui.tabAttributes(!accountExcelOpen, 'account-method-panel', 'account-method-jet') + '>在 JET 配對</button>' +
              '<button type="button" data-action="toggle-account-excel" data-focus-key="account-method-excel"' +
                Ui.tabAttributes(accountExcelOpen, 'account-method-panel', 'account-method-excel') + '>用 Excel 配對</button>' +
            '</div><div role="tabpanel" id="account-method-panel" data-motion-key="account-method-' + (accountExcelOpen ? 'excel' : 'jet') +
              '" aria-labelledby="account-method-' + (accountExcelOpen ? 'excel' : 'jet') + '">' +
              (accountExcelOpen ? accountMappingCardHtml(state.importState.accountMapping, ready, state.project && state.project.projectId)
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
        card('GL 分錄數', stats ? Ui.countText(stats.glRowCount) : '—') +
        card('傳票數', stats ? Ui.countText(stats.voucherCount) : '—') +
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

  // 每條規則一列：標題與狀態，有明細時整列可點開。規則在做什麼不再常駐在標題下方：滑鼠停在標題上時出現，
  // 點開後寫在明細最上方一次，常駐說明不再多寫（裁定 2026-10-29-02）。
  // detailsBelow：並排的兩項彙總把明細接在整張清單下方、佔滿整寬，展開一邊時另一邊不會被撐高（裁定 2026-10-29-05：濫用置中會讓並排的區塊對不齊）。
  function ruleListHtml(items, runData, scope, options) {
    var view = detailView(detailRunKind(scope));
    var detailsBelow = !!(options && options.detailsBelow);
    var detailsAfterList = [];
    var rows = items.map(function (item, idx) {
      // status 為 null 表示這一列還沒有結果可顯示（尚未執行，或規則沒有回傳件數），這時不算 detail，
      // 避免 detail 去取缺漏的子物件而丟例外拖垮整步；詳情只在規則確有結果時才存在。
      var status = item.status(runData);
      var detail = (status && item.detail) ? item.detail(runData) : null;
      var expanded = !!view.expanded[scope + '-' + idx];
      var head =
        '<span class="rule-item__main">' +
          '<span class="rule-item__title"' + (item.desc ? ' title="' + Ui.esc(item.desc) + '"' : '') + '>' + Ui.esc(item.title) + '</span>' +
        '</span>' +
        '<span class="rule-item__aside">' + ruleBadge(status) + '</span>';
      // 有明細的項目整列都能點開，箭頭在字前面，不另外放「檢視」（裁定 2026-10-30-36：就地展開的檢視和打開資料預覽的要分得出來）；
      // 沒有明細的項目在箭頭的位置留同樣寬的空位，標題上下對齊。
      var headHtml = detail
        ? '<button type="button" class="rule-item__head row-toggle" data-action="toggle-detail" data-scope="' + Ui.esc(scope) +
            '" data-idx="' + idx + '"' + Ui.disclosureAttributes(expanded, 'rule-detail-' + scope + '-' + idx) + '>' + head + '</button>'
        : '<div class="rule-item__head">' + head + '</div>';
      var detailContainer = detail
        ? '<div class="rule-detail" id="' + Ui.esc('rule-detail-' + scope + '-' + idx) + '" data-bind="rule-detail-' + Ui.esc(scope) + '-' + idx + '" data-collapsible' + (expanded ? '' : ' hidden') + '>' +
            (detailsBelow ? '<h4 class="rule-detail__title">' + Ui.esc(item.title) + '</h4>' : '') +
            (item.desc ? '<p class="rule-detail__about">' + Ui.esc(item.desc) + '</p>' : '') +
            detailHtml(detail, scope + '-' + idx) +
          '</div>'
        : '';
      if (detailsBelow) { detailsAfterList.push(detailContainer); }
      return (
        '<li class="rule-item">' + headHtml + (detailsBelow ? '' : detailContainer) + '</li>'
      );
    }).join('');
    return '<ul class="rule-list">' + rows + '</ul>' +
      detailsAfterList.join('');
  }

  function lastRunLineHtml(runData) {
    if (!runData || !runData.resultRef) { return ''; }
    var time = new Date(runData.resultRef.generatedUtc);
    return '<p class="rule-card__meta">上次執行：' +
      time.toLocaleString('zh-Hant', { hour12: false }) + '</p>';
  }

  function validationReportsHtml(state, v) {
    if (!v || !v.resultRef || !v.resultRef.resultId) { return ''; }
    var reports = Ui.currentReports(
      state,
      ['validationReport', 'infReport'],
      { validationResultId: v.resultRef.resultId });
    var complete = reports.length === 2;
    return (
      '<div class="report-output" data-motion-key="validation-output">' +
        '<div class="report-output__head">' +
          '<div>' +
            '<h4 class="report-output__title">驗證報告與抽樣清單</h4>' +
          '</div>' +
          '<div class="report-output__actions">' + Ui.reportFolderButtonHtml(reports) +
            '<button type="button" class="btn btn--ghost" data-action="export-validation-reports">' +
            (complete ? '重新產生兩份報告' : '產生報告與抽樣清單') + '</button></div>' +
        '</div>' +
        validationOutputStatusHtml('reports') +
        Ui.reportListHtml(reports, '驗證已完成，報告尚未產生。', { motion: 'validation' }) +
      '</div>'
    );
  }

  function validationOutputStatusHtml(kind) {
    var state = Store.getState();
    var output = state.validationOutput[kind];
    var validation = state.results.validate;
    if (!output || (kind !== 'template' && (!validation || output.resultId !== validation.resultRef.resultId))) { return ''; }
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
    // 沒有原因時請審計員再試一次，不再說明後端沒有給原因（裁定 2026-10-14-01）。
    return '<p class="form-notice" data-account-template-status="failed" role="status">科目配對範本尚未準備好' +
      (output.reason ? '：' + Ui.esc(output.reason) : '，請再試一次。') + ' ' +
      '<button type="button" class="btn btn--ghost btn--tiny" data-action="retry-account-template-in-excel">' +
        '到「用 Excel 配對」重試</button></p>';
  }

  function validationCardHtml(v, ready, state) {
    return (
      '<section class="rule-card">' +
        '<div class="rule-card__head">' +
          '<h3 class="rule-card__title">資料驗證</h3>' +
          // 每個工作面只有一把藍色主要按鈕。已經有結果、結果也沒有過期時，重新執行改成白色次要按鈕，
          // 藍色留給現在該按的那一把（裁定 2026-11-05-22、2026-10-29-06）。
          '<button type="button" class="btn' + (v && !(state.staleState && state.staleState.validation) ? ' btn--ghost' : '') +
            '" data-action="run-validate"' + (ready ? '' : ' disabled') +
            '>' + (v ? '重新執行驗證' : '執行驗證') + '</button>' +
        '</div>' +
        lastRunLineHtml(v) +
        ruleListHtml(VALIDATION_ITEMS, v, 'v') +
        validationReportsHtml(state, v) +
      '</section>'
    );
  }

  // 科目配對匯入區塊：方法論上排在「資料驗證」（含完整性測試）之後、「風險預篩選」之前。
  // 預篩選的「未預期出現之特定借貸組合」依賴科目配對，因此先提供匯入入口。
  //
  // 匯入和重新匯入鈕只看 GL 欄位配對是否已確認(mappingReady)，不需要先執行資料驗證。
  // 完整性有差異仍可篩選；只有缺少、過期或損壞的驗證結果才要求重新驗證，不要求差異歸零。
  // 只要已匯入（含重開案件），就一律顯示已儲存的科目數與「資料預覽」。
  // 前端只做操作引導，未預期借貸組合是否能執行仍由後端預篩選判定。
  // 分類留白的科目在投影時已視為 Others（同 legacy），篩選不受影響；這裡只讓審計員看到哪些科目沒填，
  // 想改分類就到範本 C 欄填好再重新匯入。清單走 query.accountMappingBlankPage 的有界分頁。
  // 整列點開的清單和下面兩份差異清單一樣記在 mappingDifferenceView：整段重繪後照原樣打開，
  // 已讀的列從同一份分頁狀態接回，不會一重繪就收起來（裁定 2026-10-13-11）。
  // 單位和下面兩份清單一樣寫「個科目」：分類留白數的是配對檔裡沒填分類的科目。
  function blankCategoryRowHtml(info) {
    if (!info || !(info.blankCategoryCount > 0)) { return ''; }
    var open = !!mappingDifferenceView(info).expanded.blank;
    return '<li data-bind="blank-category-notice">' +
        '<button type="button" class="excel-mapping__result row-toggle" data-action="list-blank-categories"' +
          Ui.disclosureAttributes(open, 'blank-category-list') + '>' +
          '<span class="excel-mapping__label">分類留白，視為 Others</span>' +
          '<span class="excel-mapping__value">' + Ui.countText(info.blankCategoryCount) + ' 個科目</span></button>' +
        '<div class="excel-mapping__detail" id="blank-category-list" data-bind="blank-category-list" data-motion-key="blank-category-list" data-collapsible' +
          (open ? '' : ' hidden') + '>' +
          '<p class="excel-mapping__note">可在「在 JET 配對」整批調整分類，或修改科目配對範本後重新匯入。</p>' +
          '<div data-bind="blank-category-rows-host"></div>' +
        '</div>' +
      '</li>';
  }

  // 清單晚到的第一頁走 JetMotion：清單從「正在讀取資料…」的高度沿彈簧長到列出來的高度，不在展開後才一下跳開
  // （裁定 2026-10-13-11）。清單本身有 data-motion-key。整段重繪時接回已讀的列不走這裡，
  // 那時重繪本身就在比對（容器帶 data-motion-rendering）。
  // 清單還在展開的高度轉場中（剛點開清單，本機第一頁常在轉場結束前就回來，或重新打開時同步接回已讀的列）
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

  // 重開案件時兩份清單先寫「尚未核對」，不自動重掃 GL；點開清單才取得本次的科目數。
  function mappingDifferenceCountText(count) {
    return count == null ? '尚未核對' : Ui.countText(count) + ' 個科目';
  }

  function setMappingDifferenceCount(node, count) {
    node.textContent = mappingDifferenceCountText(count);
    node.classList.toggle('excel-mapping__value--muted', count == null);
  }

  function mappingDifferenceRowsHtml(info) {
    if (!info) { return ''; }
    var view = mappingDifferenceView(info);
    // 說明過多（裁定 2026-10-10-02），兩份清單只留名稱與筆數，不再附說明句。
    return ['mappingOnly', 'unmapped'].map(function (kind) {
      var count = view.counts[kind];
      return '<li>' +
          '<button type="button" class="excel-mapping__result row-toggle" data-mapping-difference="' + kind + '"' +
            Ui.disclosureAttributes(!!view.expanded[kind], 'difference-list-' + kind) + '>' +
            '<span class="excel-mapping__label">' + (kind === 'mappingOnly' ? '本案沒有的科目' : '配對檔未列的科目') + '</span>' +
            '<span class="excel-mapping__value' + (count == null ? ' excel-mapping__value--muted' : '') +
              '" data-difference-count="' + kind + '">' + mappingDifferenceCountText(count) + '</span></button>' +
          '<div class="excel-mapping__detail" id="difference-list-' + kind + '" data-difference-list="' + kind +
            '" data-motion-key="difference-list-' + kind + '" data-collapsible' + (view.expanded[kind] ? '' : ' hidden') + '></div>' +
        '</li>';
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
                if (view.countNodes[kind]) { setMappingDifferenceCount(view.countNodes[kind], data.totalCount); }
              }
              return data;
            });
          },
          appendRows: function (items) {
            function fill() {
              rows.insertAdjacentHTML('beforeend', items.map(function (row) {
                return '<li>' + Ui.esc(row.accountNumber || '（空白科目編號）') + (row.accountName ? '　' + Ui.esc(row.accountName) : '') + '</li>';
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

  // 「用 Excel 配對」是一個托盤：上面一排操作照做事的順序排，下面一張白紙列出匯入的結果（裁定 2026-10-30-35：
  // 採方案丙，參考方案乙增加設計感）。主要的藍色按鈕只在還沒匯入時給「匯入科目配對檔」；會清空分類的「重建科目配對檔」收進「更多」。
  // 科目配對需要已整理的科目，不依賴驗證結果；GL 還沒確認配對時沒有科目可列，托盤只寫要先做的事。
  function accountMappingCardHtml(info, mappingReady, projectId) {
    if (!mappingReady) {
      return '<section class="excel-mapping" data-bind="account-mapping-card" tabindex="-1">' +
          '<p class="excel-mapping__gate">GL 尚未確認配對，請先按「確認配對」。</p>' +
        '</section>';
    }
    if (accountSheetPick && accountSheetPick.projectId !== projectId) { accountSheetPick = null; }
    var pick = accountSheetPick;
    // 選工作表時，藍色的主要按鈕交給「匯入這個工作表」，上面那顆暫時改成一般按鈕。
    var importPrimary = !info && !pick;
    var actions =
      '<div class="excel-mapping__actions">' +
        '<button type="button" class="btn btn--ghost" data-action="ensure-account-mapping-template">準備科目配對範本</button>' +
        '<button type="button" class="btn btn--ghost" data-action="open-account-mapping-folder">開啟案件資料夾</button>' +
        '<span class="excel-mapping__sep" aria-hidden="true"></span>' +
        '<button type="button" class="btn' + (importPrimary ? '' : ' btn--ghost') + '" data-action="import-account-mapping">' +
          (info ? '重新匯入科目配對範本' : '匯入科目配對範本') + '</button>' +
        '<span class="excel-mapping__spacer" aria-hidden="true"></span>' +
        '<span class="jet-menu">' +
          '<button type="button" class="btn btn--ghost jet-menu__toggle" aria-haspopup="menu" aria-expanded="false" ' +
            'aria-controls="account-mapping-more">更多</button>' +
          '<div class="jet-menu__items" id="account-mapping-more" role="menu" aria-label="更多科目配對範本操作" hidden>' +
            '<button type="button" class="jet-menu__item jet-menu__item--danger" role="menuitem" tabindex="-1" ' +
              'data-action="download-account-mapping-template">重建科目配對範本' +
              '<span class="jet-menu__item-note">重建會清空檔案內已填的分類。</span></button>' +
          '</div>' +
        '</span>' +
      '</div>';
    // 「也可匯入」照裁定 2026-11-01-06 改回原本的字。
    var hint = '<p class="excel-mapping__hint">在「Standardized Account Name*」欄填入分類後存檔，再' + (info ? '重新匯入' : '匯入') +
      '；也可匯入前一個案件的配對檔。' + (info ? '重新匯入會覆蓋目前全部配對。' : '') + '</p>';
    // 已匯入時一律可從右側資料預覽看已儲存的配對。「資料預覽」緊接在名稱後面，科目數和下面幾列一樣靠右對齊。
    var saved = '<li><div class="excel-mapping__result">' +
        '<span class="excel-mapping__label">已儲存的科目配對</span>' +
        (info
          ? '<button type="button" class="jet-view jet-view--preview" data-action="preview-account-mapping">資料預覽</button>' +
            '<span class="excel-mapping__value">' + Ui.countText(info.rowCount) + ' 個科目</span>'
          : '<span class="excel-mapping__value excel-mapping__value--muted">尚未匯入</span>') +
      '</div></li>';
    var sheetPick = pick
      ? '<div class="excel-mapping__sheet-pick" data-bind="account-mapping-sheet-pick" data-motion-key="account-mapping-sheet-pick">' +
          '<p class="excel-mapping__hint">「' + Ui.esc(pick.fileName) + '」有多個工作表，請選擇科目配對所在的工作表。</p>' +
          '<div class="excel-mapping__actions">' +
            '<label class="excel-mapping__sheet-label">工作表 <select data-account-mapping-sheet>' +
              pick.sheets.map(function (name) {
                return '<option value="' + Ui.esc(name) + '"' + (name === pick.sheetName ? ' selected' : '') + '>' + Ui.esc(name) + '</option>';
              }).join('') +
            '</select></label>' +
            '<button type="button" class="btn" data-action="import-account-mapping-sheet">匯入這個工作表</button>' +
            '<button type="button" class="btn btn--ghost" data-action="cancel-account-mapping-sheet">取消</button>' +
          '</div>' +
        '</div>'
      : '';
    return (
      '<section class="excel-mapping" data-bind="account-mapping-card" tabindex="-1">' +
        actions + sheetPick + hint + validationOutputStatusHtml('template') +
        '<ul class="excel-mapping__results" data-motion-key="account-mapping-results">' +
          saved + blankCategoryRowHtml(info) + mappingDifferenceRowsHtml(info) +
        '</ul>' +
      '</section>'
    );
  }

  /* ---- 科目分類編輯器 --------------------------------------------- */

  // 目前顯示用的分類清單：優先使用本地草稿，否則鏡射後端 snapshot。
  // 後端 revision 換版時丟棄草稿，避免把舊版覆寫回較新的分類設定。
  function categoryRows(state) {
    var snapshot = state.accountCategory;
    var revision = snapshot ? snapshot.revision : null;
    if (categoryDraft && categoryDraftRevision === revision) { return categoryDraft; }
    categoryDraft = null;
    if (categoryDraftRevision !== revision) { categoryRemoval = null; }
    categoryDraftRevision = revision;
    return Ui.accountCategories(state).map(function (category) {
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

  function startCategoryDraft(state) {
    if (!categoryDraft) {
      categoryDraft = categoryRows(state).map(function (row) { return Object.assign({}, row); });
      categoryDraftRevision = state.accountCategory ? state.accountCategory.revision : null;
    }
    return categoryDraft;
  }

  // 前端引導檢查：顯示名稱必填且不重複（不分大小寫）。長度與身分規則仍由系統端裁定。
  function categoryProblems(rows) {
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
      return (row.label || '').trim().length > Ui.CATEGORY_MAX_LABEL_LENGTH;
    })) {
      problems.push('顯示名稱最多 ' + Ui.CATEGORY_MAX_LABEL_LENGTH + ' 個字');
    }
    return problems;
  }

  function categoryCardHtml(state) {
    var rows = categoryRows(state);
    var problems = categoryProblems(rows);
    var dirty = !!categoryDraft;

    var tree = Ui.categoryTree({ accountCategory: { categories: rows } });
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
      return '<span class="category-row__tree" aria-hidden="true">' + cells +
        '<i class="' + (continues(depth) ? 'is-branch' : 'is-last') + '"></i></span>';
    }
    // 科目分類的上下層級與分類用途在第一個正式版隱藏（裁定 2026-11-03-27）：開關關著時不畫「進階設定」、分類用途與上層分類，
    // 分類設定只填名稱，和舊版 JET 的科目配對一樣；新增的分類沒有上層，用途是 others。程式保留，Ui.FEATURES.categoryHierarchy
    // 改成 true 就恢復。已有上下層的分類照樣畫出縮排與連接線，移除有下層的分類照樣先確認。
    var advancedAvailable = !!Ui.FEATURES.categoryHierarchy;
    var advanced = advancedAvailable && categoryAdvanced;
    var items = tree.map(function (row, index) {
      var advancedFields = '';
      if (advancedAvailable) {
        var roleOptions = Ui.ACCOUNT_CATEGORY_ROLES.map(function (role) {
          return '<option value="' + role.value + '"' +
            (row.semanticRole === role.value ? ' selected' : '') + '>' + role.label + '</option>';
        }).join('');
        var descendants = Ui.categorySelectionCategories({ accountCategory: { categories: rows } }, [row.categoryId], 'subtree');
        advancedFields =
          '<div class="category-row__field category-row__advanced" data-motion="fade" data-motion-key="' + Ui.esc('category-role-' + row.rowId) + '"' + (advanced ? '' : ' hidden') + '><label for="category-role-' + index + '">分類用途</label>' +
          '<select id="category-role-' + index + '" aria-describedby="category-role-help" data-category-role="' + Ui.esc(row.rowId) + '"' +
            ' data-focus-key="category-role-' + Ui.esc(row.rowId) + '"' +
            (row.isBuiltIn ? ' disabled aria-disabled="true"' : '') + '>' + roleOptions + '</select></div>' +
          '<label class="category-row__field category-row__advanced" data-motion="fade" data-motion-key="' + Ui.esc('category-parent-' + row.rowId) + '"' + (advanced ? '' : ' hidden') + '>上層分類<select aria-describedby="category-parent-help" data-category-parent="' + Ui.esc(row.rowId) + '" data-focus-key="category-parent-' + Ui.esc(row.rowId) + '"><option value="">無（最上層）</option>' +
            tree.filter(function (candidate) { return !descendants.some(function (descendant) { return descendant.categoryId === candidate.categoryId; }); }).map(function (candidate) {
              return '<option value="' + Ui.esc(candidate.categoryId) + '"' + (row.parentCategoryId === candidate.categoryId ? ' selected' : '') + '>' +
                Ui.esc('　'.repeat(candidate.depth) + (candidate.depth ? '└ ' : '') + (candidate.label || '尚未命名的分類')) + '</option>';
            }).join('') + '</select></label>';
      }
      return '<li class="category-row' + (row.isBuiltIn ? ' is-builtin' : '') + '" data-motion-key="' + Ui.esc('category-' + row.rowId) + '" data-category-depth="' + row.depth +
        '" style="--category-depth:' + row.depth + '">' + treeGuidesHtml(index) +
        '<div class="category-row__field"><label for="category-label-' + index + '">分類名稱</label>' +
        '<input class="form__input category-row__label" type="text" id="category-label-' + index + '" aria-describedby="category-name-help"' +
          ' data-category-label="' + Ui.esc(row.rowId) + '" data-focus-key="category-label-' + Ui.esc(row.rowId) + '" value="' + Ui.esc(row.label) +
          '" maxlength="' + Ui.CATEGORY_MAX_LABEL_LENGTH + '"></div>' +
        advancedFields +
        (row.isBuiltIn ? '<span class="category-row__badge" title="JET 內建的分類，可以改名，不能移除">內建</span>' : '') +
        '<div class="category-row__actions">' +
        (row.isBuiltIn ? '' : '<button type="button" class="btn btn--ghost btn--tiny" data-category-remove="' + Ui.esc(row.rowId) + '">移除分類</button>') + '</div>' +
        '</li>';
    }).join('');

    return (
      '<section class="account-classification' + (advanced ? ' is-advanced' : '') + '" data-bind="account-category-card" data-motion-key="account-category">' +
        '<div class="rule-card__head"><h4 class="rule-card__title">分類設定</h4>' +
        (advancedAvailable ? '<button type="button" class="btn btn--ghost btn--tiny jet-disclosure" data-action="toggle-category-advanced" data-focus-key="category-advanced"' +
          Ui.disclosureAttributes(categoryAdvanced, 'category-guide') + '>進階設定</button>' : '') + '</div>' +
        // 開關關著時說明只留分類名稱這一句，給名稱欄的讀屏說明用，畫面上不打開。
        '<dl class="category-guide" id="category-guide" data-motion-key="category-guide"' + (advanced ? '' : ' hidden') + '>' +
          '<div><dt>分類名稱</dt><dd id="category-name-help">用於配對檔與篩選條件，可依案件命名。</dd></div>' +
          (advancedAvailable ?
            '<div><dt>分類用途</dt><dd id="category-role-help">自訂分類需要參與收入與現金等借貸組合檢查時才設定。Cash 已對應現金，無須另設。</dd></div>' +
            '<div><dt>上層分類</dt><dd id="category-parent-help">需要將多個分類一起篩選時，可歸入同一上層。新增下層分類時，分類用途預設沿用上層，之後可以自行修改。</dd></div>' : '') +
        '</dl>' +
        categoryRemovalHtml(rows) +
        '<ul class="category-list" data-preserve-scroll="category-list" data-motion-list="category">' + items + '</ul>' +
        '<p class="form-notice" data-bind="category-problems" data-collapsible' + (problems.length ? '' : ' hidden') + '>' +
          (problems.length ? '尚需補齊：' + problems.map(Ui.esc).join('、') : '') + '</p>' +
        '<div class="import-card__actions">' +
          '<button type="button" class="btn btn--ghost btn--add" data-action="add-category" data-focus-key="category-add">新增分類</button>' +
          '<button type="button" class="btn" data-action="save-category"' +
            (dirty && problems.length === 0 ? '' : ' disabled') + '>儲存分類設定</button>' +
          '<button type="button" class="btn btn--ghost" data-action="reset-category" data-collapsible-inline' +
            (dirty ? '' : ' hidden') + '>取消變更</button>' +
        '</div>' +
        '<p class="rule-card__gate" data-bind="category-dirty-notice" data-collapsible' + (dirty ? '' : ' hidden') +
          '>分類設定尚未儲存。</p>' +
      '</section>'
    );
  }

  function categoryRemovalHtml(rows) {
    var removed = categoryRemoval && rows.find(function (row) { return row.rowId === categoryRemoval; });
    if (!removed) { return ''; }
    var children = rows.filter(function (row) { return row.parentCategoryId === removed.categoryId; });
    var parent = rows.find(function (row) { return row.categoryId === removed.parentCategoryId; });
    // 確認後果而不是錯誤，用「需要注意」的琥珀色（裁定 2026-10-09-03）。
    return '<div class="form-notice form-notice--warning" data-motion-key="category-removal" role="alert">移除「' + Ui.esc(removed.label) + '」後，它下面的「' +
      children.map(function (row) { return Ui.esc(row.label || '尚未命名的分類'); }).join('、') + '」會改放到' +
      (parent ? '「' + Ui.esc(parent.label) + '」底下' : '最上層') + '。' +
      '<button type="button" class="btn btn--ghost btn--tiny" data-action="confirm-category-remove">確認移除分類</button>' +
      '<button type="button" class="btn btn--ghost btn--tiny" data-action="cancel-category-remove">先不移除</button></div>';
  }

  function removeCategoryRow(rowId) {
    var rows = startCategoryDraft(Store.getState());
    var removed = rows.find(function (row) { return row.rowId === rowId; });
    if (!removed) { return; }
    categoryDraft = rows.filter(function (row) { return row !== removed; }).map(function (row) {
      return row.parentCategoryId === removed.categoryId
        ? Object.assign({}, row, { parentCategoryId: removed.parentCategoryId || null }) : row;
    });
    categoryRemoval = null; Store.touch();
  }

  function bindCategoryCard(container) {
    var card = container.querySelector('[data-bind="account-category-card"]');
    if (!card) { return; }

    card.querySelectorAll('[data-category-label]').forEach(function (input) {
      input.addEventListener('input', function () {
        var rowId = input.getAttribute('data-category-label');
        var rows = startCategoryDraft(Store.getState());
        rows.forEach(function (row) {
          if (row.rowId === rowId) { row.label = input.value; }
        });
        // 草稿只存在此編輯器；就地更新衍生提示，不換掉正在組字或移動游標的 input。
        // 保存、取消、新增與移除仍走完整重繪，保存前也會重新檢查同一份草稿。
        // 提示與「取消變更」原地收放（data-collapsible、data-collapsible-inline）；收起時保留原來的字，
        // 才看得到它收回去，不會先變成空白再消失（裁定 2026-10-13-11）。
        var problems = categoryProblems(rows);
        var notice = card.querySelector('[data-bind="category-problems"]');
        if (problems.length) { notice.textContent = '尚需補齊：' + problems.join('、'); }
        notice.hidden = problems.length === 0;
        card.querySelector('[data-action="save-category"]').disabled = problems.length > 0;
        card.querySelector('[data-action="reset-category"]').hidden = false;
        card.querySelector('[data-bind="category-dirty-notice"]').hidden = false;
      });
    });

    card.querySelectorAll('[data-category-role]').forEach(function (select) {
      select.addEventListener('change', function () {
        var rowId = select.getAttribute('data-category-role');
        var rows = startCategoryDraft(Store.getState());
        rows.forEach(function (row) {
          if (row.rowId === rowId && !row.isBuiltIn) { row.semanticRole = select.value; row.roleExplicit = true; }
        });
        Store.touch();
      });
    });

    card.querySelectorAll('[data-category-parent]').forEach(function (select) {
      select.addEventListener('change', function () {
        var rows = startCategoryDraft(Store.getState());
        var row = rows.find(function (item) { return item.rowId === select.getAttribute('data-category-parent'); });
        if (row) {
          row.parentCategoryId = select.value || null;
          var parent = rows.find(function (item) { return item.categoryId === row.parentCategoryId; });
          if (row.isNew && !row.roleExplicit) { row.semanticRole = parent ? parent.semanticRole : 'others'; }
          Store.touch();
        }
      });
    });

    card.querySelectorAll('[data-category-remove]').forEach(function (button) {
      button.addEventListener('click', function () {
        var rowId = button.getAttribute('data-category-remove');
        var rows = categoryRows(Store.getState());
        var removed = rows.find(function (row) { return row.rowId === rowId; });
        if (removed && rows.some(function (row) { return row.parentCategoryId === removed.categoryId; })) {
          categoryRemoval = rowId; Store.touch();
        } else { removeCategoryRow(rowId); }
      });
    });
    var confirmRemove = card.querySelector('[data-action="confirm-category-remove"]');
    if (confirmRemove) {
      confirmRemove.addEventListener('click', function () {
        // 確認移除後，那一列先輕微快速地晃一下再收起來，讓使用者知道哪一個分類被拿掉（裁定 2026-10-13-13）。
        // 確認提示本身只收起來、不晃；直接按「移除分類」（沒有下層）的路徑由 app.js 統一處理。
        if (categoryRemoval && global.JetMotion && typeof global.JetMotion.removing === 'function') {
          global.JetMotion.removing({ keys: ['category-' + categoryRemoval] });
        }
        removeCategoryRow(categoryRemoval);
      });
    }
    var cancelRemove = card.querySelector('[data-action="cancel-category-remove"]');
    if (cancelRemove) { cancelRemove.addEventListener('click', function () { categoryRemoval = null; Store.touch(); }); }

    var addBtn = card.querySelector('[data-action="add-category"]');
    if (addBtn) {
      addBtn.addEventListener('click', function () {
        var rows = startCategoryDraft(Store.getState());
        categoryDraftSeq++;
        rows.push({
          rowId: 'draft-' + categoryDraftSeq,
          categoryId: 'draft-' + categoryDraftSeq,
          label: '',
          semanticRole: 'others',
          isNew: true,
          parentCategoryId: null,
          isBuiltIn: false
        });
        Store.touch();
        var rowId = 'draft-' + categoryDraftSeq;
        if (global.JetFocus) { global.JetFocus.defer(function () {
          var target = document.querySelector('[data-category-label="' + rowId + '"]');
          if (target) { target.scrollIntoView({ block: 'nearest', inline: 'nearest' }); }
          return target;
        }); }
      });
    }

    var resetBtn = card.querySelector('[data-action="reset-category"]');
    if (resetBtn) {
      resetBtn.addEventListener('click', function () {
        categoryDraft = null;
        categoryRemoval = null;
        Store.touch();
      });
    }

    var saveBtn = card.querySelector('[data-action="save-category"]');
    if (saveBtn) {
      saveBtn.addEventListener('click', function () {
        var state = Store.getState();
        var rows = categoryRows(state);
        if (categoryProblems(rows).length) { return; }
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
          return global.JetApi.accountCategorySave({
            revision: state.accountCategory ? state.accountCategory.revision : 1,
            categories: categories
          }).then(function (data) {
            categoryDraft = null;
            categoryRemoval = null;
            categoryDraftRevision = data.revision;
            // 儲存後新分類換成正式 ID，畫面上還是同一列；直接換，不當成移除再加入。
            var apply = function () { Store.setCategoryAfterSave(data); Store.applyMutationEffects(data); };
            if (global.JetMotion) { global.JetMotion.still(apply); } else { apply(); }
            return data;
          });
        }, { completionMessage: function (data) {
          // 完成句由 Ui.run 接上耗時後記一則（裁定 2026-10-13-17：成功時只留一則）。
          // 這句的用字依裁定 2026-10-13-28。
          return data ? '科目分類已更新，共 ' + Ui.countText(data.categories.length) + ' 類；預篩選要重新執行。' : '';
        } });
      });
    }
  }

  function exportValidationReports(runData) {
    var resultId = runData && runData.resultRef ? runData.resultRef.resultId : null;
    if (!resultId) { return Promise.reject(new Error('找不到可匯出的驗證結果。')); }
    return performValidationOutput('reports', resultId, function () {
      return global.JetApi.exportValidationReports({ resultId: resultId });
    }, function (data) {
      Store.applyReportExport(data);
      return '驗證報告與抽樣清單已產生，可從下方清單查看。';
    });
  }

  // 單獨產生驗證報告時，這句是整個操作的完成句；和執行驗證一起產生時，是驗證完成前的一則紀錄。
  function validationReportsMessage(data) {
    return data ? '已在案件資料夾產生驗證階段的 ' + (data.publishedReports || []).length + ' 份報告。' : '';
  }

  function requireValidationOutputActive(projectId, resultId, requiresValidation) {
    var state = Store.getState();
    var validation = state.results.validate;
    if (state.cancellationRequested || !state.project || state.project.projectId !== projectId
        || (requiresValidation && (!validation || validation.resultRef.resultId !== resultId))) {
      var error = new Error('後續檔案產生已取消，已完成的驗證與檔案會保留。');
      error.code = 'operation_cancelled';
      error.completedResultsPreserved = true;
      throw error;
    }
  }

  function performValidationOutput(kind, resultId, action, onSuccess) {
    var start = Store.getState(), project = start.project, generation = start.dataGeneration;
    var projectId = project.projectId;
    requireValidationOutputActive(projectId, resultId, kind !== 'template');
    var pending = { resultId: resultId, status: 'pending', message: kind === 'template' ? '正在準備科目配對範本…' : '正在產生報告與抽樣清單…' };
    Store.setValidationOutput(kind, pending);
    function active() {
      var current = Store.getState(), run = current.results.validate;
      return current.project === project && current.dataGeneration === generation && current.validationOutput[kind] === pending &&
        (kind === 'template' || run && run.resultRef && run.resultRef.resultId === resultId);
    }
    return action().then(function (data) {
      // 已完成的 action 結果照實顯示；取消只影響下一個尚未開始的 action。
      if (!active()) { return null; }
      Store.setValidationOutput(kind, { resultId: resultId, status: 'ready', message: onSuccess(data) });
      return data;
    }).catch(function (error) {
      if (active()) {
        Store.setValidationOutput(kind, { resultId: resultId, status: 'failed',
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
    var resultId = runData && runData.resultRef ? runData.resultRef.resultId : null;
    return performValidationOutput('template', resultId, function () {
      return global.JetApi.exportAccountMappingTemplate({ resultId: resultId, onlyIfMissing: onlyIfMissing });
    }, function (data) {
      return data.disposition === 'kept'
        ? '已保留案件資料夾的 ' + data.fileName + '，不改動其中已填寫的分類。'
        : '已建立 ' + data.fileName + '（' + Ui.countText(data.rowCount) + ' 個科目）。填妥科目分類並存檔後，即可匯入。';
    });
  }

  function exportValidationOutputs(runData) {
    var start = Store.getState(), project = start.project, generation = start.dataGeneration, failures = [];
    function active() {
      var current = Store.getState();
      return current.project === project && current.dataGeneration === generation && current.results.validate === runData;
    }
    function preserveFailure(error) {
      // 走到這裡時資料驗證已完成並保存；產生報告時被取消，訊息也要說已完成的部分仍保留。
      if (error && error.code === 'operation_cancelled') {
        error.completedResultsPreserved = true;
        throw error;
      }
      failures.push(error);
    }
    return exportValidationReports(runData).then(function (data) {
      var sentence = validationReportsMessage(data);
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
    var resultId = runData && runData.resultRef ? runData.resultRef.resultId : null;
    if (!resultId) { return Promise.reject(new Error('找不到可匯出的預篩選結果。')); }
    var state = Store.getState(), project = state.project, generation = state.dataGeneration;
    function active() {
      var current = Store.getState(), run = current.results.prescreen;
      return current.project === project && current.dataGeneration === generation && run && run.resultRef && run.resultRef.resultId === resultId;
    }
    return global.JetApi.exportPrescreenReport({ resultId: resultId }).then(function (data) {
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
    // 「更多」是共用選單，開關與鍵盤由 Ui.bindMenus 處理，這裡只處理按下「重建科目配對檔」。
    var card = container.querySelector('[data-bind="account-mapping-card"]');
    if (card) { Ui.bindMenus(card); }
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
        // 說明句留在清單上方，列與分頁放進下面的容器。
        var rowsHost = blankList.querySelector('[data-bind="blank-category-rows-host"]');
        rowsHost.innerHTML = '<ul class="kv-list" data-bind="blank-category-rows"></ul>';
        var rowsEl = rowsHost.querySelector('[data-bind="blank-category-rows"]');
        var listed = 0;
        // 掛上時從 blankView.pages.blank 同步接回的已讀列直接放進去：這時清單剛取消 hidden，
        // 走 JetMotion 會在列放進去前先量高度，展開區反而朝空清單的高度長。只有晚到的第一頁才走 JetMotion。
        var mounting = true;
        function appendRows(rows) {
          function fill() {
            rowsEl.insertAdjacentHTML('beforeend', rows.map(function (row) {
              return '<li>' + Ui.esc(row.accountNumber) + (row.accountName ? '　' + Ui.esc(row.accountName) : '') + '</li>';
            }).join(''));
          }
          if (listed === 0 && !mounting) { morphCheckList(blankList, fill); } else { fill(); }
          listed += rows.length;
        }
        try {
          Ui.bindPagedTable(rowsHost, {
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
        Ui.openDataPreview('accountMapping');
      });
    }

    // 後端以目前科目產生工作檔；不以驗證結果限制使用者準備或重建配對檔。
    var templateBtn = container.querySelector('[data-action="download-account-mapping-template"]');
    if (templateBtn) {
      templateBtn.addEventListener('click', function () {
        // 操作名稱照按鈕（裁定 2026-10-13-28）；失敗原因寫在區塊內的狀態，不另外在步驟上方顯示。
        Ui.run('重建科目配對範本', function () {
          return exportAccountMappingTemplate(Store.getState().results.validate, false);
        }, { logCompletion: true, inlineError: templateFailureShown });
      });
    }

    var ensureTemplateBtn = container.querySelector('[data-action="ensure-account-mapping-template"]');
    if (ensureTemplateBtn) {
      ensureTemplateBtn.addEventListener('click', function () {
        Ui.run('準備科目配對範本', function () {
          return exportAccountMappingTemplate(Store.getState().results.validate, true);
        }, { inlineError: templateFailureShown });
      });
    }

    var btn = container.querySelector('[data-action="import-account-mapping"]');
    if (!btn) { return; }

    btn.addEventListener('click', function () {
      // 匯入科目配對檔會取代第四步的科目分類；還有沒儲存的分類時，選檔前先問，沒有時照舊直接選檔。
      if (!Ui.pendingAccountCategoryCount()) { importAccountMappingFile(); return; }
      Ui.confirmPendingAccountCategories('匯入科目配對範本後，檔案內容會取代第四步的科目分類，這些改動不會保留。', '繼續匯入', { saveable: false })
        .then(function (proceed) { if (proceed) { importAccountMappingFile(); } });
    });

    var sheetSelect = container.querySelector('[data-account-mapping-sheet]');
    if (sheetSelect) {
      sheetSelect.addEventListener('change', function () {
        if (accountSheetPick) { accountSheetPick.sheetName = sheetSelect.value; }
      });
    }
    var sheetImport = container.querySelector('[data-action="import-account-mapping-sheet"]');
    if (sheetImport) {
      sheetImport.addEventListener('click', function () {
        var pick = accountSheetPick;
        if (!pick) { return; }
        Ui.run('匯入科目配對範本', function () {
          return importAccountMappingSource({ filePath: pick.filePath, fileName: pick.fileName, sheetName: pick.sheetName })
            .then(function (data) {
              // 匯入失敗時留著選擇，審計員可以改選工作表再試；成功後收起來。
              if (accountSheetPick === pick) { accountSheetPick = null; Store.touch(); }
              return data;
            });
        }, { completionMessage: accountMappingCompletion });
      });
    }
    var sheetCancel = container.querySelector('[data-action="cancel-account-mapping-sheet"]');
    if (sheetCancel) {
      sheetCancel.addEventListener('click', function () {
        accountSheetPick = null;
        Store.touch();
      });
    }

    // 完成句由 Ui.run 接上耗時後記一則；沒有選檔或改成先選工作表時不記（裁定 2026-10-13-17：成功時只留一則）。
    function accountMappingCompletion(data) {
      // 這句的用字依裁定 2026-10-13-28；沿用前一個案件的說明寫在托盤的提示句。
      return data ? '科目配對匯入完成，共 ' + Ui.countText(data.rowCount) + ' 個科目。' : '';
    }

    // 選了 Excel 檔時先看有幾個有欄名的工作表：只有一個就直接匯入它；有多個時在托盤列出來請審計員選，
    // 有工作底稿的「自動化工具-科目配對資訊」時預先選它（裁定 2026-11-05-07）。
    // JET 匯出的報告都有一張隱藏的中繼資料工作表，審計員在 Excel 看不到，不列進選單。
    // JET 自己的科目配對範本另有一張分類清單，照舊直接匯入範本那一張，不多問一次；原本只要有兩張以上就問，
    // 提交前複審發現按「準備科目配對範本」再匯入回來這條主要路徑因此多了一步。
    function importAccountMappingFile() {
      var projectId = Store.getState().project && Store.getState().project.projectId;
      Ui.run('匯入科目配對範本', function () {
        return global.JetApi.hostSelectFile({
          title: '選擇科目配對範本或工作底稿（科目編號、科目名稱、科目分類）',
          extensions: ['.xlsx', '.csv']
        }).then(function (file) {
          if (!file.filePath) { return; }
          accountSheetPick = null;
          if (!/\.xlsx$/i.test(file.filePath)) {
            return importAccountMappingSource({ filePath: file.filePath, fileName: file.fileName });
          }
          return global.JetApi.importInspectFile({ filePath: file.filePath }).then(function (inspected) {
            var visible = (inspected.worksheets || []).filter(function (sheet) { return sheet.name !== REPORT_METADATA_SHEET; });
            var names = visible.map(function (sheet) { return sheet.name; });
            var sheets = visible.filter(function (sheet) { return sheet.columns.length > 0; }).map(function (sheet) { return sheet.name; });
            // 範本是依工作表名稱認的，不看第一列有沒有字：範本的欄名不在第一列。
            var template = names.indexOf(ACCOUNT_MAPPING_TEMPLATE_SHEET) >= 0 && names.indexOf(WORKING_PAPER_ACCOUNT_MAPPING_SHEET) < 0;
            if (template || sheets.length <= 1) {
              return importAccountMappingSource({ filePath: file.filePath, fileName: file.fileName, sheetName: template ? ACCOUNT_MAPPING_TEMPLATE_SHEET : sheets[0] });
            }
            accountSheetPick = {
              projectId: projectId, filePath: file.filePath, fileName: file.fileName, sheets: sheets,
              sheetName: sheets.indexOf(WORKING_PAPER_ACCOUNT_MAPPING_SHEET) >= 0 ? WORKING_PAPER_ACCOUNT_MAPPING_SHEET : sheets[0]
            };
            Store.touch();
            return null;
          });
        });
      }, { completionMessage: accountMappingCompletion });
    }

    function importAccountMappingSource(source) {
      return global.JetApi.importAccountMappingFromFile(source).then(function (data) {
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
        // 文字檔欄位內含換行的提醒，和 GL、TB 匯入同一句、同一種顯示方式（裁定 2026-10-19-06）。
        (data.warnings || []).forEach(function (warning) { Store.addMessage(warning, 'warn'); });
        Store.applyMutationEffects(data);
        var differences = mappingDifferenceView(Store.getState().importState.accountMapping);
        differences.counts = { mappingOnly: data.mappingOnlyCount, unmapped: data.unmappedCount };
        Store.touch();
        return data;
      });
    }
  }

  // p = prescreen.run 結果（驅動 PRESCREEN_ITEMS）；v = validate.run 結果（驅動移入的空值兩子項）。
  // 空值兩子項雖列在此卡，資料仍來自 validate.run，故獨立用 scope 'pn' 渲染、傳入 v（不與 'p' 混用）。
  function prescreenReportHtml(state, p, positioning) {
    if (!p || !p.resultRef || !p.resultRef.resultId) { return ''; }
    var reports = Ui.currentReports(
      state, ['prescreenReport'], { prescreenResultId: p.resultRef.resultId });
    return (
      '<div class="report-output" data-motion-key="prescreen-output">' +
        '<div class="report-output__head">' +
          '<div><h4 class="report-output__title">預篩選報告</h4></div>' +
          '<div class="report-output__actions">' + Ui.reportFolderButtonHtml(reports) +
            '<button type="button" class="btn btn--ghost" data-action="export-prescreen-report">' +
            (reports.length ? '重新產生報告' : '產生報告') + '</button></div>' +
        '</div>' +
        Ui.reportListHtml(reports, '預篩選已完成，報告尚未產生。', { motion: 'prescreen' }) +
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
          // 和資料驗證同一個做法：已經有結果、結果也沒有過期時，重新執行預篩選是白色次要按鈕（裁定 2026-11-05-22）。
          '<button type="button" class="btn' + (p && !(state.staleState && state.staleState.prescreen) ? ' btn--ghost' : '') +
            '" data-action="run-prescreen"' + (canRun ? '' : ' disabled') +
            '>' + (p ? '重新執行預篩選' : '執行預篩選') + '</button>' +
        '</div>' +
        gate +
        lastRunLineHtml(p) +
        '<section class="prescreen-primary" aria-labelledby="prescreen-primary-title">' +
          ruleListHtml(PRESCREEN_AGGREGATE_ITEMS, p, 'pa', { detailsBelow: true }) +
        '</section>' +
        '<details class="prescreen-signals"' + (prescreenSignalsOpen ? ' open' : '') + '>' +
          '<summary class="prescreen-signals__summary">' +
            '<span>篩選結果</span><span class="prescreen-signals__hint">展開查看</span>' +
          '</summary>' +
          '<div class="prescreen-signals__content">' +
            '<p class="prescreen-signals__guidance">' + Ui.esc(positioning.signalGuidance) + '</p>' +
            ruleListHtml(PRESCREEN_SIGNAL_ITEMS, p, 'ps') +
            '<h5 class="prescreen-signals__secondary-title">資料缺漏輔助檢視</h5>' +
            ruleListHtml(BLANK_PRESCREEN_ITEMS, v, 'pn') +
          '</div>' +
        '</details>' +
        prescreenReportHtml(state, p, positioning) +
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
    [['toggle-category', function () { categoryOpen = !categoryOpen; }],
      ['toggle-category-advanced', function () { categoryAdvanced = !categoryAdvanced; }],
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
    Ui.bindReports(container);
    Ui.bindStepFooter(container);
    var signals = container.querySelector('.prescreen-signals');
    if (signals) { signals.addEventListener('toggle', function () { prescreenSignalsOpen = signals.open; }); }

    bindCategoryCard(container);
    bindAccountMappingCard(container);

    var validationExportBtn = container.querySelector('[data-action="export-validation-reports"]');
    if (validationExportBtn) {
      validationExportBtn.addEventListener('click', function () {
        Ui.run('產生驗證階段報告', function () {
          return exportValidationReports(Store.getState().results.validate);
        }, { completionMessage: validationReportsMessage, inlineError: reportsFailureShown });
      });
    }

    var prescreenExportBtn = container.querySelector('[data-action="export-prescreen-report"]');
    if (prescreenExportBtn) {
      prescreenExportBtn.addEventListener('click', function () {
        Ui.run('產生預篩選報告', function () {
          return exportPrescreenReport(Store.getState().results.prescreen);
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

        // 從當前 store 狀態取 resultId，作為快取鍵的一部分。
        var state = Store.getState();
        var presRun = state.results.prescreen;
        var resultId = presRun && presRun.resultRef ? presRun.resultRef.resultId : null;

        var validationResult = state.results.validate;
        var validationResultId = validationResult && validationResult.resultRef
          ? validationResult.resultRef.resultId : null;

        panel.querySelectorAll('[data-dynamic-page]').forEach(function (block) {
          var pageKey = block.getAttribute('data-dynamic-page');
          var spec = DYNAMIC_PAGE_SPECS[pageKey];
          if (!spec) { return; }
          var cacheKey = (validationResultId || 'unknown') + '|' + pageKey;
          var body = block.querySelector('.rule-detail__preview-body');

          loadDetailPreview(body, cacheKey, 'validate', validationResult,
            function () { return spec.fetchPage(null); },
            function (data) { renderDynamicPage(body, data, spec); });
        });

        panel.querySelectorAll('[data-prescreen-key]').forEach(function (block) {
          var key = block.getAttribute('data-prescreen-key');
          var cacheKey = (resultId || 'unknown') + '|' + key;

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
          Store.setResult('validate', data);
          return exportValidationOutputs(data);
        });
      }, { completionMessage: function (data) {
        // 驗證結果的完成句由 Ui.run 接上整個操作的耗時後記一則（裁定 2026-10-13-17：成功時只留一則）；
        // 報告與範本是這個操作的一部分，各自的紀錄照舊。
        // 各項筆數已寫在下方各項結果，完成句只留一句（裁定 2026-10-13-28）。
        return data ? '資料驗證完成' : '';
      } });
    });

    container.querySelector('[data-action="run-prescreen"]').addEventListener('click', function () {
      var eligibility = Ui.completenessEligibility(Store.getState().results.validate);
      if (!eligibility.isEligible) {
        // 這句的用字依裁定 2026-10-13-28。
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
          Store.setResult('prescreen', data);
          return data;
        });
      }, { completionMessage: function (data) {
        // 完成句由 Ui.run 接上耗時後記一則（裁定 2026-10-13-17：成功時只留一則）。
        // 各項筆數已寫在下方各項結果，完成句只留一句（裁定 2026-10-13-28）。
        return data ? '預篩選已更新' : '';
      } });
    }
  }

  Ui.registerStep('validate', render);
})(window);
