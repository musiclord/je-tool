/*
  流程總覽的母體事實區塊（BI）渲染器（JetOverviewBi namespace）。
  目前持有：預篩選規則全期命中分布、分錄金額級距與累積分布、
  集中度分析（編製人員分錄分布／較少使用之科目）。

  硬性邊界：本檔只鏡射後端 validate.run 的 amountDistribution，以及 prescreen.run
  的 rulePeriod／concentration 區塊並格式化。ECDF、符合比例、累積占比、「其他」彙總
  與前五佔比全部由後端算好；此處不得再做任何審計數字的加總、排序或百分比換算。
  ECharts 5.5.0 只負責以本地 SVG renderer 呈現 option，不接外網、不持有業務狀態。
*/
(function (global) {
  'use strict';

  var Ui = global.JetUi;
  var CHART_FONT = '"Noto Sans TC", "Microsoft JhengHei", sans-serif';
  var BAR_FILL = '#b08968';
  var BAR_HOVER = '#7a6033';
  var BAR_MANUAL_FILL = '#d8c3a5';
  var LINE_STROKE = '#1f6c9f';
  var DOT_FILL = '#7a6033';
  var AXIS_STROKE = '#c9c5b9';
  var GRID_STROKE = '#eceadf';
  var LABEL_FILL = '#6f6d64';
  var VALUE_FILL = '#4a4840';
  var PLACEHOLDER_FILL = '#a5a297';

  // validate.run 的 canonical key presentation mirror。順序、數量與 keys 由
  // AmountDistributionWireKeys guard 對齊後端；軸只顯示上界，完整區間進 tooltip。
  var AMOUNT_BIN_META = [
    { key: 'zero', tick: '零元', range: '零元' },
    { key: 'lt1k', tick: '1k', range: '<1k' },
    { key: '1k-2k', tick: '2k', range: '1–2k' },
    { key: '2k-5k', tick: '5k', range: '2–5k' },
    { key: '5k-10k', tick: '10k', range: '5–10k' },
    { key: '10k-20k', tick: '20k', range: '10–20k' },
    { key: '20k-50k', tick: '50k', range: '20–50k' },
    { key: '50k-100k', tick: '100k', range: '50–100k' },
    { key: '100k-200k', tick: '200k', range: '100–200k' },
    { key: '200k-500k', tick: '500k', range: '200–500k' },
    { key: '500k-1M', tick: '1M', range: '500k–1M' },
    { key: '1M-2M', tick: '2M', range: '1–2M' },
    { key: '2M-5M', tick: '5M', range: '2–5M' },
    { key: '5M-10M', tick: '10M', range: '5–10M' },
    { key: 'gt10M', tick: '>10M', range: '>10M' }
  ];

  var mountedCharts = [];
  var resizeObserver = typeof global.ResizeObserver === 'function'
    ? new global.ResizeObserver(function () { resizeCharts(); })
    : null;

  function fmtNum(n) {
    return (n == null || isNaN(n)) ? '—' : Number(n).toLocaleString('en-US');
  }

  // 圖軸標須有界；完整 count 仍保留在 ECharts tooltip。
  function fmtAxisCount(n) {
    var value = Number(n);
    if (isNaN(value)) { return '—'; }
    var magnitude = Math.abs(value);
    var scaled;
    var suffix;
    if (magnitude >= 1000000000) {
      scaled = value / 1000000000;
      suffix = 'B';
    } else if (magnitude >= 1000000) {
      scaled = value / 1000000;
      suffix = 'M';
    } else if (magnitude >= 1000) {
      scaled = value / 1000;
      suffix = 'k';
    } else {
      return String(value);
    }
    return scaled.toFixed(1).replace(/\.0$/, '') + suffix;
  }

  // 後端已給定一位小數的占比；分母為 0 時後端回 null，這裡顯示「—」而不是 0%。
  function fmtPct(pct) {
    return (pct == null) ? '—' : Number(pct).toFixed(1) + '%';
  }

  // S2b 的大母體符合比例可能遠低於 0.1%；專用自適應精度避免非零率被顯示成 0.0%。
  // 真正 0 固定顯示 0.0%，極小非零值在小數表示不足時才退回科學記號。
  function fmtRulePct(pct) {
    if (pct == null) { return '—'; }
    var value = Number(pct);
    if (isNaN(value)) { return '—'; }
    if (value === 0) { return '0.0%'; }

    var magnitude = Math.abs(value);
    if (magnitude >= 1) { return value.toFixed(1) + '%'; }
    if (magnitude >= 0.01) { return value.toFixed(2) + '%'; }
    if (magnitude >= 0.000001) {
      return value.toFixed(6).replace(/0+$/, '').replace(/\.$/, '') + '%';
    }
    return value.toExponential(2) + '%';
  }

  function clip(text, max) {
    var value = String(text == null ? '' : text);
    if (value.length <= max) { return value; }
    return value.slice(0, max - 1) + '…';
  }

  function tooltipContent(title, lines) {
    return '<div class="overview-bi-tooltip">' +
      '<strong class="overview-bi-tooltip__title">' + Ui.esc(title) + '</strong>' +
      lines.map(function (line) {
        return '<span class="overview-bi-tooltip__line">' + Ui.esc(line) + '</span>';
      }).join('') +
      '</div>';
  }

  function amountMeta(key) {
    for (var index = 0; index < AMOUNT_BIN_META.length; index++) {
      if (AMOUNT_BIN_META[index].key === key) { return AMOUNT_BIN_META[index]; }
    }
    return { key: key, tick: key, range: key };
  }

  function prescreenLabel(key) {
    var label = key;
    Ui.PRESCREEN_KEY_OPTIONS.forEach(function (option) {
      if (option.value === key) { label = option.label; }
    });
    return label;
  }

  function rulePeriodChartOption(rulePeriod) {
    // 不適用規則不進圖表；真正適用且零命中者仍以 0 值保留。
    var rules = rulePeriod.rules.filter(function (rule) { return rule.naReason == null; });
    var labels = rules.map(function (rule) { return prescreenLabel(rule.key); });
    var data = rules.map(function (rule) {
      return {
        value: rule.ratePct,
        key: rule.key,
        hitLines: rule.hitLines,
        hitVouchers: rule.hitVouchers,
        ratePct: rule.ratePct
      };
    });

    return {
      animation: false,
      textStyle: { fontFamily: CHART_FONT, color: VALUE_FILL },
      aria: {
        enabled: true,
        description: '適用的預篩選規則全查核期間符合比例分布；沒有符合分錄的項目仍顯示為零。'
      },
      tooltip: {
        trigger: 'axis',
        confine: true,
        axisPointer: { type: 'shadow' },
        formatter: function (params) {
          var item = params && params.length ? params[0].data : null;
          if (!item) { return ''; }
          var label = prescreenLabel(item.key);
          return tooltipContent(label, [
            '全查核期間',
            '符合條件的分錄 ' + fmtNum(item.hitLines) + ' 筆',
            '傳票 ' + fmtNum(item.hitVouchers) + ' 張（同號只算一張）',
            '納入測試 ' + fmtNum(rulePeriod.population) + ' 筆',
            '符合比例 ' + fmtRulePct(item.ratePct)
          ]);
        }
      },
      grid: { left: 198, right: 28, top: 20, bottom: 36 },
      xAxis: {
        type: 'value',
        min: 0,
        name: '符合比例',
        nameLocation: 'middle',
        nameGap: 28,
        nameTextStyle: { fontFamily: CHART_FONT, fontSize: 10, color: LABEL_FILL },
        axisLine: { show: true, lineStyle: { color: AXIS_STROKE } },
        axisTick: { show: false },
        splitLine: { show: true, lineStyle: { color: GRID_STROKE } },
        axisLabel: {
          color: LABEL_FILL,
          fontFamily: CHART_FONT,
          fontSize: 9.5,
          formatter: function (value) { return fmtRulePct(value); }
        }
      },
      yAxis: {
        type: 'category',
        inverse: true,
        data: labels,
        axisLine: { show: false },
        axisTick: { show: false },
        axisLabel: {
          interval: 0,
          margin: 12,
          align: 'right',
          color: LABEL_FILL,
          fontFamily: CHART_FONT,
          fontSize: 10.5,
          formatter: function (value) { return clip(value, 24); }
        }
      },
      series: [{
        name: '符合比例',
        type: 'bar',
        barWidth: 12,
        data: data,
        itemStyle: { color: BAR_FILL },
        emphasis: { itemStyle: { color: BAR_HOVER } }
      }]
    };
  }

  function amountDistributionChartOption(amountDistribution) {
    var bins = amountDistribution.bins;
    var ticks = bins.map(function (bin) { return amountMeta(bin.key).tick; });
    var countData = bins.map(function (bin) {
      return {
        value: bin.count,
        key: bin.key,
        count: bin.count,
        ecdfPct: bin.ecdfPct
      };
    });
    var ecdfData = bins.map(function (bin) {
      return bin.ecdfPct == null ? null : bin.ecdfPct;
    });

    return {
      animation: false,
      textStyle: { fontFamily: CHART_FONT, color: VALUE_FILL },
      aria: {
        enabled: true,
        description: '分錄金額級距與累積分布；零元另計，累積分布以非零元分錄為分母。'
      },
      tooltip: {
        trigger: 'axis',
        confine: true,
        axisPointer: { type: 'shadow' },
        formatter: function (params) {
          var bar = null;
          (params || []).forEach(function (param) {
            if (param.seriesName === '分錄筆數') { bar = param; }
          });
          if (!bar || !bar.data) { return ''; }
          var meta = amountMeta(bar.data.key);
          return tooltipContent('分錄金額級距與累積分布，' + meta.range, [
            '分錄 ' + fmtNum(bar.data.count) + ' 筆',
            '累積分布 ' + fmtPct(bar.data.ecdfPct)
          ]);
        }
      },
      legend: { show: false },
      grid: { left: 54, right: 58, top: 24, bottom: 76 },
      xAxis: {
        type: 'category',
        data: ticks,
        axisLine: { lineStyle: { color: AXIS_STROKE } },
        axisTick: { alignWithLabel: true, lineStyle: { color: AXIS_STROKE } },
        axisLabel: {
          interval: 0,
          rotate: 45,
          color: LABEL_FILL,
          fontFamily: CHART_FONT,
          fontSize: 9.5
        }
      },
      yAxis: [
        {
          type: 'value',
          name: '分錄筆數',
          min: 0,
          nameTextStyle: { color: LABEL_FILL, fontFamily: CHART_FONT, fontSize: 10 },
          axisLine: { show: true, lineStyle: { color: AXIS_STROKE } },
          axisTick: { show: false },
          splitLine: { show: true, lineStyle: { color: GRID_STROKE } },
          axisLabel: {
            color: LABEL_FILL,
            fontFamily: CHART_FONT,
            fontSize: 9.5,
            formatter: fmtAxisCount
          }
        },
        {
          type: 'value',
          name: '累積分布',
          min: 0,
          max: 100,
          nameTextStyle: { color: LINE_STROKE, fontFamily: CHART_FONT, fontSize: 10 },
          axisLine: { show: true, lineStyle: { color: AXIS_STROKE } },
          axisTick: { show: false },
          splitLine: { show: false },
          axisLabel: {
            color: LABEL_FILL,
            fontFamily: CHART_FONT,
            fontSize: 9.5,
            formatter: function (value) { return value + '%'; }
          }
        }
      ],
      series: [
        {
          name: '分錄筆數',
          type: 'bar',
          yAxisIndex: 0,
          barWidth: '62%',
          data: countData,
          itemStyle: { color: BAR_FILL },
          emphasis: { itemStyle: { color: BAR_HOVER } }
        },
        {
          name: '累積分布',
          type: 'line',
          yAxisIndex: 1,
          data: ecdfData,
          connectNulls: false,
          symbol: 'circle',
          symbolSize: 5,
          lineStyle: { color: LINE_STROKE, width: 2 },
          itemStyle: { color: LINE_STROKE }
        }
      ]
    };
  }

  function preparerChartOption(preparers) {
    var rows = preparers.top.map(function (row) {
      return {
        label: row.createdBy == null || !String(row.createdBy).trim() ? '（空白）' : row.createdBy,
        entryCount: row.entryCount,
        manualCount: row.manualCount,
        cumulativePct: row.cumulativePct,
        aggregated: false
      };
    });
    if (preparers.othersEntryCount > 0) {
      rows.push({
        label: '其他',
        entryCount: preparers.othersEntryCount,
        manualCount: null,
        cumulativePct: null,
        aggregated: true
      });
    }

    return {
      animation: false,
      textStyle: { fontFamily: CHART_FONT, color: VALUE_FILL },
      aria: {
        enabled: true,
        description: '編製人員分錄筆數、人工分錄與累積占比分布。'
      },
      tooltip: {
        trigger: 'axis',
        confine: true,
        axisPointer: { type: 'shadow' },
        formatter: function (params) {
          if (!params || !params.length) { return ''; }
          var row = rows[params[0].dataIndex];
          var lines = ['分錄 ' + fmtNum(row.entryCount) + ' 筆'];
          if (row.manualCount != null) {
            lines.push('其中人工分錄 ' + fmtNum(row.manualCount) + ' 筆');
          }
          if (row.cumulativePct != null) {
            lines.push('累積占比 ' + fmtPct(row.cumulativePct));
          }
          if (row.aggregated) {
            lines.push('「其他」為其餘編製人員的合計');
          }
          return tooltipContent(row.label, lines);
        }
      },
      grid: { left: 54, right: 56, top: 28, bottom: 72 },
      xAxis: {
        type: 'category',
        data: rows.map(function (row) { return row.label; }),
        axisLine: { lineStyle: { color: AXIS_STROKE } },
        axisTick: { alignWithLabel: true, lineStyle: { color: AXIS_STROKE } },
        axisLabel: {
          interval: 0,
          rotate: rows.length > 7 ? 35 : 0,
          color: LABEL_FILL,
          fontFamily: CHART_FONT,
          fontSize: 10,
          formatter: function (value) { return clip(value, 10); }
        }
      },
      yAxis: [
        {
          type: 'value',
          name: '分錄筆數',
          min: 0,
          nameTextStyle: { color: LABEL_FILL, fontFamily: CHART_FONT, fontSize: 10 },
          axisLine: { show: true, lineStyle: { color: AXIS_STROKE } },
          axisTick: { show: false },
          splitLine: { show: true, lineStyle: { color: GRID_STROKE } },
          axisLabel: {
            color: LABEL_FILL,
            fontFamily: CHART_FONT,
            fontSize: 9.5,
            formatter: fmtAxisCount
          }
        },
        {
          type: 'value',
          name: '累積占比',
          min: 0,
          max: 100,
          nameTextStyle: { color: LINE_STROKE, fontFamily: CHART_FONT, fontSize: 10 },
          axisLine: { show: true, lineStyle: { color: AXIS_STROKE } },
          axisTick: { show: false },
          splitLine: { show: false },
          axisLabel: {
            color: LABEL_FILL,
            fontFamily: CHART_FONT,
            fontSize: 9.5,
            formatter: function (value) { return value + '%'; }
          }
        }
      ],
      series: [
        {
          name: '分錄筆數',
          type: 'bar',
          yAxisIndex: 0,
          barWidth: '52%',
          data: rows.map(function (row) { return row.entryCount; }),
          itemStyle: { color: BAR_FILL },
          emphasis: { itemStyle: { color: BAR_HOVER } }
        },
        {
          name: '其中人工分錄',
          type: 'bar',
          yAxisIndex: 0,
          barWidth: '26%',
          barGap: '-78%',
          data: rows.map(function (row) { return row.manualCount; }),
          itemStyle: { color: BAR_MANUAL_FILL }
        },
        {
          name: '累積占比',
          type: 'line',
          yAxisIndex: 1,
          data: rows.map(function (row) { return row.cumulativePct; }),
          connectNulls: false,
          symbol: 'circle',
          symbolSize: 5,
          lineStyle: { color: LINE_STROKE, width: 1.75 },
          itemStyle: { color: LINE_STROKE }
        }
      ]
    };
  }

  function rareAccountChartOption(accounts) {
    var labels = accounts.map(function (account) {
      return account.accountCode + (account.accountName ? ' ' + account.accountName : '');
    });
    var data = accounts.map(function (account, index) {
      return {
        value: [account.entryCount, labels[index]],
        accountCode: account.accountCode,
        accountName: account.accountName,
        entryCount: account.entryCount
      };
    });

    return {
      animation: false,
      textStyle: { fontFamily: CHART_FONT, color: VALUE_FILL },
      aria: {
        enabled: true,
        description: '使用次數最低的科目分布，依使用次數由少至多呈現。'
      },
      tooltip: {
        trigger: 'item',
        confine: true,
        formatter: function (param) {
          var item = param.data;
          var label = item.accountCode + (item.accountName ? ' ' + item.accountName : '');
          return tooltipContent(label, ['使用 ' + fmtNum(item.entryCount) + ' 筆']);
        }
      },
      grid: { left: 218, right: 42, top: 18, bottom: 38 },
      xAxis: {
        type: 'value',
        min: 0,
        name: '使用次數',
        nameLocation: 'middle',
        nameGap: 28,
        nameTextStyle: { color: LABEL_FILL, fontFamily: CHART_FONT, fontSize: 10 },
        axisLine: { show: true, lineStyle: { color: AXIS_STROKE } },
        axisTick: { show: false },
        splitLine: { show: true, lineStyle: { color: GRID_STROKE } },
        axisLabel: { color: LABEL_FILL, fontFamily: CHART_FONT, fontSize: 9.5 }
      },
      yAxis: {
        type: 'category',
        inverse: true,
        data: labels,
        axisLine: { show: false },
        axisTick: { show: false },
        axisLabel: {
          interval: 0,
          color: LABEL_FILL,
          fontFamily: CHART_FONT,
          fontSize: 10.5,
          formatter: function (value) { return clip(value, 24); }
        }
      },
      series: [{
        name: '使用次數',
        type: 'scatter',
        data: data,
        symbolSize: 9,
        itemStyle: { color: DOT_FILL },
        emphasis: { itemStyle: { color: '#d64422' } }
      }]
    };
  }

  function chartContainerHtml(kind, modifier, label) {
    return '<div class="overview-bi__chart overview-bi__chart--' + modifier +
      '" data-overview-chart="' + kind + '" role="img" aria-label="' +
      Ui.esc(label) + '"></div>';
  }

  function tabsHtml(activeTab) {
    return [
      { key: 'preparers', label: '編製人員分錄分布' },
      { key: 'accounts', label: '較少使用之科目' }
    ].map(function (tab) {
      var active = tab.key === activeTab;
      return '<button type="button" class="overview-bi__tab' +
        (active ? ' overview-bi__tab--active' : '') + '" data-overview-tab="' + tab.key +
        '" id="overview-concentration-tab-' + tab.key + '" role="tab" ' +
        'aria-controls="overview-concentration-panel" aria-selected="' +
        (active ? 'true' : 'false') + '" tabindex="' + (active ? '0' : '-1') + '">' +
        Ui.esc(tab.label) + '</button>';
    }).join('');
  }

  function sectionHtml(inner, tabs, activeTab) {
    var content = tabs
      ? '<div class="overview-bi__tabs" role="tablist" aria-label="集中度檢視">' + tabs + '</div>' +
        '<div id="overview-concentration-panel" role="tabpanel" tabindex="0" ' +
          'aria-labelledby="overview-concentration-tab-' + activeTab + '">' + inner + '</div>'
      : inner;
    return (
      '<details class="overview__bi overview__analysis" data-overview-analysis="concentration">' +
        '<summary class="overview__analysis-summary">' +
          '<span>預篩選</span><span class="overview__analysis-hint">展開</span>' +
        '</summary>' +
        '<div class="overview__analysis-content" aria-label="預篩選">' +
          content +
        '</div>' +
      '</details>'
    );
  }

  function messageHtml(modifier, title, body) {
    return (
      '<div class="overview-bi__message overview-bi__message--' + modifier + '">' +
        '<span class="overview-bi__message-title">' + Ui.esc(title) + '</span>' +
        '<p class="overview-bi__message-body">' + Ui.esc(body) + '</p>' +
      '</div>'
    );
  }

  function rulePeriodSectionHtml(inner) {
    return (
      '<details class="overview__bi overview__bi--rule-period overview__analysis" data-overview-analysis="rule-period">' +
        '<summary class="overview__analysis-summary">' +
          '<span>預篩選結果分布</span>' +
          '<span class="overview__analysis-hint">展開</span>' +
        '</summary>' +
        '<div class="overview__analysis-content" aria-label="預篩選結果分布">' +
          inner +
        '</div>' +
      '</details>'
    );
  }

  function rulePeriodNaHtml(rulePeriod) {
    var rows = rulePeriod.rules.filter(function (rule) { return rule.naReason != null; });
    if (rows.length === 0) { return ''; }
    return (
      '<div class="overview-bi__na-list" aria-label="不適用規則">' +
        '<span class="overview-bi__na-title">不適用規則</span>' +
        rows.map(function (rule) {
          return '<div class="overview-bi__na-row"><span>' + Ui.esc(prescreenLabel(rule.key)) +
            '</span><span>' + Ui.esc(rule.naReason) + '</span></div>';
        }).join('') +
      '</div>'
    );
  }

  function rulePeriodPanelHtml(rulePeriod) {
    var applicable = rulePeriod.rules.filter(function (rule) { return rule.naReason == null; });
    var zeroPopulationNote = rulePeriod.population === 0
      ? '納入測試的分錄為 0，符合比例以「—」表示，不代表 0%。'
      : '';
    var note = '符合比例＝符合條件的分錄數 ÷ 納入測試的分錄 ' + fmtNum(rulePeriod.population) +
      ' 筆。長條依各規則符合比例呈現，僅供比較規則間分布。' +
      zeroPopulationNote + '此為分布描述，非風險評估。';

    return (
      (applicable.length > 0
        ? '<div class="overview-bi__body">' +
            chartContainerHtml('rule-period', 'rule-period', '適用的預篩選規則全期符合比例分布') +
          '</div>'
        : messageHtml('na', '沒有可繪製的適用規則', '不適用規則與原因列於下方。')) +
      rulePeriodNaHtml(rulePeriod) +
      '<p class="overview-bi__note">' + Ui.esc(note) + '</p>'
    );
  }

  /**
   * 預篩選規則全期命中分布。舊 summary 沒有 rulePeriod 時必須誠實降級，
   * 不可由既有 count 在前端自行補算分母、去重傳票或符合比例。
   */
  function rulePeriodHtml(prescreen) {
    if (!prescreen) {
      return rulePeriodSectionHtml(messageHtml(
        'empty',
        '尚未執行預篩選（選用）',
        '在「資料驗證與測試」步驟執行預篩選後，這裡會顯示預篩選的全期結果分布。'));
    }

    var rulePeriod = prescreen.rulePeriod;
    if (!rulePeriod || !Array.isArray(rulePeriod.rules) || rulePeriod.rules.length === 0) {
      return rulePeriodSectionHtml(messageHtml(
        'stale',
        '需重新執行預篩選以產生此統計',
        '這次預篩選結果沒有預篩選結果分布，重新執行預篩選即可查看。'));
    }

    return rulePeriodSectionHtml(rulePeriodPanelHtml(rulePeriod));
  }

  function amountDistributionSectionHtml(inner) {
    return (
      '<details class="overview__bi overview__bi--amount overview__analysis" data-overview-analysis="amount">' +
        '<summary class="overview__analysis-summary">' +
          '<span>分錄金額級距與累積分布</span>' +
          '<span class="overview__analysis-hint">展開</span>' +
        '</summary>' +
        '<div class="overview__analysis-content" aria-label="分錄金額級距與累積分布">' +
          inner +
        '</div>' +
      '</details>'
    );
  }

  function amountDistributionPanelHtml(amountDistribution) {
    return (
      '<div class="overview-bi__body">' +
        chartContainerHtml('amount', 'amount', '分錄金額級距與累積分布') +
      '</div>' +
      '<p class="overview-bi__legend">' +
        '<span class="overview-bi__key overview-bi__key--bar"></span>分錄筆數' +
        '<span class="overview-bi__key overview-bi__key--line"></span>累積分布' +
      '</p>' +
      '<p class="overview-bi__note">' +
        'X 軸為絕對金額的 1–2–5 對數級距；零元另計、不納入對數軸。' +
        '累積分布由系統計算，並以納入測試的非零金額分錄為分母；沒有累積比例的級距不連線。' +
        '完整區間請查看圖形提示。未設重要性門檻線。此為分布描述，非風險評估。' +
      '</p>'
    );
  }

  /**
   * validate.run 金額級距。舊 summary 沒有 amountDistribution 時必須要求重跑，
   * 不得從 stats 或其他畫面數字在前端補算。
   */
  function amountDistributionHtml(validation) {
    var amountDistribution = validation && validation.amountDistribution;
    if (!amountDistribution ||
        !Array.isArray(amountDistribution.bins) ||
        amountDistribution.bins.length !== AMOUNT_BIN_META.length) {
      return amountDistributionSectionHtml(messageHtml(
        'stale',
        '需重新執行資料驗證以產生此統計',
        '目前顯示的是先前執行結果，尚未包含金額級距與累積分布。重新執行資料驗證即可產生。'));
    }

    return amountDistributionSectionHtml(
      amountDistributionPanelHtml(amountDistribution));
  }

  function preparerPanelHtml(concentration) {
    var preparers = concentration.preparers;
    var denominator = '累積占比以查核期間全部 ' + fmtNum(preparers.totalPreparerCount) +
      ' 位編製人員、' + fmtNum(preparers.totalEntryCount) + ' 筆分錄為分母。';
    var lead = preparers.top5SharePct == null
      ? denominator
      : '前 5 位編製人員占查核期間分錄 ' + fmtPct(preparers.top5SharePct) + '。' + denominator;

    return (
      '<div class="overview-bi__body">' +
        chartContainerHtml('concentration', 'concentration', '編製人員分錄筆數與累積占比分布') +
      '</div>' +
      '<p class="overview-bi__legend">' +
        '<span class="overview-bi__key overview-bi__key--bar"></span>分錄筆數' +
        '<span class="overview-bi__key overview-bi__key--manual"></span>其中人工分錄' +
        '<span class="overview-bi__key overview-bi__key--line"></span>累積占比' +
      '</p>' +
      '<p class="overview-bi__note">' + Ui.esc(lead) + '此為分布描述，非風險評估。</p>'
    );
  }

  function rareAccountPanelHtml(concentration) {
    var accounts = concentration.rareAccounts;
    var note = '以下為使用次數最低的 ' + fmtNum(accounts.length) + ' 個科目（升冪）；' +
      '查核期間共 ' + fmtNum(concentration.distinctAccountCount) + ' 個科目。' +
      '本圖聚焦使用次數最低的科目，不呈現高頻科目的集中程度。';

    return (
      '<div class="overview-bi__body">' +
        chartContainerHtml(
          'concentration',
          'concentration overview-bi__chart--rare-accounts',
          '使用次數最低的科目分布') +
      '</div>' +
      '<p class="overview-bi__note">' + Ui.esc(note) + '</p>'
    );
  }

  /**
   * 集中度分析區塊。prescreen＝state.lastRuns.prescreen（可為 null）。
   * 三種降級一律以文字說明取代圖表，不以 0 冒充統計：
   *   1) 尚未執行風險預篩選；2) 舊執行結果沒有此統計；3) 後端裁定不適用。
   */
  function concentrationHtml(prescreen, activeTab) {
    if (!prescreen) {
      return sectionHtml(messageHtml(
        'empty',
        '尚未執行預篩選（選用）',
        '在「資料驗證與測試」步驟執行預篩選後，這裡會顯示編製人員分錄分布與較少使用之科目。'), '');
    }

    var concentration = prescreen.concentration;
    if (!concentration) {
      return sectionHtml(messageHtml(
        'stale',
        '需重新執行預篩選以產生此統計',
        '目前顯示的是先前執行結果，尚未包含預篩選。重新執行預篩選即可產生。'), '');
    }

    if (concentration.status !== 'V') {
      return sectionHtml(messageHtml(
        'na',
        '不適用',
        concentration.naReason ||
          '查核期間沒有可彙總的分錄，因此無法產生預篩選。「不適用」與結果為 0 語意不同。'), '');
    }

    var tab = activeTab === 'accounts' ? 'accounts' : 'preparers';
    var panel = tab === 'accounts'
      ? rareAccountPanelHtml(concentration)
      : preparerPanelHtml(concentration);
    return sectionHtml(panel, tabsHtml(tab), tab);
  }

  function chartOption(kind, validation, prescreen, activeTab) {
    if (kind === 'rule-period' && prescreen && prescreen.rulePeriod) {
      return rulePeriodChartOption(prescreen.rulePeriod);
    }
    if (kind === 'amount' && validation && validation.amountDistribution) {
      return amountDistributionChartOption(validation.amountDistribution);
    }
    if (kind === 'concentration' && prescreen && prescreen.concentration) {
      return activeTab === 'accounts'
        ? rareAccountChartOption(prescreen.concentration.rareAccounts)
        : preparerChartOption(prescreen.concentration.preparers);
    }
    return null;
  }

  function resizeCharts() {
    mountedCharts.forEach(function (entry) {
      if (!entry.chart.isDisposed()) { entry.chart.resize(); }
    });
  }

  function dispose() {
    if (resizeObserver) { resizeObserver.disconnect(); }
    mountedCharts.forEach(function (entry) {
      if (!entry.chart.isDisposed()) { entry.chart.dispose(); }
    });
    mountedCharts = [];
  }

  function disposeWithin(root) {
    if (!root) { return; }
    mountedCharts = mountedCharts.filter(function (entry) {
      if (!root.contains(entry.element)) { return true; }
      if (resizeObserver) { resizeObserver.unobserve(entry.element); }
      if (!entry.chart.isDisposed()) { entry.chart.dispose(); }
      return false;
    });
  }

  function mount(root, validation, prescreen, activeTab) {
    if (!root) { return; }
    var elements = root.querySelectorAll('[data-overview-chart]');
    elements.forEach(function (element) {
      var section = element.closest('details');
      if (section && !section.open) { return; }
      var alreadyMounted = mountedCharts.some(function (entry) { return entry.element === element; });
      if (alreadyMounted) { return; }
      var kind = element.getAttribute('data-overview-chart');
      var option = chartOption(kind, validation, prescreen, activeTab);
      if (!option) { return; }
      if (!global.echarts || typeof global.echarts.init !== 'function') {
        element.textContent = '圖表資產載入失敗；請重新啟動 JET。';
        element.classList.add('overview-bi__chart--error');
        return;
      }

      var chart = global.echarts.init(element, null, { renderer: 'svg' });
      chart.setOption(option, true);
      mountedCharts.push({ element: element, chart: chart });
      if (resizeObserver) { resizeObserver.observe(element); }
    });

    setTimeout(resizeCharts, 30);
    if (global.document && global.document.fonts && global.document.fonts.ready) {
      global.document.fonts.ready.then(resizeCharts);
    }
  }

  global.addEventListener('resize', resizeCharts);

  global.JetOverviewBi = {
    rulePeriodHtml: rulePeriodHtml,
    amountDistributionHtml: amountDistributionHtml,
    concentrationHtml: concentrationHtml,
    mount: mount,
    dispose: dispose,
    disposeWithin: disposeWithin,
    resize: resizeCharts,
    options: {
      rulePeriod: rulePeriodChartOption,
      amountDistribution: amountDistributionChartOption,
      preparers: preparerChartOption,
      rareAccounts: rareAccountChartOption
    }
  };
})(window);
