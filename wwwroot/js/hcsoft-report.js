(function (root, factory) {
    const api = factory();

    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    }

    if (root) {
        root.hcsoftReportRenderer = api;
    }
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
    'use strict';

    const REPORT_DATA_MARKER_PATTERN =
        /<script\b[^>]*\bid\s*=\s*(["'])report-data\1[^>]*>/i;
    const RUNTIME_MARKER = 'data-hcsoft-report-runtime="pagination-v1"';
    const ANALYSIS_RUNTIME_MARKER =
        'data-hcsoft-report-runtime="analysis-facts-v1"';
    const CHART_RUNTIME_MARKER =
        'data-hcsoft-chart-runtime="local-fallback-v1"';
    const CHART_LAYOUT_RUNTIME_MARKER =
        'data-hcsoft-chart-runtime="layout-recovery-v1"';
    const CHART_EXTERNAL_SCRIPT_PATTERN =
        /<script\b(?=[^>]*\bsrc\s*=)[^>]*\bsrc\s*=\s*(["'])[^"']*(?:chart\.js@|chart(?:\.umd)?(?:\.min)?\.js)[^"']*\1[^>]*>\s*<\/script\s*>/i;

    const paginationRuntimeSource = `
(function (global, document) {
    'use strict';

    var existing = global.__hcsoftReportPagination;
    if (existing && typeof existing.install === 'function') {
        existing.install();
        return;
    }

    function positiveInteger(value, fallback) {
        var number = parseInt(value, 10);
        return Number.isFinite(number) && number > 0 ? number : fallback;
    }

    function suffixOf(tableId) {
        return String(tableId || '').replace(/^tbl/i, '');
    }

    function pageSizeOf(tableId, fallback) {
        var suffix = suffixOf(tableId);
        var selector =
            document.getElementById('topn' + suffix) ||
            document.getElementById('page-size');
        return positiveInteger(selector && selector.value, positiveInteger(fallback, 20));
    }

    function rowsOf(table) {
        return Array.prototype.slice.call(table.querySelectorAll('tbody tr'));
    }

    function paginationOf(tableId) {
        return document.getElementById('pg' + suffixOf(tableId));
    }

    function paginateTable(tableId, page, pageSize) {
        var table = document.getElementById(tableId);
        if (!table) return;

        var allRows = rowsOf(table);
        var matchedRows = allRows.filter(function (row) {
            return row.getAttribute('data-hcsoft-filter-match') !== '0';
        });
        var size = pageSizeOf(tableId, pageSize);
        var totalPages = Math.max(1, Math.ceil(matchedRows.length / size));
        var currentPage = Math.min(positiveInteger(page, 1), totalPages);
        var start = (currentPage - 1) * size;
        var end = start + size;

        allRows.forEach(function (row) {
            row.style.display = 'none';
        });
        matchedRows.forEach(function (row, index) {
            row.style.display = index >= start && index < end ? '' : 'none';
        });

        var pagination = paginationOf(tableId);
        if (!pagination) return;

        pagination.textContent = '';
        for (var number = 1; number <= totalPages; number += 1) {
            var button = document.createElement('button');
            button.type = 'button';
            button.textContent = String(number);
            if (number === currentPage) button.classList.add('active');
            button.addEventListener('click', (function (targetPage) {
                return function () {
                    paginateTable(tableId, targetPage, size);
                };
            })(number));
            pagination.appendChild(button);
        }
    }

    function filterTable(tableId, query) {
        var table = document.getElementById(tableId);
        if (!table) return;

        var normalized = String(query || '').toLocaleLowerCase().trim();
        rowsOf(table).forEach(function (row) {
            var searchable =
                row.getAttribute('data-search') ||
                row.textContent ||
                '';
            var matched =
                normalized === '' ||
                searchable.toLocaleLowerCase().indexOf(normalized) >= 0;
            row.setAttribute('data-hcsoft-filter-match', matched ? '1' : '0');
        });
        paginateTable(tableId, 1, pageSizeOf(tableId, 20));
    }

    function refresh() {
        Array.prototype.slice.call(document.querySelectorAll('table[id]'))
            .forEach(function (table) {
                if (!paginationOf(table.id)) return;
                paginateTable(table.id, 1, pageSizeOf(table.id, 20));
            });
    }

    function install() {
        global.paginateTable = paginateTable;
        global.filterTable = filterTable;
    }

    global.__hcsoftReportPagination = {
        version: 1,
        install: install,
        refresh: refresh
    };
    install();
})(window, document);
`.trim();

    const paginationBootstrap =
        `<script ${RUNTIME_MARKER}>\n${paginationRuntimeSource}\n<\/script>`;
    const paginationRestore =
        `<script data-hcsoft-report-runtime="pagination-restore-v1">` +
        `(function(w){var r=w.__hcsoftReportPagination;` +
        `if(r){r.install();r.refresh();}})(window);<\/script>`;

    function insertBeforeClosingTag(html, tagName, content) {
        const pattern = new RegExp(`</${tagName}\\s*>`, 'i');
        const match = pattern.exec(html);
        if (match) {
            return (
                html.slice(0, match.index) +
                content +
                '\n' +
                html.slice(match.index)
            );
        }

        if (tagName.toLowerCase() === 'head') {
            const firstScript = /<script\b/i.exec(html);
            if (firstScript) {
                return (
                    html.slice(0, firstScript.index) +
                    content +
                    '\n' +
                    html.slice(firstScript.index)
                );
            }
        }

        return content + '\n' + html;
    }

    /**
     * 为模型生成的 Chart.js CDN 标签追加同源后备脚本。
     *
     * CDN 被 WebView、浏览器扩展或网络策略拦截时，静态 CDN 标签执行完毕
     * 但 window.Chart 仍为空；旧报告也可能引用未在当前 WebView 验证的版本。
     * 这里紧跟其后用 document.write 同步加载已验证的 4.4.0 本地副本，保证
     * 后续内联初始化代码执行前 Chart 已经可用且版本一致。传入绝对 URL后，
     * 下载到本地的 HTML 也能继续访问 ChatBot.Web 的同源静态资源。
     */
    function prepareChartJsHtml(html, localScriptUrl) {
        const source = String(html || '');
        if (!source) {
            return source;
        }

        const chartScript = CHART_EXTERNAL_SCRIPT_PATTERN.exec(source);
        if (!chartScript) {
            return source;
        }

        let prepared = source;
        const localUrl = String(localScriptUrl || '').trim();
        if (!prepared.includes(CHART_RUNTIME_MARKER) && localUrl) {
            const safeAttributeUrl = localUrl
                .replace(/&/g, '&amp;')
                .replace(/"/g, '&quot;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;');
            const safeJsUrl = safeAttributeUrl
                .replace(/\\/g, '\\\\')
                .replace(/'/g, "\\'");
            const fallback =
                `<script ${CHART_RUNTIME_MARKER}>\n` +
                `if (!window.Chart || window.Chart.version !== '4.4.0') {\n` +
                `  document.write('<script src="${safeJsUrl}"><\\/script>');\n` +
                `}\n` +
                `<\/script>`;
            const insertAt = chartScript.index + chartScript[0].length;
            prepared =
                prepared.slice(0, insertAt) +
                '\n' +
                fallback +
                prepared.slice(insertAt);
        }

        if (prepared.includes(CHART_LAYOUT_RUNTIME_MARKER)) {
            return prepared;
        }

        /*
         * 模型生成的图表经常位于折叠的 details 或尚未完成布局的容器中。
         * Chart.js 在容器宽高为 0 时初始化会得到空白或异常比例。此运行时不接触
         * 工程数据，只在页面稳定、折叠区域展开或页面重新可见时要求现有图表重算尺寸。
         */
        const layoutRecovery = `<script ${CHART_LAYOUT_RUNTIME_MARKER}>
(function (global, document) {
  'use strict';
  var existing = global.__hcsoftChartLayoutRecovery;
  if (existing && typeof existing.install === 'function') {
    existing.install();
    return;
  }

  var listenersInstalled = false;

  function chartFor(canvas) {
    if (!global.Chart || !canvas) return null;
    try {
      if (typeof global.Chart.getChart === 'function') {
        return global.Chart.getChart(canvas) || null;
      }
      var instances = global.Chart.instances || {};
      var keys = Object.keys(instances);
      for (var index = 0; index < keys.length; index++) {
        var instance = instances[keys[index]];
        if (instance && instance.canvas === canvas) return instance;
      }
    } catch (error) {
      return null;
    }
    return null;
  }

  function recover(scope) {
    var root = scope && typeof scope.querySelectorAll === 'function'
      ? scope
      : document;
    var canvases = root.matches && root.matches('canvas')
      ? [root]
      : Array.prototype.slice.call(root.querySelectorAll('canvas'));
    canvases.forEach(function (canvas) {
      var rect = canvas.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) return;
      var chart = chartFor(canvas);
      if (!chart) return;
      try {
        if (typeof chart.resize === 'function') chart.resize();
        if (typeof chart.update === 'function') chart.update('none');
      } catch (error) {
        /* 单个旧图表配置异常不能中断其他图表恢复。 */
      }
    });
  }

  function schedule(scope) {
    [0, 80, 300, 1000].forEach(function (delay) {
      global.setTimeout(function () { recover(scope); }, delay);
    });
  }

  function install() {
    if (!listenersInstalled) {
      listenersInstalled = true;
      document.addEventListener('toggle', function (event) {
        var details = event.target;
        if (details && details.tagName === 'DETAILS' && details.open) {
          schedule(details);
        }
      }, true);
      document.addEventListener('visibilitychange', function () {
        if (!document.hidden) schedule(document);
      });
      global.addEventListener('load', function () { schedule(document); });
      global.addEventListener('pageshow', function () { schedule(document); });
    }
    schedule(document);
  }

  global.__hcsoftChartLayoutRecovery = {
    version: 1,
    install: install,
    recover: recover
  };
  install();
})(window, document);
<\/script>`;

        return insertBeforeClosingTag(
            prepared,
            'body',
            layoutRecovery);
    }

    /**
     * 删除 JSON 字符串之外的 JavaScript 风格注释。
     *
     * application/json 脚本块中的正文必须是严格 JSON，浏览器不会把
     * /* ... *\/ 或 // ... 当作可忽略的 JavaScript 注释。部分旧报告会
     * 在 JSON 根对象前加入说明注释，导致 JSON.parse 在第一个 “/” 处失败。
     *
     * 此扫描器会完整保留 JSON 字符串中的 URL、转义字符以及字面量
     * “/* ... *\/”，只清理字符串之外的注释。
     */
    function stripJsonComments(value) {
        const source = String(value == null ? '' : value);
        let result = '';
        let index = 0;
        let inString = false;
        let escaped = false;

        while (index < source.length) {
            const character = source.charAt(index);
            const next = source.charAt(index + 1);

            if (inString) {
                result += character;
                if (escaped) {
                    escaped = false;
                } else if (character === '\\') {
                    escaped = true;
                } else if (character === '"') {
                    inString = false;
                }
                index++;
                continue;
            }

            if (character === '"') {
                inString = true;
                result += character;
                index++;
                continue;
            }

            if (character === '/' && next === '/') {
                index += 2;
                while (index < source.length &&
                    source.charAt(index) !== '\n' &&
                    source.charAt(index) !== '\r') {
                    index++;
                }
                continue;
            }

            if (character === '/' && next === '*') {
                index += 2;
                while (index < source.length &&
                    !(source.charAt(index) === '*' &&
                        source.charAt(index + 1) === '/')) {
                    /*
                     * 保留注释中的换行，使清理后的脚本行号尽量接近原报告，
                     * 便于继续定位其他运行时错误。
                     */
                    if (source.charAt(index) === '\n') {
                        result += '\n';
                    }
                    index++;
                }
                if (index < source.length) {
                    index += 2;
                }
                continue;
            }

            result += character;
            index++;
        }

        return result;
    }

    function canParseJson(value) {
        try {
            JSON.parse(value);
            return true;
        } catch (_) {
            return false;
        }
    }

    /**
     * 修复模型生成的 report-data 数据块，同时保持无法确认的数据原样。
     *
     * 先检查原文；只有原文无效且“仅清理注释”后能够通过 JSON.parse 时，
     * 才替换数据正文。这样不会猜测或修改真正损坏的工程数值。
     */
    function sanitizeReportDataScripts(html) {
        const source = String(html || '');
        const reportDataScriptPattern =
            /(<script\b[^>]*\bid\s*=\s*(["'])report-data\2[^>]*>)([\s\S]*?)(<\/script\s*>)/gi;

        return source.replace(
            reportDataScriptPattern,
            (whole, opening, _quote, rawData, closing) => {
                const withoutBom = rawData.replace(/^\uFEFF/, '');
                if (canParseJson(withoutBom)) {
                    return whole;
                }

                const cleaned = stripJsonComments(withoutBom).trim();
                if (!cleaned || !canParseJson(cleaned)) {
                    return whole;
                }

                /*
                 * 清理成功后顺带按 HTML script-data 规则转义 “<”，避免数据
                 * 字符串中的 </script> 提前截断后续下载或运行的报告。
                 */
                const safeJson = cleaned
                    .replace(/</g, '\\u003c')
                    .replace(/-->/g, '--\\u003e');
                return `${opening}\n${safeJson}\n${closing}`;
            });
    }

    /**
     * 为旧版模型手写的工程造价报告补充分页运行时。
     *
     * 某些旧报告先执行 renderTable()，随后才通过
     * window.paginateTable = function (...) 赋值。属性赋值不会发生函数提升，
     * 因而首个表格渲染就会抛出 ReferenceError，并阻断后续 Chart.js 初始化。
     * 这里在 head 中先安装兼容实现，在 body 末尾再次恢复，兼容旧报告覆盖全局函数。
     */
    function prepareLegacyHtml(html) {
        const source = String(html || '');
        if (!REPORT_DATA_MARKER_PATTERN.test(source)) {
            return source;
        }

        /*
         * JSON 兼容清理不依赖分页功能。即使报告没有 paginateTable，也要先
         * 修复 report-data，否则报告自己的初始化函数仍会进入“数据加载失败”。
         */
        let prepared = sanitizeReportDataScripts(source);
        if (prepared.includes(RUNTIME_MARKER) ||
            !/\bpaginateTable\s*\(/.test(prepared)) {
            return prepared;
        }

        prepared = insertBeforeClosingTag(
            prepared,
            'head',
            paginationBootstrap);
        prepared = insertBeforeClosingTag(
            prepared,
            'body',
            paginationRestore);
        return prepared;
    }

    /**
     * 从桌面端 decimal 累计结果构造报告可使用的事实和图表数据。
     * 这里不复制 itemEvidence，因此固化后的 HTML 不会暴露会话内 targetId。
     */
    function buildAnalysisReportData(analysis) {
        if (!analysis ||
            analysis.schemaVersion !== 'analysis-1.0' ||
            !analysis.coverage ||
            analysis.coverage.complete !== true ||
            !analysis.aggregates) {
            return null;
        }

        const facts = {};
        const put = (key, value) => {
            if (value !== undefined && value !== null) {
                facts[key] = String(value);
            }
        };
        const coverage = analysis.coverage;
        const project = analysis.unitProject || {};
        put('project.name', project.name);
        put('project.quotaSystem', project.quotaSystem);
        put('project.templateName', project.templateName);
        put('project.pricingMode', project.pricingMode);
        put('project.buildType', project.buildType);
        put('project.capturedAtUtc', analysis.capturedAtUtc);
        put('project.semanticEvidenceMode',
            analysis.semanticEvidenceMode || 'prioritized');

        put('coverage.expectedTotal', coverage.expectedTotal);
        put('coverage.processedTotal', coverage.processedTotal);
        put('coverage.filteredItems', coverage.filteredItems);
        put('coverage.missingSubmittedItems',
            coverage.missingSubmittedItems);
        put('coverage.missingSubmittedFees',
            coverage.missingSubmittedFees);
        put('coverage.invalidNumericValues',
            coverage.invalidNumericValues);

        const totals = analysis.aggregates.authoritativeTotals || {};
        for (const key of [
            'total',
            'civilOrInstallation',
            'labor',
            'material',
            'machinery',
            'other',
            'mainMaterial',
            'equipment',
            'calculatedCost'
        ]) {
            put(`totals.${key}`, totals[key]);
        }

        const comparison = analysis.aggregates.comparison || {};
        for (const key of [
            'comparableItems',
            'currentTotal',
            'submittedTotal',
            'difference',
            'grossIncrease',
            'grossReduction',
            'netReduction',
            'differenceRate',
            'quantityImpact',
            'unitPriceImpact',
            'reconciliationWarnings'
        ]) {
            put(`comparison.${key}`, comparison[key]);
        }

        const material = analysis.aggregates.materials || {};
        for (const key of [
            'count',
            'comparableBudgetMarketCount',
            'budgetPriceSum',
            'marketPriceSum',
            'marketMinusBudget'
        ]) {
            put(`materials.${key}`, material[key]);
        }

        const fee = analysis.aggregates.fees || {};
        for (const key of [
            'count',
            'comparableCount',
            'currentTotal',
            'submittedTotal',
            'difference'
        ]) {
            put(`fees.${key}`, fee[key]);
        }

        const sections = Array.isArray(analysis.aggregates.sections)
            ? analysis.aggregates.sections
            : [];
        sections.forEach((section, index) => {
            put(`sections.${index}.index`, section.index);
            put(`sections.${index}.name`, section.name);
            put(`sections.${index}.itemCount`, section.itemCount);
            put(`sections.${index}.currentTotal`, section.currentTotal);
            put(`sections.${index}.submittedTotal`, section.submittedTotal);
            put(`sections.${index}.difference`, section.difference);
        });

        const rowTypes = Array.isArray(analysis.aggregates.rowTypes)
            ? analysis.aggregates.rowTypes
            : [];
        rowTypes.forEach((row, index) => {
            put(`rowTypes.${index}.rowType`, row.rowType);
            put(`rowTypes.${index}.count`, row.count);
            put(`rowTypes.${index}.effectiveCount`, row.effectiveCount);
            put(`rowTypes.${index}.observedTotal`, row.observedTotal);
        });

        /*
         * 重点证据也转换为逐字段事实，但绝不复制 evidenceId、targetId 或
         * parentTargetId。这样报告可以展示可核查的明细，同时固化后的 HTML
         * 不会泄露只在当前桌面会话内有效的定位凭据。
         */
        const itemEvidence = Array.isArray(analysis.itemEvidence)
            ? analysis.itemEvidence
            : [];
        put('evidence.items.count', itemEvidence.length);
        itemEvidence.forEach((entry, index) => {
            const item = entry && entry.item ? entry.item : {};
            put(`evidence.items.${index}.reasons`,
                Array.isArray(entry && entry.reasons)
                    ? entry.reasons.join('；')
                    : '');
            put(`evidence.items.${index}.sectionName`, item.sectionName);
            put(`evidence.items.${index}.rowType`, item.rowType);
            put(`evidence.items.${index}.sequence`, item.sequence);
            put(`evidence.items.${index}.code`, item.code);
            put(`evidence.items.${index}.name`, item.name);
            put(`evidence.items.${index}.specification`, item.specification);
            put(`evidence.items.${index}.unit`, item.unit);
            put(`evidence.items.${index}.actualQuantity`, item.actualQuantity);
            put(`evidence.items.${index}.submittedActualQuantity`,
                item.submittedActualQuantity);
            put(`evidence.items.${index}.currentTotal`, entry.currentTotal);
            put(`evidence.items.${index}.submittedTotal`, entry.submittedTotal);
            put(`evidence.items.${index}.difference`, entry.difference);
        });

        const materialEvidence = Array.isArray(analysis.materialEvidence)
            ? analysis.materialEvidence
            : [];
        put('evidence.materials.count', materialEvidence.length);
        materialEvidence.forEach((entry, index) => {
            const materialItem =
                entry && entry.material ? entry.material : {};
            put(`evidence.materials.${index}.code`, materialItem.code);
            put(`evidence.materials.${index}.name`, materialItem.name);
            put(`evidence.materials.${index}.specification`,
                materialItem.specification);
            put(`evidence.materials.${index}.unit`, materialItem.unit);
            put(`evidence.materials.${index}.budgetPrice`,
                materialItem.budgetPrice);
            put(`evidence.materials.${index}.marketPrice`,
                materialItem.marketPrice);
            put(`evidence.materials.${index}.basePrice`,
                materialItem.basePrice);
            put(`evidence.materials.${index}.difference`, entry.difference);
        });

        const feeEvidence = Array.isArray(analysis.feeEvidence)
            ? analysis.feeEvidence
            : [];
        put('evidence.fees.count', feeEvidence.length);
        feeEvidence.forEach((entry, index) => {
            const feeItem = entry && entry.fee ? entry.fee : {};
            put(`evidence.fees.${index}.sectionName`, feeItem.sectionName);
            put(`evidence.fees.${index}.code`, feeItem.code);
            put(`evidence.fees.${index}.name`, feeItem.name);
            put(`evidence.fees.${index}.rate`, feeItem.rate);
            put(`evidence.fees.${index}.formula`, feeItem.formula);
            put(`evidence.fees.${index}.value`, feeItem.value);
            put(`evidence.fees.${index}.submittedValue`,
                feeItem.submittedValue);
            put(`evidence.fees.${index}.difference`, entry.difference);
        });

        const comparableItems = itemEvidence
            .filter(entry =>
                entry &&
                entry.item &&
                entry.submittedTotal !== null &&
                entry.submittedTotal !== undefined)
            .slice(0, 12);
        const materialItems = materialEvidence
            .filter(entry => entry && entry.material)
            .slice(0, 12);
        const comparableFees = feeEvidence
            .filter(entry =>
                entry &&
                entry.fee &&
                entry.fee.submittedValue !== null &&
                entry.fee.submittedValue !== undefined)
            .slice(0, 12);

        const charts = {
            'cost-components': {
                type: 'doughnut',
                labels: ['人工费', '材料费', '机械费', '主材费', '设备费', '其他费用'],
                datasets: [{
                    label: '费用构成',
                    data: [
                        totals.labor || '0',
                        totals.material || '0',
                        totals.machinery || '0',
                        totals.mainMaterial || '0',
                        totals.equipment || '0',
                        totals.other || '0'
                    ],
                    backgroundColor: [
                        '#2563eb', '#14b8a6', '#f59e0b',
                        '#8b5cf6', '#ec4899', '#64748b'
                    ]
                }]
            },
            'section-current-vs-submitted': {
                type: 'bar',
                labels: sections.map(item => item.name || '未命名标段'),
                datasets: [
                    {
                        label: '当前值',
                        data: sections.map(item => item.currentTotal || '0'),
                        backgroundColor: '#2563eb'
                    },
                    {
                        label: '报送/对比值',
                        data: sections.map(item => item.submittedTotal || '0'),
                        backgroundColor: '#94a3b8'
                    }
                ]
            },
            'row-type-counts': {
                type: 'bar',
                labels: rowTypes.map(item => item.rowType || ''),
                datasets: [{
                    label: '有效记录数',
                    data: rowTypes.map(item => item.effectiveCount || 0),
                    backgroundColor: '#0f766e'
                }]
            },
            'audit-current-vs-submitted': {
                type: 'bar',
                labels: ['当前值', '报送/对比值', '核增', '核减'],
                datasets: [{
                    label: '金额',
                    data: [
                        comparison.currentTotal || '0',
                        comparison.submittedTotal || '0',
                        comparison.grossIncrease || '0',
                        comparison.grossReduction || '0'
                    ],
                    backgroundColor: [
                        '#2563eb', '#64748b', '#ef4444', '#10b981'
                    ]
                }]
            },
            'material-budget-vs-market': {
                type: 'bar',
                labels: [
                    '预算价格字段观察和',
                    '市场/调整价格字段观察和'
                ],
                datasets: [{
                    label: '材料价格字段校验值（非材料造价）',
                    data: [
                        material.budgetPriceSum || '0',
                        material.marketPriceSum || '0'
                    ],
                    backgroundColor: ['#64748b', '#f59e0b']
                }]
            },
            'fee-current-vs-submitted': {
                type: 'bar',
                labels: ['当前费用', '报送费用'],
                datasets: [{
                    label: '费用金额',
                    data: [
                        fee.currentTotal || '0',
                        fee.submittedTotal || '0'
                    ],
                    backgroundColor: ['#2563eb', '#94a3b8']
                }]
            },
            'evidence-item-current-vs-submitted': {
                type: 'bar',
                labels: comparableItems.map(entry =>
                    entry.item.name || entry.item.code || '未命名项目'),
                datasets: [
                    {
                        label: '当前值',
                        data: comparableItems.map(entry =>
                            entry.currentTotal || '0'),
                        backgroundColor: '#2563eb'
                    },
                    {
                        label: '报送/对比值',
                        data: comparableItems.map(entry =>
                            entry.submittedTotal || '0'),
                        backgroundColor: '#94a3b8'
                    }
                ]
            },
            'evidence-item-differences': {
                type: 'bar',
                labels: comparableItems.map(entry =>
                    entry.item.name || entry.item.code || '未命名项目'),
                datasets: [{
                    label: '当前值－报送/对比值',
                    data: comparableItems.map(entry =>
                        entry.difference || '0'),
                    backgroundColor: comparableItems.map(entry =>
                        String(entry.difference || '0').startsWith('-')
                            ? '#10b981'
                            : '#ef4444')
                }]
            },
            'evidence-material-budget-vs-market': {
                type: 'bar',
                labels: materialItems.map(entry =>
                    entry.material.name ||
                    entry.material.code ||
                    '未命名材料'),
                datasets: [
                    {
                        label: '预算价',
                        data: materialItems.map(entry =>
                            entry.material.budgetPrice || '0'),
                        backgroundColor: '#64748b'
                    },
                    {
                        label: '市场/调整价',
                        data: materialItems.map(entry =>
                            entry.material.marketPrice || '0'),
                        backgroundColor: '#f59e0b'
                    }
                ]
            },
            'evidence-fee-current-vs-submitted': {
                type: 'bar',
                labels: comparableFees.map(entry =>
                    entry.fee.name || entry.fee.code || '未命名费用'),
                datasets: [
                    {
                        label: '当前费用',
                        data: comparableFees.map(entry =>
                            entry.fee.value || '0'),
                        backgroundColor: '#2563eb'
                    },
                    {
                        label: '报送费用',
                        data: comparableFees.map(entry =>
                            entry.fee.submittedValue || '0'),
                        backgroundColor: '#94a3b8'
                    }
                ]
            }
        };

        return {
            schemaVersion: 'report-facts-1.0',
            capturedAtUtc: analysis.capturedAtUtc || '',
            semanticEvidenceMode:
                analysis.semanticEvidenceMode || 'prioritized',
            facts,
            charts
        };
    }

    const analysisRuntimeSource = `
(function (global, document) {
    'use strict';
    var node = document.getElementById('hcsoft-analysis-report-data');
    if (!node) return;
    var payload;
    try { payload = JSON.parse(node.textContent || '{}'); }
    catch (_) { return; }

    function roundDecimal(value, digits) {
        var text = String(value == null ? '' : value).trim();
        if (!/^-?\\d+(?:\\.\\d+)?$/.test(text)) return text || '—';
        var negative = text.charAt(0) === '-';
        if (negative) text = text.slice(1);
        var parts = text.split('.');
        var integer = parts[0] || '0';
        var fraction = parts[1] || '';
        var padded = (fraction + '0'.repeat(digits + 1)).slice(0, digits + 1);
        var retained = padded.slice(0, digits);
        var scaledText = (integer + retained).replace(/^0+(?=\\d)/, '') || '0';
        var scaled = BigInt(scaledText);
        if (Number(padded.charAt(digits) || '0') >= 5) scaled += 1n;
        var result = scaled.toString().padStart(digits + 1, '0');
        var whole = digits > 0 ? result.slice(0, -digits) : result;
        var decimal = digits > 0 ? result.slice(-digits) : '';
        whole = whole.replace(/\\B(?=(\\d{3})+(?!\\d))/g, ',');
        return (negative && scaled !== 0n ? '-' : '') +
            whole + (digits > 0 ? '.' + decimal : '');
    }

    function format(value, kind) {
        if (kind === 'integer') return roundDecimal(value, 0);
        if (kind === 'percent') return roundDecimal(value, 2) + '%';
        if (kind === 'money') return roundDecimal(value, 2);
        if (kind === 'money4') return roundDecimal(value, 4);
        return String(value == null || value === '' ? '—' : value);
    }

    function installFacts() {
        var facts = payload.facts || {};
        document.querySelectorAll('[data-hcsoft-fact]').forEach(function (element) {
            var id = element.getAttribute('data-hcsoft-fact');
            if (!Object.prototype.hasOwnProperty.call(facts, id)) {
                element.textContent = '数据不可用';
                element.setAttribute('data-hcsoft-fact-error', 'unknown');
                return;
            }
            var value = format(
                facts[id],
                element.getAttribute('data-hcsoft-format') || '');
            var unit = element.getAttribute('data-hcsoft-unit') || '';
            element.textContent = value + unit;
        });
    }

    function showChartMessage(canvas, message) {
        canvas.hidden = true;
        var frame = canvas.parentElement;
        if (!frame) return;
        var existing = frame.querySelector('[data-hcsoft-chart-message]');
        if (!existing) {
            existing = document.createElement('p');
            existing.setAttribute('data-hcsoft-chart-message', '');
            existing.style.margin = 'auto';
            existing.style.padding = '24px';
            existing.style.color = '#64748b';
            existing.style.textAlign = 'center';
            frame.appendChild(existing);
        }
        existing.textContent = message;
    }

    function hasChartData(definition) {
        return Boolean(
            definition &&
            Array.isArray(definition.labels) &&
            definition.labels.length > 0 &&
            Array.isArray(definition.datasets) &&
            definition.datasets.some(function (dataset) {
                return dataset &&
                    Array.isArray(dataset.data) &&
                    dataset.data.length === definition.labels.length &&
                    dataset.data.some(function (value) {
                        return /^-?\\d+(?:\\.\\d+)?$/.test(
                            String(value == null ? '' : value));
                    });
            }));
    }

    function renderChartWhenSized(canvas, definition) {
        var frame = canvas.parentElement;
        if (!frame ||
            frame.clientWidth < 16 ||
            frame.clientHeight < 16) {
            return false;
        }
        if (!hasChartData(definition)) {
            showChartMessage(canvas, '无足够可比数据');
            return true;
        }

        var existing = global.Chart.getChart &&
            global.Chart.getChart(canvas);
        if (existing) {
            existing.resize();
            return true;
        }
        canvas.hidden = false;
        new global.Chart(canvas.getContext('2d'), {
                type: definition.type,
                data: {
                    labels: definition.labels,
                    datasets: definition.datasets
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    resizeDelay: 150,
                    devicePixelRatio: Math.min(
                        Number(global.devicePixelRatio) || 1,
                        2),
                    animation: { duration: 450 },
                    interaction: { mode: 'index', intersect: false },
                    plugins: {
                        legend: { position: 'bottom' },
                        tooltip: {
                            callbacks: {
                                label: function (context) {
                                    var label = context.dataset.label || '';
                                    return label + ': ' +
                                        roundDecimal(context.raw, 2);
                                }
                            }
                        }
                    },
                    scales: definition.type === 'doughnut'
                        ? {}
                        : { y: { beginAtZero: true } }
                }
            });
        return true;
    }

    function installCharts() {
        var canvases = document.querySelectorAll(
            'canvas[data-hcsoft-chart]');
        if (typeof global.Chart !== 'function') {
            canvases.forEach(function (canvas) {
                showChartMessage(
                    canvas,
                    '统计图加载失败，请检查网络或 CDN');
            });
            return;
        }

        var charts = payload.charts || {};
        canvases.forEach(function (canvas) {
            var id = canvas.getAttribute('data-hcsoft-chart');
            var definition = charts[id];
            if (!definition) return;
            if (renderChartWhenSized(canvas, definition)) return;

            /*
             * 隐藏章节的 clientWidth/clientHeight 为 0。使用 ResizeObserver
             * 等到容器真正展开后再创建 Chart，避免首次渲染被拉长或压扁。
             */
            if (typeof global.ResizeObserver === 'function') {
                var observer = new global.ResizeObserver(function () {
                    if (renderChartWhenSized(canvas, definition)) {
                        observer.disconnect();
                    }
                });
                observer.observe(canvas.parentElement);
                return;
            }

            global.setTimeout(function () {
                renderChartWhenSized(canvas, definition);
            }, 0);
        });

        var resizeTimer = 0;
        global.addEventListener('resize', function () {
            global.clearTimeout(resizeTimer);
            resizeTimer = global.setTimeout(function () {
                canvases.forEach(function (canvas) {
                    var chart = global.Chart.getChart &&
                        global.Chart.getChart(canvas);
                    if (chart) chart.resize();
                });
            }, 180);
        });
    }

    function init() {
        installFacts();
        installCharts();
    }
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init, { once: true });
    } else {
        init();
    }
})(window, document);`;

    /**
     * 在模型完整 HTML 中注入不含 targetId 的确定性事实数据和通用绑定运行时。
     * 页面布局仍完全由模型生成，本函数不提供或复制任何固定 HTML 模板。
     */
    function prepareAnalysisHtml(html, analysis) {
        const source = String(html || '');
        if (!analysis || source.includes(ANALYSIS_RUNTIME_MARKER)) {
            return source;
        }

        /*
         * 确定性绑定现在是可选能力，不再是报告输出门禁。
         * HTML 没有绑定时原样返回；有绑定时注入可用事实，未知绑定由运行时
         * 显示“数据不可用”，但不会阻止报告预览、下载或导出。
         */
        if (!/\bdata-hcsoft-(?:fact|chart)\s*=/i.test(source)) {
            return source;
        }

        const reportData = buildAnalysisReportData(analysis);
        if (!reportData) {
            return source;
        }
        const json = JSON.stringify(reportData)
            .replace(/</g, '\\u003c')
            .replace(/-->/g, '--\\u003e');
        const runtime =
            `<script id="hcsoft-analysis-report-data" type="application/json">${json}</script>\n` +
            `<script ${ANALYSIS_RUNTIME_MARKER}>${analysisRuntimeSource}</script>`;
        return insertBeforeClosingTag(source, 'body', runtime);
    }

    return Object.freeze({
        prepareLegacyHtml: prepareLegacyHtml,
        prepareChartJsHtml: prepareChartJsHtml,
        buildAnalysisReportData: buildAnalysisReportData,
        prepareAnalysisHtml: prepareAnalysisHtml
    });
});
