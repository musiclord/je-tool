'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const solutionRoot = path.resolve(__dirname, '..', '..', '..');
const modulePath = path.join(solutionRoot, 'JET', 'wwwroot', 'js', 'overview-bi.js');
const source = fs.readFileSync(modulePath, 'utf8');

const resizeListeners = [];
const timeoutCalls = [];
const observerEvents = [];
const chartEvents = [];

function escapeHtml(value) {
  return String(value == null ? '' : value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

class FakeResizeObserver {
  constructor(callback) {
    this.callback = callback;
    observerEvents.push({ type: 'create' });
  }

  observe(element) {
    observerEvents.push({ type: 'observe', element });
  }

  disconnect() {
    observerEvents.push({ type: 'disconnect' });
  }
}

const windowStub = {
  JetUi: {
    esc: escapeHtml,
    PRESCREEN_KEY_OPTIONS: [
      { value: 'completeness_test', label: '完整性測試' },
      { value: 'post_period_approval', label: '期後核准' },
      { value: 'manual_entry', label: '人工分錄' }
    ]
  },
  ResizeObserver: FakeResizeObserver,
  addEventListener(eventName, callback) {
    resizeListeners.push({ eventName, callback });
  },
  echarts: {
    init(element, theme, initOptions) {
      const event = {
        element,
        theme,
        initOptions,
        disposed: false,
        options: [],
        resizeCount: 0
      };
      chartEvents.push(event);
      return {
        setOption(option, notMerge) {
          event.options.push({ option, notMerge });
        },
        resize() {
          event.resizeCount += 1;
        },
        dispose() {
          event.disposed = true;
        },
        isDisposed() {
          return event.disposed;
        }
      };
    }
  }
};
windowStub.window = windowStub;

vm.runInNewContext(source, {
  window: windowStub,
  setTimeout(callback, delay) {
    timeoutCalls.push({ callback, delay });
  }
}, { filename: modulePath });

const options = windowStub.JetOverviewBi.options;

const rulePeriod = {
  population: 1_000,
  rules: [
    {
      key: 'completeness_test',
      naReason: '缺少 <欄位>',
      hitLines: null,
      hitVouchers: null,
      ratePct: null
    },
    {
      key: 'post_period_approval',
      naReason: null,
      hitLines: 0,
      hitVouchers: 0,
      ratePct: 0
    },
    {
      key: 'manual_entry',
      naReason: null,
      hitLines: 3,
      hitVouchers: 2,
      ratePct: 0.3
    }
  ]
};
const amountDistribution = {
  bins: [
    'zero', 'lt1k', '1k-2k', '2k-5k', '5k-10k',
    '10k-20k', '20k-50k', '50k-100k', '100k-200k',
    '200k-500k', '500k-1M', '1M-2M', '2M-5M',
    '5M-10M', 'gt10M'
  ].map((key, index) => ({
    key,
    count: index,
    ecdfPct: index === 0 || index === 7 ? null : index * 7.1
  }))
};
const preparers = {
  top: [
    { createdBy: '甲<&', entryCount: 20, manualCount: 3, cumulativePct: 40 },
    { createdBy: '乙', entryCount: 15, manualCount: 2, cumulativePct: 70 }
  ],
  othersEntryCount: 10,
  totalPreparerCount: 8,
  totalEntryCount: 45,
  top5SharePct: 90
};
const rareAccounts = [
  { accountCode: '9002', accountName: '第二', entryCount: 2 },
  { accountCode: '9001', accountName: '第一', entryCount: 1 }
];

const inputSnapshot = JSON.stringify({
  rulePeriod,
  amountDistribution,
  preparers,
  rareAccounts
});
const ruleOption = options.rulePeriod(rulePeriod);
const amountOption = options.amountDistribution(amountDistribution);
const preparerOption = options.preparers(preparers);
const rareOption = options.rareAccounts(rareAccounts);

assert.equal(JSON.stringify({
  rulePeriod,
  amountDistribution,
  preparers,
  rareAccounts
}), inputSnapshot, 'option builders must not mutate backend DTOs');

[ruleOption, amountOption, preparerOption, rareOption].forEach((option) => {
  assert.equal(option.animation, false);
  assert.equal(option.tooltip.confine, true);
});

assert.deepEqual(
  Array.from(ruleOption.series[0].data, row => row.value),
  [null, 0, 0.3],
  'N/A, valid zero, and non-zero rule rates must remain distinct'
);
assert.match(
  ruleOption.tooltip.formatter([{ data: ruleOption.series[0].data[0] }]),
  /缺少 &lt;欄位&gt;/
);
assert.match(
  ruleOption.tooltip.formatter([{ data: ruleOption.series[0].data[2] }]),
  /母體 1,000 筆/
);

assert.equal(amountOption.series[0].type, 'bar');
assert.equal(amountOption.series[1].type, 'line');
assert.equal(amountOption.series[1].connectNulls, false);
assert.equal(amountOption.series[1].data.length, 15);
assert.equal(amountOption.series[1].data[0], null);
assert.equal(amountOption.series[1].data[7], null);
assert.equal(amountOption.series[0].data[14].value, 14);
assert.match(
  amountOption.tooltip.formatter([{
    seriesName: '分錄筆數',
    data: amountOption.series[0].data[14]
  }]),
  /&gt;10M/
);

assert.equal(preparerOption.series[0].type, 'bar');
assert.equal(preparerOption.series[1].type, 'bar');
assert.equal(preparerOption.series[2].type, 'line');
assert.deepEqual(Array.from(preparerOption.series[1].data), [3, 2, null]);
assert.deepEqual(Array.from(preparerOption.series[2].data), [40, 70, null]);
assert.match(
  preparerOption.tooltip.formatter([{ dataIndex: 0 }]),
  /甲&lt;&amp;/
);

assert.equal(rareOption.series[0].type, 'scatter');
assert.deepEqual(
  Array.from(rareOption.yAxis.data),
  ['9002 第二', '9001 第一'],
  'rare-account order must remain the backend order'
);

function fakeElement(kind) {
  return {
    kind,
    textContent: '',
    classList: { add() {} },
    getAttribute(name) {
      return name === 'data-overview-chart' ? kind : null;
    }
  };
}

const elements = [
  fakeElement('rule-period'),
  fakeElement('amount'),
  fakeElement('concentration')
];
const root = {
  querySelectorAll(selector) {
    assert.equal(selector, '[data-overview-chart]');
    return elements;
  }
};

windowStub.JetOverviewBi.mount(
  root,
  { amountDistribution },
  { rulePeriod, concentration: { preparers, rareAccounts } },
  'preparers'
);

assert.equal(resizeListeners.length, 1);
assert.equal(resizeListeners[0].eventName, 'resize');
assert.equal(chartEvents.length, 3);
chartEvents.forEach((event) => {
  assert.equal(event.initOptions.renderer, 'svg');
  assert.deepEqual(Object.keys(event.initOptions), ['renderer']);
  assert.equal(event.options.length, 1);
  assert.equal(event.options[0].notMerge, true);
});
assert.equal(observerEvents.filter(event => event.type === 'observe').length, 3);
assert.equal(timeoutCalls.length, 1);
assert.equal(timeoutCalls[0].delay, 30);

timeoutCalls[0].callback();
resizeListeners[0].callback();
chartEvents.forEach(event => assert.equal(event.resizeCount, 2));

windowStub.JetOverviewBi.dispose();
chartEvents.forEach(event => assert.equal(event.disposed, true));
assert.equal(observerEvents.at(-1).type, 'disconnect');

console.log('S7 ECharts option/lifecycle assertions: 54 passed');
