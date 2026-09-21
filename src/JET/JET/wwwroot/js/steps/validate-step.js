/*
  Step 3：資料驗證與測試（四項資料驗證 + 預篩選合併步驟）。
  規則名稱對齊 guide §4 命名登錄表（V/R 代號已退役）；
  標題與說明只是 presentation，權威規則一律在後端 SQL。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;
  var Ui = global.JetUi;

  function naStatus(rule) {
    return { tone: 'na', text: '無法執行', tip: rule.naReason };
  }

  // 完整性測試的第一個檢查：匯入前後的筆數與借貸總額。後端 partA 缺漏時所有值為 null，不算不符。
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
    var n = Number(rule.count);
    return n > 0
      ? { tone: 'hit', text: n.toLocaleString() + ' 筆', tip: tip }
      : { tone: 'ok', text: '未發現', tip: tip };
  }

  // 過帳／核准成對計數（週末／假日規則）。approvalCount 為 null 表示核准日未配對。
  // rule 缺漏（resume 舊版結果）→「未執行」，理由同 hitCountStatus。
  function pairCountStatus(rule) {
    if (!rule) { return null; }
    if (rule.naReason) { return naStatus(rule); }
    var approval = rule.approvalCount == null ? '—' : Number(rule.approvalCount).toLocaleString();
    var any = Number(rule.postingCount) > 0 || Number(rule.approvalCount) > 0;
    return {
      tone: any ? 'hit' : 'ok',
      text: '過帳 ' + Number(rule.postingCount).toLocaleString() + '，核准 ' + approval,
      tip: rule.approvalCount == null ? '未配對「傳票核准日」欄位，無法檢查核准日期。' : undefined
    };
  }

  // 預篩選命中首頁快取：以 runId + '|' + prescreenKey 為鍵；完整明細仍由使用者以 keyset 逐頁載入。
  // 每次新的 run（新 runId）自然不命中快取，重新抓取。
  var prescreenPreviewCache = {};
  var detailPreviewProject = null;
  var detailPreviewGeneration = null;
  var detailPreviewRuns = {};

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
      display(data);
      body._jetDetailEntry = entry;
    }
    if (entry.data) { show(entry.data); return; }
    body.textContent = '正在讀取分錄…';
    entry.promise.then(function (data) {
      if (current() && body.isConnected) { show(data); }
    }).catch(function (error) {
      if (!current() || !body.isConnected) { return; }
      body.textContent = error && error.code === 'operation_cancelled'
        ? '讀取已取消；收合後再展開即可重試。'
        : '讀取分錄失敗；收合後再展開即可重試。' + (error && error.message ? ' ' + error.message : '');
    });
  }

  // 科目分類編輯草稿（純畫面狀態）。null = 尚未編輯，直接顯示後端 snapshot；
  // 一旦使用者改動就在本地保留，直到保存成功、取消或後端 revision 換版。
  var taxonomyDraft = null;
  var taxonomyDraftRevision = null;
  // 新增分類的本地識別（尚未取得後端 stable ID 前只用於 DOM 對位；絕不送進 payload）。
  var taxonomyDraftSeq = 0;

  Ui.registerWorkflowReset(function () {
    prescreenPreviewCache = {};
    dynamicPageCache = {};
    detailPreviewProject = null;
    detailPreviewGeneration = null;
    detailPreviewRuns = {};
    taxonomyDraft = null;
    taxonomyDraftRevision = null;
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

  // 預篩選命中表：首屏 50 筆之後由同一張表接「載入更多」、表頭排序與依傳票號碼查看；
  // 首擊或換排序時先清掉首屏列，再從資料庫端的第一頁接續。
  function bindPrescreenTable(block, key) {
    var tbody = block.querySelector('.preview-table tbody');
    var summary = block.querySelector('.rule-detail__preview-summary');
    if (!tbody) { return; }
    Ui.bindPagedTable(block, {
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryPrescreenPage({ ruleKey: key, cursor: cursor, pageSize: 200, sort: sort, search: search || null });
      },
      appendRows: function (rows) {
        Ui.appendRowsToTbody(tbody, rows, PRESCREEN_PAGE_COLUMNS);
        if (summary) { summary.textContent = '已載入 ' + tbody.rows.length.toLocaleString() + ' 筆命中分錄'; }
      },
      clearRows: function () { tbody.innerHTML = ''; },
      loadMore: block.querySelector('[data-prescreen-load-more]'),
      table: block.querySelector('.preview-table'),
      search: block.querySelector('[data-page-search]')
    });
  }

  function prescreenPageHtml(data) {
    var rows = (data && data.rows) || [];
    return (
      '<p class="rule-detail__preview-summary">已載入 ' + rows.length.toLocaleString() + ' 筆命中分錄</p>' +
      Ui.pageSearchHtml('依傳票號碼查看') +
      '<div class="preview-table__wrap">' + Ui.previewTableHtml(rows, 'query.prescreenPage') + '</div>' +
      '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more" data-prescreen-load-more' +
        (data && data.nextCursor != null ? '' : ' hidden') + '>載入更多</button>'
    );
  }

  // 詳情渲染輔助：把 detail 物件轉成 HTML 字串（放進 hidden 容器）。
  // prescreenPreview 類型回傳佔位 HTML；展開時觸發惰性抓取（見 bind()）。
  function detailHtml(detail) {
    if (!detail) { return ''; }
    if (detail.kind === 'reason') {
      return '<p class="rule-detail__reason">' + Ui.esc(detail.text) + '</p>' +
             '<p class="rule-detail__remedy">' + Ui.esc(detail.remedy) + '</p>';
    }
    if (detail.kind === 'table') {
      // 只有一顆「載入更多」的表才提供排序與搜尋（空值表兩顆鈕共用一個 tbody，混排兩個類別不能排序）。
      var lmIdsForHead = detail.loadMore == null ? [] :
        (Array.isArray(detail.loadMore) ? detail.loadMore : [detail.loadMore]);
      var sortable = !!detail.sortAction && lmIdsForHead.length === 1;
      var thead = '<tr>' + (sortable
        ? Ui.sortableHeadCellsHtml(detail.sortAction, detail.columns.map(function (c, index) {
            return { label: c, key: (detail.sortKeys || [])[index] || null };
          }))
        : detail.columns.map(function (c) { return '<th>' + Ui.esc(c) + '</th>'; }).join('')) + '</tr>';
      var searchHtml = sortable && detail.searchLabel ? Ui.pageSearchHtml(detail.searchLabel) : '';
      var tbody = detail.rows.map(function (row) {
        return '<tr>' + row.map(function (cell) {
          return '<td>' + Ui.esc(String(cell == null ? '' : cell)) + '</td>';
        }).join('') + '</tr>';
      }).join('');
      var prefix = detail.prefix ? '<p class="rule-detail__prefix">' + Ui.esc(detail.prefix) + '</p>' : '';
      // loadMore：把後端首屏 ≤50 預覽下方接「載入更多」鈕(keyset)。
      // detail.loadMore 可為單一 id 字串或 id 陣列(空值「傳票號／科目」一格 tbody 兩 category 各一鈕)。
      // tbody 標記 data-load-target(用第一個 id 對齊),每顆鈕標記 data-load-more（bind() 用 JetUi.bindLoadMore 綁定）。
      var lmIds = detail.loadMore == null ? [] :
        (Array.isArray(detail.loadMore) ? detail.loadMore : [detail.loadMore]);
      var targetAttr = lmIds.length ? ' data-load-target="' + Ui.esc(lmIds[0]) + '"' : '';
      var buttons = lmIds.map(function (id) { return loadMoreButtonHtml(id); }).join('');
      return prefix + searchHtml +
        '<div class="preview-table__wrap">' +
          '<table class="preview-table"><thead>' + thead + '</thead>' +
          '<tbody' + targetAttr + '>' + tbody + '</tbody></table>' +
        '</div>' +
        buttons;
    }
    if (detail.kind === 'dynamicPage') {
      var spec = DYNAMIC_PAGE_SPECS[detail.pageKey];
      return '<div class="rule-detail__preview-block" data-dynamic-page="' + Ui.esc(detail.pageKey) + '">' +
        '<span class="rule-detail__preview-label">' + Ui.esc(spec ? spec.label : '') + '</span>' +
        '<div class="rule-detail__preview-body">載入中…</div>' +
      '</div>';
    }
    if (detail.kind === 'prescreenPreview') {
      // 每個 preview 項目產生一個帶佔位符的子區塊；data-prescreen-key 供 bind() 識別。
      return detail.previews.map(function (pv) {
        return '<div class="rule-detail__preview-block" data-prescreen-key="' + Ui.esc(pv.key) + '">' +
          '<span class="rule-detail__preview-label">' + Ui.esc(pv.label) + '</span>' +
          '<div class="rule-detail__preview-body">載入中…</div>' +
        '</div>';
      }).join('');
    }
    return '';
  }

  // 「載入更多」鈕標記:沿用既有 btn--ghost btn--tiny;data-load-more 對應同 id 的 tbody。
  function loadMoreButtonHtml(id) {
    return '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more"' +
      ' data-load-more="' + Ui.esc(id) + '">載入更多</button>';
  }

  // 載入更多分頁的金額欄一律走 Ui.money（固定四位小數），與首頁 detail() 欄同格式，避免分頁後尾數位數不一致。
  // 目前 num() 的呼叫點全為金額（tbAmount/glAmount/diff/debit/credit）；若日後拿它顯示計數，請改回 toLocaleString。
  function num(v) { return Ui.money(v); }

  // 「載入更多」規格表:id → { fetchPage(cursor)、columns(接列欄序與既有 table 對齊) }。
  // fetchPage 只發對應 query.*Page action、回 { rows, nextCursor };columns 為每欄的 row→cell 取值函式。
  // 純呼叫膠水:不算差異、不判命中、不組 SQL;顯示形狀(金額換算、借貸文字)由後端 row 決定,前端僅格式化呈現。
  var LOAD_MORE_SPECS = {
    completeness: {
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryCompletenessDiffPage({ cursor: cursor, pageSize: 200, sort: sort || null, search: search || null });
      },
      columns: [
        function (r) { return r.accountCode || ''; },
        function (r) { return r.accountName || ''; },
        function (r) { return num(r.tbAmount); },
        function (r) { return num(r.glAmount); },
        function (r) { return num(r.diff); }
      ]
    },
    docBalance: {
      fetchPage: function (cursor, sort, search) {
        return global.JetApi.queryDocBalancePage({ cursor: cursor, pageSize: 200, sort: sort || null, search: search || null });
      },
      columns: [
        function (r) { return r.documentNumber || ''; },
        function (r) { return num(r.debit); },
        function (r) { return num(r.credit); },
        function (r) { return num(r.diff); }
      ]
    },
  };

  // 空值四子項共對應五個後端 category 與固定的「異常類別」顯示字;
  // id 形如 null-<category>,動態註冊進 LOAD_MORE_SPECS（columns 與既有空值 table 欄序對齊）。
  function nullLoadMoreSpec(category, categoryLabel) {
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
  LOAD_MORE_SPECS['null-outOfRangeDate'] = nullLoadMoreSpec('outOfRangeDate', '核准日不在期間');
  LOAD_MORE_SPECS['null-nullDescription'] = nullLoadMoreSpec('nullDescription', '空摘要');
  LOAD_MORE_SPECS['null-nullDocument'] = nullLoadMoreSpec('nullDocument', '空傳票號');
  LOAD_MORE_SPECS['null-nullAccount'] = nullLoadMoreSpec('nullAccount', '空科目');

  // 來源品質全量明細（query.sourceQualityPage）：不套查核期間述詞，因此仍看得到
  // 被期間界定排除的空白總帳日期；欄序與下方 detail 的固定欄一致。
  LOAD_MORE_SPECS.sourceQuality = {
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
    return category === 'nullPostDate' ? '空白總帳日期' : (category || '');
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
      account: '空科目', document: '空傳票號', description: '空摘要',
      date: '核准日不在期間'
    };
    return (issues || []).map(function (k) { return map[k] || k; }).join('、');
  }

  // 空值紀錄測試在後端是單一規則（null_records_test）；UI 依異常類別拆成四個子項呈現。
  // 每個子項讀對應子計數作徽章，詳情用 nullRows 的 issues 標籤過濾出該類列
  // （明細仍是後端那份 ≤50 有界樣本，故過濾後筆數可能少於徽章數）。
  // categories：後端 nullRecordsPage 的 category 白名單值(一或多個);各對應一顆「載入更多」鈕,
  // 接續逐頁回取該類全量明細(首屏仍是後端 ≤50 有界樣本)。
  function nullSubItem(title, desc, issueKeys, countOf, categories) {
    return {
      title: title,
      desc: desc,
      status: function (v) {
        if (!v) { return null; }
        var n = countOf(v.nullRecordsTest);
        return n > 0
          ? { tone: 'alert', text: n.toLocaleString() + ' 筆異常' }
          : { tone: 'ok', text: '通過' };
      },
      detail: function (v) {
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
          loadMore: (categories || []).map(function (c) { return 'null-' + c; })
        };
      }
    };
  }

  var VALIDATION_ITEMS = [
    {
      title: '完整性測試',
      desc: '先核對匯入前後的筆數與借貸總額是否一致，再逐科目計算 GL 發生額與 TB 期間變動額是否相等；金額不符代表總帳母體可能缺漏。',
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
          else if (controlTotalsMismatch(v)) { text = '匯入前後總數不一致'; }
          else if (v.completenessTest.partA && v.completenessTest.partA.rowCountMatch == null) {
            text = '匯入總數無法核對';
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
              Ui.money(d.diff)
            ];
          });
          return {
            kind: 'table',
            columns: ['科目代碼', '科目名稱', 'TB 金額', 'GL 金額', '差額'],
            sortAction: 'query.completenessDiffPage', sortKeys: ['accountCode', 'accountName', 'tbAmount', 'glAmount', 'diff'],
            searchLabel: '依科目編號查看',
            rows: rows,
            prefix: eligibility.reason || eligibility.warning,
            loadMore: 'completeness'
          };
        }
        if (!eligibility.isEligible || eligibility.warning) {
          return {
            kind: 'reason',
            text: eligibility.reason || eligibility.warning,
            remedy: eligibility.isEligible ? null : '請重新執行「資料驗證」。'
          };
        }
        return null;
      }
    },
    {
      title: '借貸不平測試',
      desc: '逐張傳票檢查借方與貸方金額是否相等；借貸不平代表傳票編號或金額欄位可能有誤。',
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
              Ui.money(d.diff)
            ];
          });
          return {
            kind: 'table',
            columns: ['傳票號', '借方', '貸方', '差額'],
            sortAction: 'query.docBalancePage', sortKeys: ['documentNumber', 'debit', 'credit', 'diff'],
            searchLabel: '依傳票號碼查看',
            rows: rows,
            loadMore: 'docBalance'
          };
        }
        return null;
      }
    },
    {
      title: '資料可靠性測試',
      desc: '以固定種子抽出可重現的樣本，供人工確認摘要、日期及所選攸關資料元素（RDE）是否可靠，再判斷是否使用相關高風險篩選條件。ValidationReport 與 INF 報表中稱為「INF 抽樣測試」。',
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
    nullSubItem('核准日不在期間', '核准日（確認日期）落在查核期間之外的分錄。', ['date'],
      function (nrt) { return Number(nrt.outOfRangeDateCount); }, ['outOfRangeDate']),
    {
      // 來源品質是 validate.run 的獨立區塊，不是第五條資料驗證規則：它看的是匯入原貌，
      // 不套查核期間，因此筆數不與空值紀錄的四類相加。
      title: '總帳日期空白',
      desc: '只列出匯入 GL 中總帳日期空白的來源列；這些資料無法判斷查核期間，因此不會進入測試母體。日期在查核期間外不列在這裡。',
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
          columns: ['類別', '來源檔', '來源列號', '傳票號', '科目', '摘要'],
          sortAction: 'query.sourceQualityPage', sortKeys: [null, null, 'sourceRowNumber', 'documentNumber', 'accountCode', 'description'],
          searchLabel: '依傳票號碼查看',
          rows: rows,
          prefix: '這些分錄保留在匯入原貌中，但沒有進入查核期間的測試母體。',
          loadMore: 'sourceQuality'
        };
      }
    }
  ];

  // 空值紀錄測試的「空白摘要」「空白傳票號碼／科目」兩個子項改列在「風險預篩選」卡呈現
  // （呈現位置移動，資料來源不變）。兩者仍是後端單一 null_records_test 規則的子計數，
  // 由 validate.run 產生，因此它們的 runData 一律吃 state.lastRuns.validate（不是 prescreen）。
  // 「核准日不在期間」留在資料驗證卡，因為它是資料品質面（期間界定）而非風險候選面。
  var NULL_PRESCREEN_ITEMS = [
    nullSubItem('空白摘要', '摘要欄位空白的分錄。', ['description'],
      function (nrt) { return Number(nrt.nullDescriptionCount); }, ['nullDescription']),
    nullSubItem('空白傳票號碼', '傳票號碼空白的分錄。與空白科目編號分開列示；同一筆可能同時出現在兩份明細。', ['document'],
      function (nrt) { return Number(nrt.nullDocumentCount); }, ['nullDocument']),
    nullSubItem('空白科目編號', '科目編號空白的分錄。各類筆數分別計算，不將兩類相加當作分錄總數。', ['account'],
      function (nrt) { return Number(nrt.nullAccountCount); }, ['nullAccount'])
  ];

  var PRESCREEN_ITEMS = [
    {
      title: '期末財報準備日起核准之分錄',
      desc: '傳票核准日在案件設定的期末財報準備日當日或之後。仍只檢查查核期間的測試母體，不將這個日期解讀成查核期間結束日。',
      status: function (p) { return p ? hitCountStatus(p.postPeriodApproval) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.postPeriodApproval.naReason) {
          return { kind: 'reason', text: p.postPeriodApproval.naReason, remedy: '確認查核期間與核准日欄位配對正確' };
        }
        if (Number(p.postPeriodApproval.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'postPeriodApproval' }] };
        }
        return null;
      }
    },
    {
      title: '分錄摘要出現特定描述',
      desc: '摘要包含預設的 25 個中英文關鍵字之一，包括 legacy 的繁體與簡體詞。在進階篩選選「摘要關鍵字（可自行編輯）」可檢視及調整整份清單。',
      status: function (p) { return p ? hitCountStatus(p.suspiciousKeywords) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.suspiciousKeywords.naReason) {
          return { kind: 'reason', text: p.suspiciousKeywords.naReason, remedy: '確認摘要欄位配對正確' };
        }
        if (Number(p.suspiciousKeywords.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'suspiciousKeywords' }] };
        }
        return null;
      }
    },
    {
      title: '未預期出現之特定借貸組合',
      desc: '收入貸方所在傳票缺少應收、現金或預收款借方，可能是缺少正常對方科目的異常收入（需先匯入科目配對）。',
      status: function (p) { return p ? hitCountStatus(p.unexpectedAccountPair) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.unexpectedAccountPair.naReason) {
          return { kind: 'reason', text: p.unexpectedAccountPair.naReason, remedy: '先到「欄位配對」匯入科目配對' };
        }
        if (Number(p.unexpectedAccountPair.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'unexpectedAccountPair' }] };
        }
        return null;
      }
    },
    {
      title: '分錄金額中有連續零的尾數',
      desc: '依本次顯示的尾零位數，找出金額主單位整數部分符合連續零尾數的分錄，供審計員進一步查核。',
      status: function (p) {
        return p ? hitCountStatus(p.trailingZeros,
          '本次門檻：尾數連續 ' + p.trailingZeros.zerosThreshold + ' 個 0。') : null;
      },
      detail: function (p) {
        if (!p) { return null; }
        if (p.trailingZeros.naReason) {
          return { kind: 'reason', text: p.trailingZeros.naReason, remedy: '確認金額欄位配對正確' };
        }
        if (Number(p.trailingZeros.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'trailingZeros' }] };
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
        return { tone: 'info', text: p.creatorSummary.creators.length + ' 位人員' };
      },
      detail: function (p) {
        if (!p) { return null; }
        if (p.creatorSummary.naReason) {
          return { kind: 'reason', text: p.creatorSummary.naReason, remedy: '確認建立者欄位配對正確' };
        }
        if (p.creatorSummary.creators && p.creatorSummary.creators.length > 0) {
          var rows = p.creatorSummary.creators.map(function (c) {
            return [
              c.createdBy || '',
              Number(c.entryCount).toLocaleString(),
              Ui.money(c.debitTotal),
              Ui.money(c.creditTotal),
              Number(c.manualCount).toLocaleString()
            ];
          });
          return { kind: 'table', columns: ['人員識別值', '分錄筆數', '借方', '貸方', '人工分錄筆數'], rows: rows };
        }
        return null;
      }
    },
    {
      title: '較少使用之科目',
      desc: '按科目編號統計分錄筆數並由低到高排列，作為了解母體分布與少用科目的彙總參考。',
      status: function (p) {
        if (!p) { return null; }
        return { tone: 'info', text: Number(p.rareAccounts.distinctAccountCount).toLocaleString() + ' 個科目' };
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
          return { kind: 'table', columns: ['科目代碼', '科目名稱', '分錄筆數', '借方', '貸方'], rows: rows };
        }
        return null;
      }
    },
    {
      title: '週末過帳／核准之分錄',
      desc: '總帳日期或傳票核准日落在設定週末日的分錄（補班日仍納入）。',
      status: function (p) { return p ? pairCountStatus(p.weekendActivity) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.weekendActivity.naReason) {
          return { kind: 'reason', text: p.weekendActivity.naReason, remedy: '確認總帳日期欄位配對正確' };
        }
        var previews = [];
        if (Number(p.weekendActivity.postingCount) > 0) {
          previews.push({ label: '過帳側', key: 'weekendPosting' });
        }
        if (p.weekendActivity.approvalCount != null && Number(p.weekendActivity.approvalCount) > 0) {
          previews.push({ label: '核准側', key: 'weekendApproval' });
        }
        return previews.length > 0 ? { kind: 'prescreenPreview', previews: previews } : null;
      }
    },
    {
      title: '假日過帳／核准之分錄',
      desc: '總帳日期或傳票核准日落在國定假日的分錄（需先匯入假日清單）。',
      status: function (p) { return p ? pairCountStatus(p.holidayActivity) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.holidayActivity.naReason) {
          return { kind: 'reason', text: p.holidayActivity.naReason, remedy: '先匯入假日清單' };
        }
        var previews = [];
        if (Number(p.holidayActivity.postingCount) > 0) {
          previews.push({ label: '過帳側', key: 'holidayPosting' });
        }
        if (p.holidayActivity.approvalCount != null && Number(p.holidayActivity.approvalCount) > 0) {
          previews.push({ label: '核准側', key: 'holidayApproval' });
        }
        return previews.length > 0 ? { kind: 'prescreenPreview', previews: previews } : null;
      }
    },
    {
      title: '回溯過帳之分錄',
      desc: '總帳日期早於傳票日期的分錄。兩個日期均有值才比較；需先配對傳票日期。這是供審計員查核的日期差異，不代表已判定不當過帳。',
      status: function (p) { return p ? hitCountStatus(p.backdatedPosting) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.backdatedPosting.naReason) {
          return { kind: 'reason', text: p.backdatedPosting.naReason, remedy: '確認傳票日期欄位配對正確' };
        }
        if (Number(p.backdatedPosting.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'backdatedPosting' }] };
        }
        return null;
      }
    },
    {
      title: '摘要空白之分錄',
      desc: '摘要未填寫的分錄；可在下一步直接作為篩選條件。',
      status: function (p) { return p ? hitCountStatus(p.blankDescription) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.blankDescription.naReason) {
          return { kind: 'reason', text: p.blankDescription.naReason, remedy: '確認摘要欄位配對正確' };
        }
        if (Number(p.blankDescription.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'blankDescription' }] };
        }
        return null;
      }
    },
    {
      title: '非授權編製人員之分錄',
      desc: '傳票建立人員的識別值非空白，且未列在匯入的授權清單中。請使用與 GL 相同的姓名或員工代碼欄位；這個差異本身不代表已判定越權。',
      status: function (p) { return p ? hitCountStatus(p.nonAuthorizedPreparer) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.nonAuthorizedPreparer.naReason) {
          return { kind: 'reason', text: p.nonAuthorizedPreparer.naReason,
            remedy: '先到「匯入資料」上傳授權編製人員清單' };
        }
        if (Number(p.nonAuthorizedPreparer.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'nonAuthorizedPreparer' }] };
        }
        return null;
      }
    },
    {
      title: '低頻編製者之分錄',
      desc: '以目前測試母體按傳票建立人員計算分錄筆數，列出 11 筆以下的人員分錄。同張傳票多列會計多筆；空白人員不命中。自訂條件可另設分錄筆數上限。',
      status: function (p) { return p ? hitCountStatus(p.lowFrequencyPreparer) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (p.lowFrequencyPreparer.naReason) {
          return { kind: 'reason', text: p.lowFrequencyPreparer.naReason, remedy: '確認建立者欄位配對正確' };
        }
        if (Number(p.lowFrequencyPreparer.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'lowFrequencyPreparer' }] };
        }
        return null;
      }
    },
    {
      title: '低頻科目之分錄',
      desc: '以目前測試母體按科目編號計算分錄筆數，列出 11 筆以下的科目分錄。同張傳票多列會計多筆；空白科目不命中。自訂條件可另設分錄筆數上限。',
      status: function (p) { return p ? hitCountStatus(p.lowFrequencyAccount) : null; },
      detail: function (p) {
        if (!p) { return null; }
        if (Number(p.lowFrequencyAccount.count) > 0) {
          return { kind: 'prescreenPreview', previews: [{ label: '命中分錄', key: 'lowFrequencyAccount' }] };
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
      '<div class="panel panel--wide">' +
        '<h2 class="panel__title">資料驗證與測試</h2>' +
        (ready ? '' :
          '<p class="panel__warn">尚未確認 GL 欄位配對，無法執行；請先回「欄位配對」完成確認。</p>') +
        Ui.mappingReviewBannerHtml(state) +
        Ui.staleNoticeHtml(state, 'validation',
          '上游資料已變更，先前的資料驗證結果已失效；請重新執行資料驗證。') +
        Ui.staleNoticeHtml(state, 'prescreen',
          '上游資料已變更，先前的預篩選結果已失效；如需查看母體彙總或輔助訊號，請重新執行預篩選。') +
        statsBarHtml(v) +
        populationSummaryHtml(v) +
        validationCardHtml(v, ready, state) +
        taxonomyCardHtml(state) +
        accountMappingCardHtml(state.importState.accountMapping, !!v) +
        prescreenCardHtml(p, v, ready, state, eligibility) +
        Ui.stepFooterHtml(state) +
      '</div>';

    bind(container, ready);
  }

  function statsBarHtml(v) {
    var stats = v && v.stats ? v.stats : null;
    function card(label, value) {
      return (
        '<div class="stat-card">' +
          '<span class="stat-card__value">' + value + '</span>' +
          '<span class="stat-card__label">' + label + '</span>' +
        '</div>'
      );
    }

    return (
      '<div class="stats-bar">' +
        card('測試母體分錄筆數', stats ? Number(stats.glRowCount).toLocaleString() : '—') +
        card('傳票數', stats ? Number(stats.voucherCount).toLocaleString() : '—') +
        card('借貸淨額（應為 0）', stats ? Ui.money(stats.net) : '—') +
        card('查核期間', stats ? Ui.esc(stats.periodStart) + ' ～ ' + Ui.esc(stats.periodEnd) : '—') +
      '</div>'
    );
  }

  // 母體分區：全部標準化列 = 測試母體 + 期間排除 + 過帳狀態排除。
  // 三個數字與兩類排除原因都由系統端在同一次驗證裁定，畫面只複述，不自行相減或推算。
  function populationSummaryHtml(v) {
    var summary = v && v.populationSummary ? v.populationSummary : null;
    if (!summary) { return ''; }
    var raw = summary.raw || {};
    var effective = summary.effective || {};
    var excluded = summary.excluded || {};

    function cell(label, value, hint) {
      return '<div class="population-cell">' +
        '<span class="population-cell__value">' + Number(value).toLocaleString() + '</span>' +
        '<span class="population-cell__label">' + Ui.esc(label) + '</span>' +
        (hint ? '<span class="population-cell__hint">' + Ui.esc(hint) + '</span>' : '') +
        '</div>';
    }

    return (
      '<section class="population-summary" data-bind="population-summary">' +
        '<h3 class="population-summary__title">測試母體與排除原因</h3>' +
        '<div class="population-summary__grid">' +
          cell('標準化後全部分錄', raw.rowCount, '匯入並成功標準化的所有列') +
          cell('進入測試母體', effective.rowCount, '符合查核期間與過帳狀態設定的分錄') +
          cell('期間排除', excluded.byPeriodCount, '總帳日期空白或不在查核期間') +
          cell('過帳狀態排除', excluded.byPostingStatusCount, '過帳狀態不符合本案設定') +
        '</div>' +
        '<button type="button" class="btn btn--ghost btn--tiny" data-action="preview-excluded-entries">' +
          '檢視被排除的分錄</button>' +
      '</section>'
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
    return '<ul class="rule-list">' + items.map(function (item, idx) {
      // status 為 null = 未執行（無 runData 或 resume 舊結果缺此規則鍵）→ 不算 detail,
      // 避免 detail 去取缺漏子物件而丟例外拖垮整步;詳情只在規則確有結果時才存在。
      var status = item.status(runData);
      var detail = (status && item.detail) ? item.detail(runData) : null;
      var toggleBtn = detail
        ? '<button type="button" class="btn btn--ghost btn--tiny rule-item__toggle"' +
            ' data-action="toggle-detail" data-scope="' + Ui.esc(scope) + '" data-idx="' + idx + '">' +
            '檢視</button>'
        : '';
      var detailContainer = detail
        ? '<div class="rule-detail" data-bind="rule-detail-' + Ui.esc(scope) + '-' + idx + '" hidden>' +
            detailHtml(detail) +
          '</div>'
        : '';
      return (
        '<li class="rule-item">' +
          '<div class="rule-item__main">' +
            '<span class="rule-item__title">' + Ui.esc(item.title) + '</span>' +
            '<span class="rule-item__desc">' + Ui.esc(item.desc) + '</span>' +
          '</div>' +
          ruleBadge(status) +
          toggleBtn +
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
      '<div class="report-output">' +
        '<div class="report-output__head">' +
          '<div>' +
            '<h4 class="report-output__title">驗證階段報告</h4>' +
            '<p class="report-output__hint">下方列出已產生的 ValidationReport 與 INF Report 檔名。' +
              '檔案位於目前案件資料夾，按右上角「資料夾」即可開啟。' +
              'INF 報表就是資料可靠性測試的樣本；科目配對範本的狀態另列於下方卡片。</p>' +
          '</div>' +
          '<button type="button" class="btn btn--ghost" data-action="export-validation-artifacts">' +
            (complete ? '重新產生兩份報告' : '產生兩份驗證報告') + '</button>' +
        '</div>' +
        validationOutputStatusHtml('reports') +
        Ui.reportArtifactListHtml(artifacts, '驗證已完成，報告尚未產生。') +
      '</div>'
    );
  }

  function validationOutputStatusHtml(kind) {
    var state = Store.getState();
    var output = state.validationOutput[kind];
    var validation = state.lastRuns.validate;
    if (!output || !validation || output.runId !== validation.resultRef.runId) { return ''; }
    var statusClass = output.status === 'failed' ? 'form-notice' : 'validation-output-status';
    return '<p class="' + statusClass + '" data-validation-output="' + kind + '" role="status">' +
      Ui.esc(output.message) + '</p>';
  }

  function validationCardHtml(v, ready, state) {
    return (
      '<section class="rule-card">' +
        '<div class="rule-card__head">' +
          '<h3 class="rule-card__title">資料驗證</h3>' +
          '<button type="button" class="btn" data-action="run-validate"' + (ready ? '' : ' disabled') +
            '>' + (v ? '重新執行驗證' : '執行驗證') + '</button>' +
        '</div>' +
        '<p class="rule-card__sub">確認匯入母體完整、平衡且可信；任何一項不通過，後續測試的基礎都可能不可靠。</p>' +
        lastRunLineHtml(v) +
        ruleListHtml(VALIDATION_ITEMS, v, 'v') +
        validationArtifactsHtml(state, v) +
      '</section>'
    );
  }

  // 科目配對匯入區塊：方法論上排在「資料驗證」（含完整性測試）之後、「風險預篩選」之前。
  // 預篩選的「未預期出現之特定借貸組合」依賴科目配對，因此先提供匯入入口。
  //
  // 解鎖門檻:只有在「資料驗證」已執行過(state.lastRuns.validate 存在)後,才開放匯入/重新匯入鈕。
  // legacy 明示完整性有差異仍可篩選。eligibility 只拒絕不存在、過期或損壞的結果，不要求差異歸零。
  // 狀態恆顯示:不論門檻,只要已匯入(含 resume)就顯示「已匯入 N 科目」+「預覽科目配對」;
  // 被門檻擋住的只有匯入/重新匯入鈕本身。前端只做 UX gate,後端預篩選仍是 unexpectedAccountPair 的權威 gating。
  // 分類留白的科目在投影時已視為 Others（同 legacy），篩選不受影響；這裡只讓審計員看到哪些科目沒填，
  // 想改分類就到範本 C 欄填好再重新匯入。清單走 query.accountMappingBlankPage 的有界分頁。
  function blankCategoryNoticeHtml(info) {
    if (!info || !(info.blankCategoryCount > 0)) { return ''; }
    return '<p class="import-card__status" data-bind="blank-category-notice">分類留白 ' + Number(info.blankCategoryCount).toLocaleString() +
        ' 筆，視為 Others。要改分類時，在範本 C 欄填好再重新匯入。 ' +
        '<button type="button" class="btn btn--ghost btn--tiny" data-action="list-blank-categories" aria-expanded="false">列出科目</button></p>' +
      '<div data-bind="blank-category-list" hidden></div>';
  }

  function accountMappingCardHtml(info, validateHasRun) {
    var status = info
      ? '<p class="import-card__status import-card__status--ok">已匯入 ' + info.rowCount + ' 個科目' +
          (info.fileName ? '（' + Ui.esc(info.fileName) + '）' : '') + '。</p>'
      : '<p class="import-card__status">尚未匯入。</p>';

    // 已匯入時恆提供「預覽科目配對」(沿用全域資料預覽面板);與門檻無關。
    var previewBtn = info
      ? '<button type="button" class="btn btn--ghost" data-action="preview-account-mapping"' +
          ' title="檢視已匯入的科目配對">預覽</button>'
      : '';

    // 匯入/重新匯入鈕只在驗證已執行後出現;否則顯示前置條件提示。
    var importControls = validateHasRun
      ? '<button type="button" class="btn' + (info ? ' btn--ghost' : '') +
          '" data-action="import-account-mapping">' +
          (info ? '重新匯入' : '匯入') + '</button>'
      : '<p class="rule-card__gate">請先執行「資料驗證」後，再匯入科目配對。</p>';

    // 下載空白範本鈕與匯入鈕同一 gating(驗證已執行後才出現):範本 A/B 已填(GL∪TB 母體)、
    // C 欄下拉留空供審計員填,填完原檔上傳走同一匯入鈕。純呼叫膠水,母體/格式全在後端決定。
    var templateBtn = validateHasRun
      ? '<button type="button" class="btn btn--ghost" data-action="ensure-account-mapping-template" title="取得範本，保留已填內容">取得範本</button>' +
        '<button type="button" class="btn btn--ghost" data-action="download-account-mapping-template"' +
          ' title="重新產生空白範本，覆寫目前檔案；已填的分類會被清除">' +
          '重建範本</button>'
      : '';

    return (
      '<section class="rule-card" data-bind="account-mapping-card" tabindex="-1">' +
        '<h3 class="rule-card__title">科目配對</h3>' +
        status +
        '<p class="rule-card__sub">取得範本後，用 Excel 填好 C 欄分類，再匯入。</p>' +
        blankCategoryNoticeHtml(info) +
        validationOutputStatusHtml('template') +
        '<div class="import-card__actions">' +
          importControls +
          templateBtn +
          previewBtn +
        '</div>' +
        (validateHasRun ? '<p class="rule-card__sub">重建範本會覆寫已填的分類。</p>' : '') +
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

  // 仍被已保存篩選情境引用的自訂分類：畫面先擋刪除並說明原因。
  // 科目配對本身的引用只有系統端知道，因此後端仍會以「分類使用中」為最終權威。
  function taxonomyCategoriesInUse(state) {
    var used = {};
    (state.filter.savedScenarios || []).forEach(function (scenario) {
      (scenario.groups || []).forEach(function (group) {
        (group.rules || []).forEach(function visit(rule) {
          ['debitCategoryIds', 'creditCategoryIds', 'categoryIds'].forEach(function (key) {
            if (Array.isArray(rule[key])) {
              rule[key].forEach(function (id) { used[id] = true; });
            }
          });
          (rule.rules || []).forEach(visit);
        });
      });
    });
    return used;
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
    var inUse = taxonomyCategoriesInUse(state);
    var problems = taxonomyProblems(rows);
    var dirty = !!taxonomyDraft;

    var items = rows.map(function (row, index) {
      var lockedReason = row.isBuiltIn
        ? '內建分類不可刪除'
        : (inUse[row.categoryId] ? '已被篩選情境使用，不可刪除' : '');
      var roleOptions = Ui.ACCOUNT_TAXONOMY_ROLES.map(function (role) {
        return '<option value="' + role.value + '"' +
          (row.semanticRole === role.value ? ' selected' : '') + '>' + role.label + '</option>';
      }).join('');
      return '<li class="taxonomy-row' + (row.isBuiltIn ? ' is-builtin' : '') + '">' +
        '<div class="taxonomy-row__field"><label for="taxonomy-label-' + index + '">分類名稱</label>' +
        '<input class="form__input taxonomy-row__label" type="text" id="taxonomy-label-' + index + '"' +
          ' data-taxonomy-label="' + Ui.esc(row.rowId) + '" data-focus-key="taxonomy-label-' + Ui.esc(row.rowId) + '" value="' + Ui.esc(row.label) +
          '" maxlength="' + Ui.TAXONOMY_MAX_LABEL_LENGTH + '"></div>' +
        '<div class="taxonomy-row__field"><label for="taxonomy-role-' + index + '">審計角色</label>' +
        '<select id="taxonomy-role-' + index + '" data-taxonomy-role="' + Ui.esc(row.rowId) + '"' +
          ' data-focus-key="taxonomy-role-' + Ui.esc(row.rowId) + '"' +
          (row.isBuiltIn ? ' disabled aria-disabled="true"' : '') + '>' + roleOptions + '</select></div>' +
        '<label class="taxonomy-row__field">上層分類<select data-taxonomy-parent="' + Ui.esc(row.rowId) + '" data-focus-key="taxonomy-parent-' + Ui.esc(row.rowId) + '"><option value="">無（最上層）</option>' +
          rows.filter(function (candidate) { return candidate.categoryId && candidate.categoryId !== row.categoryId; }).map(function (candidate) {
            return '<option value="' + Ui.esc(candidate.categoryId) + '"' + (row.parentCategoryId === candidate.categoryId ? ' selected' : '') + '>' + Ui.esc(candidate.label) + '</option>';
          }).join('') + '</select></label>' +
        '<div class="taxonomy-row__actions">' +
        (row.isBuiltIn ? '<span class="taxonomy-row__badge">內建</span>' : '') +
        '<button type="button" class="btn btn--ghost btn--tiny" data-taxonomy-remove="' + Ui.esc(row.rowId) + '"' +
          (lockedReason ? ' disabled title="' + Ui.esc(lockedReason) + '"' : '') + '>移除</button></div>' +
        '</li>';
    }).join('');

    return (
      '<section class="rule-card" data-bind="account-taxonomy-card">' +
        '<h3 class="rule-card__title">科目分類</h3>' +
        '<p class="rule-card__sub">設定科目配對使用的分類。新增上層分類後，請先保存再選取。</p>' +
        '<details class="taxonomy-help"><summary>分類與審計角色的差別</summary><p>' +
          '分類名稱可修改；審計角色決定分類在審計條件中的用途，不由上層繼承。' +
          '篩選可選分類本身、包含下層，或相同審計角色。調整上層會影響包含下層的篩選結果。</p></details>' +
        '<ul class="taxonomy-list" data-preserve-scroll="taxonomy-list">' + items + '</ul>' +
        '<p class="form-notice" data-bind="taxonomy-problems"' + (problems.length ? '' : ' hidden') + '>' +
          (problems.length ? '尚需補齊：' + problems.map(Ui.esc).join('、') : '') + '</p>' +
        '<div class="import-card__actions">' +
          '<button type="button" class="btn btn--ghost" data-action="add-taxonomy-category">新增分類</button>' +
          '<button type="button" class="btn" data-action="save-taxonomy"' +
            (dirty && problems.length === 0 ? '' : ' disabled') + '>保存科目分類</button>' +
          '<button type="button" class="btn btn--ghost" data-action="reset-taxonomy"' +
            (dirty ? '' : ' hidden') + '>取消變更</button>' +
        '</div>' +
        '<p class="rule-card__gate" data-bind="taxonomy-dirty-notice"' + (dirty ? '' : ' hidden') +
          '>保存分類會使既有的風險預篩選與篩選命中失效，需要重新執行。</p>' +
      '</section>'
    );
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
        var problems = taxonomyProblems(rows);
        var notice = card.querySelector('[data-bind="taxonomy-problems"]');
        notice.textContent = problems.length ? '尚需補齊：' + problems.join('、') : '';
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
          if (row.rowId === rowId && !row.isBuiltIn) { row.semanticRole = select.value; }
        });
        Store.touch();
      });
    });

    card.querySelectorAll('[data-taxonomy-parent]').forEach(function (select) {
      select.addEventListener('change', function () {
        var rows = startTaxonomyDraft(Store.getState());
        var row = rows.find(function (item) { return item.rowId === select.getAttribute('data-taxonomy-parent'); });
        if (row) { row.parentCategoryId = select.value || null; Store.touch(); }
      });
    });

    card.querySelectorAll('[data-taxonomy-remove]').forEach(function (button) {
      button.addEventListener('click', function () {
        var rowId = button.getAttribute('data-taxonomy-remove');
        var rows = startTaxonomyDraft(Store.getState());
        taxonomyDraft = rows.filter(function (row) { return row.rowId !== rowId; });
        Store.touch();
      });
    });

    var addBtn = card.querySelector('[data-action="add-taxonomy-category"]');
    if (addBtn) {
      addBtn.addEventListener('click', function () {
        var rows = startTaxonomyDraft(Store.getState());
        taxonomyDraftSeq++;
        rows.push({
          rowId: 'draft-' + taxonomyDraftSeq,
          categoryId: null,
          label: '',
          semanticRole: 'others',
          isBuiltIn: false
        });
        Store.touch();
      });
    }

    var resetBtn = card.querySelector('[data-action="reset-taxonomy"]');
    if (resetBtn) {
      resetBtn.addEventListener('click', function () {
        taxonomyDraft = null;
        Store.touch();
      });
    }

    var saveBtn = card.querySelector('[data-action="save-taxonomy"]');
    if (saveBtn) {
      saveBtn.addEventListener('click', function () {
        var state = Store.getState();
        var rows = taxonomyRows(state);
        if (taxonomyProblems(rows).length) { return; }
        Ui.run('保存科目分類', function () {
          // ordinal 由目前清單順序決定；新分類省略 categoryId，由系統端產生 stable ID。
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
            taxonomyDraftRevision = data.revision;
            Store.setTaxonomyAfterSave(data);
            Store.addMessage('科目分類已更新（共 ' + data.categories.length +
              ' 類）；風險預篩選與篩選命中需要重新執行。', 'info');
            return data;
          });
        }, { logCompletion: true });
      });
    }
  }

  function exportValidationArtifacts(runData) {
    var runId = runData && runData.resultRef ? runData.resultRef.runId : null;
    if (!runId) { return Promise.reject(new Error('找不到可匯出的驗證結果。')); }
    return performValidationOutput('reports', runId, function () {
      return global.JetApi.exportValidationArtifacts({ runId: runId });
    }, function (data) {
      var artifacts = data.artifacts || [];
      Store.applyReportExport(data);
      Store.addMessage('已在專案目錄產生驗證階段的 ' + artifacts.length + ' 份報告。', 'info');
      return '兩份驗證報告已產生，可從下方清單查看。';
    });
  }

  function requireValidationOutputActive(projectId, runId) {
    var state = Store.getState();
    var validation = state.lastRuns.validate;
    if (state.cancellationRequested || !state.project || state.project.projectId !== projectId
        || !validation || validation.resultRef.runId !== runId) {
      var error = new Error('後續檔案產生已取消，已完成的驗證與檔案會保留。');
      error.code = 'operation_cancelled';
      throw error;
    }
  }

  function performValidationOutput(kind, runId, action, onSuccess) {
    var projectId = Store.getState().project.projectId;
    requireValidationOutputActive(projectId, runId);
    Store.setValidationOutput(kind, { runId: runId, status: 'pending',
      message: kind === 'template' ? '正在準備科目配對範本…' : '正在產生兩份驗證報告…' });
    return action().then(function (data) {
      // 已完成的 action 結果照實顯示；取消只影響下一個尚未開始的 action。
      if (!Store.getState().project || Store.getState().project.projectId !== projectId) { return data; }
      Store.setValidationOutput(kind, { runId: runId, status: 'ready', message: onSuccess(data) });
      return data;
    }).catch(function (error) {
      if (Store.getState().project && Store.getState().project.projectId === projectId) {
        Store.setValidationOutput(kind, { runId: runId, status: 'failed',
          message: (kind === 'template' ? '範本尚未產生：' : '報告尚未產生：') + error.message + ' 可使用此區按鈕重試。' });
      }
      throw error;
    });
  }

  function exportAccountMappingTemplate(runData, onlyIfMissing) {
    var runId = runData && runData.resultRef ? runData.resultRef.runId : null;
    if (!runId) { return Promise.reject(new Error('找不到可使用的驗證結果。')); }
    return performValidationOutput('template', runId, function () {
      return global.JetApi.exportAccountMappingTemplate({ runId: runId, onlyIfMissing: onlyIfMissing });
    }, function (data) {
      return data.disposition === 'kept'
        ? '已保留案件資料夾的 ' + data.fileName + '，不改動其中已填寫的分類。'
        : '已建立 ' + data.fileName + '（' + data.rowCount + ' 個科目）。用 Excel 填好 C 欄並存檔後，再匯入同一份檔案。';
    });
  }

  function exportValidationOutputs(runData) {
    var failures = [];
    function preserveFailure(error) {
      if (error && error.code === 'operation_cancelled') { throw error; }
      failures.push(error);
    }
    return exportValidationArtifacts(runData).catch(preserveFailure).then(function () {
      return exportAccountMappingTemplate(runData, true).catch(preserveFailure);
    }).then(function () {
      if (failures.length) {
        throw new Error('資料驗證已完成，部分檔案尚未產生。已完成的檔案會保留，請在對應區塊重試。');
      }
    });
  }

  function exportPrescreenReport(runData) {
    var runId = runData && runData.resultRef ? runData.resultRef.runId : null;
    if (!runId) { return Promise.reject(new Error('找不到可匯出的預篩選結果。')); }
    return global.JetApi.exportPrescreenReport({ runId: runId }).then(function (data) {
      Store.applyReportExport(data);
      Store.addMessage('已在專案目錄產生預篩選報告。', 'info');
      return data;
    });
  }

  // 科目配對區塊綁定:沿用既有 action / store / 資料預覽契約,僅位置從 import-step 移來。
  // 預覽鈕與門檻無關(已匯入即可預覽);匯入鈕只在驗證已執行時才存在(render 已 gate),
  // 此處用存在性檢查綁定,故門檻邏輯不洩漏到 bind。
  function bindAccountMappingCard(container) {
    var blankBtn = container.querySelector('[data-action="list-blank-categories"]');
    var blankList = container.querySelector('[data-bind="blank-category-list"]');
    if (blankBtn && blankList) {
      blankBtn.addEventListener('click', function () {
        if (!blankList.hidden) { blankList.hidden = true; blankBtn.setAttribute('aria-expanded', 'false'); return; }
        blankList.hidden = false; blankBtn.setAttribute('aria-expanded', 'true');
        if (blankList.dataset.loaded) { return; }
        blankList.dataset.loaded = '1';
        blankList.innerHTML = '<ul class="kv-list" data-bind="blank-category-rows"></ul>' +
          '<button type="button" class="btn btn--ghost btn--tiny" data-action="blank-category-more">載入更多</button>';
        var rowsEl = blankList.querySelector('[data-bind="blank-category-rows"]');
        var moreBtn = blankList.querySelector('[data-action="blank-category-more"]');
        function appendRows(rows) {
          rowsEl.insertAdjacentHTML('beforeend', rows.map(function (row) {
            return '<li>' + Ui.esc(row.accountCode) + (row.accountName ? '　' + Ui.esc(row.accountName) : '') + '</li>';
          }).join(''));
        }
        Ui.bindLoadMore(moreBtn, function (cursor) {
          return global.JetApi.queryAccountMappingBlankPage({ cursor: cursor, pageSize: 200 });
        }, appendRows);
        moreBtn.click();
      });
    }

    var previewBtn = container.querySelector('[data-action="preview-account-mapping"]');
    if (previewBtn && Ui.openDataPreview) {
      previewBtn.addEventListener('click', function () {
        Ui.openDataPreview('accountMappings');
      });
    }

    // 範本綁定最近的 validate run；後端直接寫進案件資料夾，是工作檔，不進報告清單。
    var templateBtn = container.querySelector('[data-action="download-account-mapping-template"]');
    if (templateBtn) {
      templateBtn.addEventListener('click', function () {
        Ui.run('重新產生空白科目配對範本', function () {
          return exportAccountMappingTemplate(Store.getState().lastRuns.validate, false);
        }, { logCompletion: true });
      });
    }

    var ensureTemplateBtn = container.querySelector('[data-action="ensure-account-mapping-template"]');
    if (ensureTemplateBtn) {
      ensureTemplateBtn.addEventListener('click', function () {
        Ui.run('取得科目配對範本', function () {
          return exportAccountMappingTemplate(Store.getState().lastRuns.validate, true);
        });
      });
    }

    var btn = container.querySelector('[data-action="import-account-mapping"]');
    if (!btn) { return; }

    btn.addEventListener('click', function () {
      Ui.run('匯入科目配對', function () {
        return global.JetApi.hostSelectFile({
          title: '選擇科目配對檔（科目代號、科目名稱、標準化分類）',
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
            Store.addMessage('科目配對匯入完成：' + data.rowCount + ' 個科目。', 'info');
            return data;
          });
        });
      }, {
        logCompletion: true,
        logCompletionWhen: function (result) { return !!result; }
      });
    });
  }

  // p = prescreen.run 結果（驅動 PRESCREEN_ITEMS）；v = validate.run 結果（驅動移入的空值兩子項）。
  // 空值兩子項雖列在此卡，資料仍來自 validate.run，故獨立用 scope 'pn' 渲染、傳入 v（不與 'p' 混用）。
  function prescreenArtifactHtml(state, p, positioning) {
    if (!p || !p.resultRef || !p.resultRef.runId) { return ''; }
    var artifacts = Ui.currentReportArtifacts(
      state, ['prescreenReport'], { prescreenRunId: p.resultRef.runId });
    return (
      '<div class="report-output">' +
        '<div class="report-output__head">' +
          '<div>' +
            '<h4 class="report-output__title">Pre-screening Report</h4>' +
            '<p class="report-output__hint">' + Ui.esc(positioning.reportGuidance) + '</p>' +
          '</div>' +
          '<button type="button" class="btn btn--ghost" data-action="export-prescreen-report">' +
            (artifacts.length ? '重新產生報告' : '在這裡先產生') + '</button>' +
        '</div>' +
        Ui.reportArtifactListHtml(artifacts, '預篩選已完成，報告尚未產生。') +
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
        '<details class="prescreen-signals">' +
          '<summary class="prescreen-signals__summary">' +
            '<span>分錄檢查</span><span class="prescreen-signals__hint">展開查看</span>' +
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

  // 以系統端回傳的欄位定義渲染結果表，並接上同一份定義的「載入更多」。
  // 欄序、欄名與每格的取值鍵都來自 response，畫面不重建這份 schema。
  function renderDynamicPage(body, data, spec) {
    var columns = (data && data.columns) || [];
    var cells = Ui.dynamicColumnCells(columns);
    body.innerHTML =
      Ui.pageSearchHtml('依傳票號碼查看') +
      '<div class="preview-table__wrap">' +
        '<table class="preview-table"><thead>' + Ui.dynamicColumnHeadHtml(columns) + '</thead>' +
        '<tbody data-dynamic-body></tbody></table>' +
      '</div>' +
      '<button type="button" class="btn btn--ghost btn--tiny rule-detail__load-more"' +
        ' data-dynamic-load-more' + (data && data.nextCursor != null ? '' : ' hidden') + '>載入更多</button>';

    var tbody = body.querySelector('[data-dynamic-body]');
    Ui.appendRowsToTbody(tbody, (data && data.rows) || [], cells);

    // 首擊或換排序、搜尋時先清掉這裡的首屏列，再自資料庫端的第一頁接續，避免兩套順序混排。
    Ui.bindPagedTable(body, {
      fetchPage: function (cursor, sort, search) { return spec.fetchPage(cursor, sort, search); },
      appendRows: function (rows) { Ui.appendRowsToTbody(tbody, rows, cells); },
      clearRows: function () { tbody.innerHTML = ''; },
      loadMore: body.querySelector('[data-dynamic-load-more]'),
      table: body.querySelector('.preview-table'),
      search: body.querySelector('[data-page-search]')
    });
  }

  function bind(container, ready) {
    Ui.bindStepFooter(container);

    var excludedBtn = container.querySelector('[data-action="preview-excluded-entries"]');
    if (excludedBtn && Ui.openDataPreview) {
      excludedBtn.addEventListener('click', function () {
        Ui.openDataPreview('glExcludedEntries');
      });
    }

    bindTaxonomyCard(container);
    bindAccountMappingCard(container);

    var validationExportBtn = container.querySelector('[data-action="export-validation-artifacts"]');
    if (validationExportBtn) {
      validationExportBtn.addEventListener('click', function () {
        Ui.run('產生驗證階段報告', function () {
          return exportValidationArtifacts(Store.getState().lastRuns.validate);
        }, { logCompletion: true });
      });
    }

    var prescreenExportBtn = container.querySelector('[data-action="export-prescreen-report"]');
    if (prescreenExportBtn) {
      prescreenExportBtn.addEventListener('click', function () {
        Ui.run('產生預篩選報告', function () {
          return exportPrescreenReport(Store.getState().lastRuns.prescreen);
        }, { logCompletion: true });
      });
    }

    // 「載入更多」綁定:每顆鈕依 id 查 LOAD_MORE_SPECS 取 fetchPage + columns,
    // 接列目標 tbody 以 data-load-target=<第一個 id> 標記(同 tbody 可有多顆鈕)。
    // 純呼叫膠水:bindLoadMore 帶 cursor 發 query.*Page、把回傳列 append 進 tbody、到底移除鈕。
    container.querySelectorAll('[data-load-more]').forEach(function (btn) {
      var id = btn.getAttribute('data-load-more');
      var spec = LOAD_MORE_SPECS[id];
      if (!spec) { return; }
      var detail = btn.closest('.rule-detail');
      var tbody = detail ? detail.querySelector('[data-load-target]') : null;
      if (!tbody) { return; }
      // 單鈕的表走可排序、可搜尋的分頁表；空值表兩鈕共用 tbody，維持原本只接續的載入更多。
      if (detail.querySelectorAll('[data-load-more]').length === 1) {
        Ui.bindPagedTable(detail, {
          fetchPage: spec.fetchPage,
          appendRows: function (rows) { Ui.appendRowsToTbody(tbody, rows, spec.columns); },
          clearRows: function () { tbody.innerHTML = ''; },
          loadMore: btn,
          table: tbody.closest('table'),
          search: detail.querySelector('[data-page-search]')
        });
        return;
      }
      Ui.bindLoadMore(btn, spec.fetchPage, function (rows) {
        Ui.appendRowsToTbody(tbody, rows, spec.columns);
      }, function () {
        // 首擊清掉上方預覽列(ABS-DESC top-50 等),改接 keyset ASC 第一頁,避免重複與排序不一致。
        // 一格 tbody 兩鈕(空傳票號/科目)時只清一次:由先點的鈕清掉混合預覽,
        // 後點的鈕不再清(直接接自己那類),避免清掉前一鈕已載入的列。
        if (tbody.getAttribute('data-cleared') === '1') { return; }
        tbody.innerHTML = '';
        tbody.setAttribute('data-cleared', '1');
      });
    });

    // 「檢視 / 收合」切換。
    // 對 prescreenPreview 類型：首次展開時觸發惰性抓取；之後只切 hidden。
    // 其餘類型（reason / table）：純 DOM，無 API。
    container.querySelectorAll('[data-action="toggle-detail"]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var scope = btn.getAttribute('data-scope');
        var idx = btn.getAttribute('data-idx');
        var panel = container.querySelector('[data-bind="rule-detail-' + scope + '-' + idx + '"]');
        if (!panel) { return; }

        panel.hidden = !panel.hidden;
        btn.textContent = panel.hidden ? '檢視' : '收合';
        btn.classList.toggle('is-open', !panel.hidden);

        // 只在展開時、且面板含 prescreenPreview 子區塊時觸發惰性抓取。
        if (panel.hidden) { return; }

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
            function () { return global.JetApi.queryPrescreenPage({ ruleKey: key, pageSize: 50 }); },
            function (data) {
              bodyEl.innerHTML = prescreenPageHtml(data);
              bindPrescreenTable(block, key);
            });
        });
      });
    });

    if (!ready) { return; }

    container.querySelector('[data-action="run-validate"]').addEventListener('click', function () {
      Ui.run('執行驗證並產生報告', function () {
        return global.JetApi.validateRun({}).then(function (data) {
          Store.setLastRun('validate', data);
          Store.addMessage(
            '資料驗證完成：科目不符 ' + data.completenessTest.diffAccountCount + '、傳票不平 ' +
            data.docBalanceTest.unbalancedDocumentCount + '、抽樣 ' + data.infSamplingTest.sampleSize +
            ' 筆、異常項次合計 ' + (Number(data.nullRecordsTest.nullAccountCount) +
              Number(data.nullRecordsTest.nullDocumentCount) +
              Number(data.nullRecordsTest.nullDescriptionCount) +
              Number(data.nullRecordsTest.outOfRangeDateCount)) +
            ' 筆（同一分錄可能重複計入）、總帳日期空白 ' +
            Number(data.sourceQuality ? data.sourceQuality.findingCount : 0) +
            ' 筆（獨立計數，不與上一項相加）。', 'info');
          return exportValidationOutputs(data);
        });
      }, { logCompletion: true });
    });

    container.querySelector('[data-action="run-prescreen"]').addEventListener('click', function () {
      var eligibility = Ui.completenessEligibility(Store.getState().lastRuns.validate);
      if (!eligibility.isEligible) {
        Store.addMessage('已阻擋：' + eligibility.reason, 'warn');
        return;
      }
      Ui.run('執行預篩選', function () {
        return global.JetApi.prescreenRun({}).then(function (data) {
          Store.setLastRun('prescreen', data);
          Store.addMessage(
            '輔助訊號已更新：財報準備日起核准 ' + data.postPeriodApproval.count +
            '、摘要特定描述 ' + data.suspiciousKeywords.count +
            '、連續零尾數 ' + data.trailingZeros.count +
            '、週末過帳 ' + data.weekendActivity.postingCount +
            '、假日過帳 ' + data.holidayActivity.postingCount + ' 筆。', 'info');
          return data;
        });
      }, { logCompletion: true });
    });
  }

  Ui.registerStep('validate', render);
})(window);
