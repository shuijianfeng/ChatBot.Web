using System.Text.RegularExpressions;

namespace ChatBot.Web.Services;

/// <summary>
/// 修复旧版模型手写工程造价报告中的公共运行时问题。
/// </summary>
/// <remarks>
/// 这里主要兼容历史分享页以及未遵守函数声明顺序约束的报告：
/// 报告可能先调用 <c>paginateTable(...)</c>，随后才通过
/// <c>window.paginateTable = function (...)</c> 赋值。属性赋值不会发生函数提升，
/// 首个表格渲染因此抛出 <c>ReferenceError</c>，并阻断后续 Chart.js 初始化。
/// </remarks>
public static class HcsoftReportHtmlNormalizer
{
    private const string RuntimeMarker =
        "data-hcsoft-report-runtime=\"pagination-v1\"";

    private static readonly Regex ReportDataRegex = new(
        """<script\b[^>]*\bid\s*=\s*(["'])report-data\1[^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PaginateCallRegex = new(
        """\bpaginateTable\s*\(""",
        RegexOptions.Compiled);

    private const string PaginationBootstrap = """
<script data-hcsoft-report-runtime="pagination-v1">
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
        return positiveInteger(
            selector && selector.value,
            positiveInteger(fallback, 20));
    }

    function rowsOf(table) {
        return Array.prototype.slice.call(
            table.querySelectorAll('tbody tr'));
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
        var totalPages = Math.max(
            1,
            Math.ceil(matchedRows.length / size));
        var currentPage = Math.min(
            positiveInteger(page, 1),
            totalPages);
        var start = (currentPage - 1) * size;
        var end = start + size;

        allRows.forEach(function (row) {
            row.style.display = 'none';
        });
        matchedRows.forEach(function (row, index) {
            row.style.display =
                index >= start && index < end ? '' : 'none';
        });

        var pagination = paginationOf(tableId);
        if (!pagination) return;

        pagination.textContent = '';
        for (var number = 1; number <= totalPages; number += 1) {
            var button = document.createElement('button');
            button.type = 'button';
            button.textContent = String(number);
            if (number === currentPage) {
                button.classList.add('active');
            }
            button.addEventListener(
                'click',
                (function (targetPage) {
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

        var normalized =
            String(query || '').toLocaleLowerCase().trim();
        rowsOf(table).forEach(function (row) {
            var searchable =
                row.getAttribute('data-search') ||
                row.textContent ||
                '';
            var matched =
                normalized === '' ||
                searchable.toLocaleLowerCase().indexOf(normalized) >= 0;
            row.setAttribute(
                'data-hcsoft-filter-match',
                matched ? '1' : '0');
        });
        paginateTable(tableId, 1, pageSizeOf(tableId, 20));
    }

    function refresh() {
        Array.prototype.slice.call(
            document.querySelectorAll('table[id]'))
            .forEach(function (table) {
                if (!paginationOf(table.id)) return;
                paginateTable(
                    table.id,
                    1,
                    pageSizeOf(table.id, 20));
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
</script>
""";

    private const string PaginationRestore = """
<script data-hcsoft-report-runtime="pagination-restore-v1">
(function (global) {
    var runtime = global.__hcsoftReportPagination;
    if (!runtime) return;
    runtime.install();
    runtime.refresh();
})(window);
</script>
""";

    /// <summary>
    /// 为识别出的旧版工程造价报告注入幂等的分页兼容运行时。
    /// 普通 HTML 以及已修复报告保持原样。
    /// </summary>
    public static string Normalize(string? html)
    {
        if (string.IsNullOrEmpty(html) ||
            !ReportDataRegex.IsMatch(html) ||
            !PaginateCallRegex.IsMatch(html) ||
            html.Contains(RuntimeMarker, StringComparison.Ordinal))
        {
            return html ?? string.Empty;
        }

        var normalized = InsertBeforeClosingTag(
            html,
            "head",
            PaginationBootstrap);
        return InsertBeforeClosingTag(
            normalized,
            "body",
            PaginationRestore);
    }

    private static string InsertBeforeClosingTag(
        string html,
        string tagName,
        string content)
    {
        var closingTag = $"</{tagName}>";
        var index = html.IndexOf(
            closingTag,
            StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return string.Concat(
                html.AsSpan(0, index),
                content,
                Environment.NewLine,
                html.AsSpan(index));
        }

        if (tagName.Equals("head", StringComparison.OrdinalIgnoreCase))
        {
            var firstScript = html.IndexOf(
                "<script",
                StringComparison.OrdinalIgnoreCase);
            if (firstScript >= 0)
            {
                return string.Concat(
                    html.AsSpan(0, firstScript),
                    content,
                    Environment.NewLine,
                    html.AsSpan(firstScript));
            }
        }

        return string.Concat(content, Environment.NewLine, html);
    }
}
