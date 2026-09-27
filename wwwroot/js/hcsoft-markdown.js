(function (root, factory) {
    'use strict';

    const api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    }
    if (root) {
        root.hcsoftMarkdownNormalizer = api;
    }
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
    'use strict';

    /**
     * 使用比正文中任何连续反引号都更长的围栏包装思考内容。
     * 例如思考内容含有 ```json ... ``` 时，外层至少使用 ````thoughts，
     * 从而保证内层示例只是 thoughts 代码块的普通文本，不会提前关闭外层。
     */
    function wrapThoughts(content) {
        const body = trimBoundaryBlankLines(content);
        const runs = body.match(/`+/g) || [];
        const longest = runs.reduce(
            (maximum, run) => Math.max(maximum, run.length),
            0);
        const fence = '`'.repeat(Math.max(4, longest + 1));
        return `\n${fence}thoughts\n${body}\n${fence}\n`;
    }

    /**
     * 只去掉内容首尾的空白行，正文内部的缩进和换行保持不变。
     * 这样既避免生成大量空行，也不会改变推理内容中的 Markdown 结构。
     */
    function trimBoundaryBlankLines(content) {
        return String(content == null ? '' : content)
            .replace(/^(?:[ \t]*\r?\n)+/, '')
            .replace(/(?:\r?\n[ \t]*)+$/, '');
    }

    /**
     * 提取 think 块中真正的推理正文。
     *
     * 兼容两种模型输出：
     * 1. <think>正文</think>
     * 2. <think>~~~Thoughts 正文 ~~~</think>
     *
     * 第二种格式只在 Thoughts 围栏包住整个 think 内容时才拆掉外壳。
     * 结束围栏从块尾判断，而不是遇到第一个 ~~~ 就结束，因此正文里可以
     * 安全包含其他 Markdown 代码块。
     */
    function unwrapThoughtsEnvelope(content) {
        const body = String(content == null ? '' : content)
            .replace(/\r\n?/g, '\n');
        const lines = body.split('\n');
        let first = 0;
        let last = lines.length - 1;

        while (first <= last && /^[ \t]*$/.test(lines[first])) {
            first++;
        }
        while (last >= first && /^[ \t]*$/.test(lines[last])) {
            last--;
        }
        if (first > last) return '';

        const opening = /^[ \t]{0,3}(`{3,}|~{3,})[ \t]*thoughts[ \t]*$/i
            .exec(lines[first]);
        if (!opening) {
            return lines.slice(first, last + 1).join('\n');
        }

        const marker = opening[1];
        const closing = /^[ \t]{0,3}(`{3,}|~{3,})[ \t]*$/
            .exec(lines[last]);
        const hasMatchingClosing =
            closing &&
            closing[1].charAt(0) === marker.charAt(0) &&
            closing[1].length >= marker.length;

        const end = hasMatchingClosing ? last : last + 1;
        return trimBoundaryBlankLines(
            lines.slice(first + 1, end).join('\n'));
    }

    function findTag(source, expression, startIndex) {
        expression.lastIndex = startIndex;
        return expression.exec(source);
    }

    /**
     * 将任意数量的 <think> 块逐块转换为 thoughts 围栏。
     *
     * 这里不使用 “<think>[\s\S]*?</think>” 一类跨块替换：
     * - 每个完整块独立转换，不会吞掉两个块之间的正式回答；
     * - 没有 ~~~Thoughts 外壳的普通 think 块同样支持；
     * - 流式传输中最后一个尚未收到 </think> 的块也能临时显示；
     * - 若异常流连续给出两个开始标签，前一块在后一标签前结束，后续块
     *   仍可继续解析。
     */
    function convertThinkBlocks(content) {
        const source = String(content == null ? '' : content);
        const openExpression = /<think\b[^>]*>/gi;
        const closeExpression = /<\/think\s*>/gi;
        let cursor = 0;
        let output = '';

        while (cursor < source.length) {
            const opening = findTag(
                source,
                new RegExp(openExpression.source, openExpression.flags),
                cursor);
            if (!opening) {
                output += source.slice(cursor);
                break;
            }

            output += source.slice(cursor, opening.index);
            // 自动续写的推理可能插在 <td> 或脚本中间。它不是 HTML 正文，
            // 不能转成新的 Markdown 围栏，否则会提前关闭正在生成的报告。
            const insideHtmlDocument = hasOpenHtmlDocument(output);
            const bodyStart = opening.index + opening[0].length;
            const closing = findTag(
                source,
                new RegExp(closeExpression.source, closeExpression.flags),
                bodyStart);
            const nestedOpening = findTag(
                source,
                new RegExp(openExpression.source, openExpression.flags),
                bodyStart);

            /*
             * 正常块以 </think> 结束。若下一个 <think> 更早出现，说明前一块
             * 的结束标签在流式传输或模型输出中丢失；在新块前截断可防止
             * 前一块吞掉后续所有内容。
             */
            if (nestedOpening &&
                (!closing || nestedOpening.index < closing.index)) {
                if (!insideHtmlDocument) {
                    output += wrapThoughts(unwrapThoughtsEnvelope(
                        source.slice(bodyStart, nestedOpening.index)));
                }
                cursor = nestedOpening.index;
                continue;
            }

            if (closing) {
                if (!insideHtmlDocument) {
                    output += wrapThoughts(unwrapThoughtsEnvelope(
                        source.slice(bodyStart, closing.index)));
                }
                cursor = closing.index + closing[0].length;
                if (insideHtmlDocument) {
                    // 后端思考块结束标记自带两个换行；移除封装才能在脚本字符串内续接。
                    const envelopeEnd = /^(?:\r?\n){2}/.exec(source.slice(cursor));
                    if (envelopeEnd) cursor += envelopeEnd[0].length;
                }
                continue;
            }

            // 流式回复尚未收到结束标签：把当前已有内容临时包成完整围栏。
            if (!insideHtmlDocument) {
                output += wrapThoughts(unwrapThoughtsEnvelope(
                    source.slice(bodyStart)));
            }
            cursor = source.length;
        }

        return output;
    }

    /**
     * 清理没有对应 <think> 的孤立结束标签。
     *
     * 某些接口会只返回 “~~~ ... </think>” 这一段。此时最后一个无语言
     * 围栏属于不可见推理块的结尾，必须连同它到 </think> 之间的内容移除，
     * 否则后面的 ```html 会被 Markdown 当成前一个匿名代码块的结束标记。
     */
    function removeOrphanThinkEndings(content) {
        const source = String(content == null ? '' : content);
        const closingExpression = /<\/think\s*>/gi;
        const plainFenceExpression =
            /^[ \t]*~{3,}[ \t]*\r?$/gm;
        let cursor = 0;
        let output = '';
        let closing;

        while ((closing = findTag(
            source,
            new RegExp(
                closingExpression.source,
                closingExpression.flags),
            cursor))) {
            const beforeClosing = source.slice(cursor, closing.index);
            let lastFence = null;
            let fence;
            plainFenceExpression.lastIndex = 0;
            while ((fence = plainFenceExpression.exec(beforeClosing))) {
                lastFence = fence;
            }

            output += lastFence
                ? beforeClosing.slice(0, lastFence.index)
                : beforeClosing;
            cursor = closing.index + closing[0].length;
        }

        return output + source.slice(cursor);
    }

    /**
     * 返回 Markdown 围栏覆盖的字符区间，同时保留最后一个未闭合围栏。
     * 这里只按 CommonMark 的行首围栏判断，避免把 HTML/JavaScript 字符串中的
     * 反引号误当成代码块边界。
     */
    function collectFenceRanges(content) {
        const source = String(content == null ? '' : content);
        const ranges = [];
        const lineExpression = /[^\r\n]*(?:\r\n|\n|$)/g;
        let opening = null;
        let line;

        while ((line = lineExpression.exec(source))) {
            if (!line[0]) break;
            const markerMatch =
                /^[ \t]{0,3}(`{3,}|~{3,})([^\r\n]*)/.exec(line[0]);
            if (!markerMatch) continue;

            const marker = markerMatch[1];
            const tail = markerMatch[2] || '';
            if (!opening) {
                opening = {
                    start: line.index,
                    markerChar: marker.charAt(0),
                    markerLength: marker.length,
                    language: tail.trim().split(/\s+/, 1)[0].toLowerCase()
                };
                continue;
            }

            const isClosing =
                marker.charAt(0) === opening.markerChar &&
                marker.length >= opening.markerLength &&
                tail.trim() === '';
            if (!isClosing) continue;

            ranges.push({
                ...opening,
                end: line.index + line[0].length,
                open: false
            });
            opening = null;
        }

        if (opening) {
            ranges.push({
                ...opening,
                end: source.length,
                open: true
            });
        }
        return ranges;
    }

    function hasOpenHtmlDocument(content) {
        const ranges = collectFenceRanges(content);
        const candidateExpression =
            /^[ \t]{0,3}(?:<!doctype\s+html\b|<html(?:\s|>))/gim;
        let candidate;
        while ((candidate = candidateExpression.exec(content))) {
            const fence = ranges.find(range =>
                candidate.index >= range.start && candidate.index < range.end);
            if (fence && (fence.language !== 'html' || !fence.open)) continue;
            if (!/<\/html\s*>/i.test(content.slice(candidate.index))) return true;
        }
        return false;
    }

    /**
     * 模型偶尔会漏掉最终输出要求中的 ```html 围栏。若把完整文档直接交给
     * marked，它会作为真实 DOM 插入聊天消息；innerHTML 插入的 script 不执行，
     * 因而既没有代码框/“运行”按钮，Chart.js 图表也会全部空白。
     *
     * 这里仅包装围栏之外、从行首开始的完整 HTML 文档。普通内联 HTML 和已经
     * 正确围住的代码块保持不变；流式文档未结束时先补开围栏，收到 </html> 后
     * 再闭合，因此重复调用也是幂等的。
     */
    function wrapStandaloneHtmlDocument(content) {
        const source = String(content == null ? '' : content);
        if (!source) return source;

        const ranges = collectFenceRanges(source);
        const openHtmlFence = ranges.find(range =>
            range.open && range.language === 'html');
        if (openHtmlFence) {
            const htmlClosingIndex = source
                .toLowerCase()
                .lastIndexOf('</html>');
            return htmlClosingIndex > openHtmlFence.start
                ? source.replace(/[ \t\r\n]*$/, '') + '\n```'
                : source;
        }

        const candidateExpression =
            /^[ \t]{0,3}(?:<!doctype\s+html\b|<html(?:\s|>))/gim;
        let candidate;
        let start = -1;
        while ((candidate = candidateExpression.exec(source))) {
            const insideFence = ranges.some(range =>
                candidate.index >= range.start &&
                candidate.index < range.end);
            if (!insideFence) {
                start = candidate.index;
                break;
            }
        }
        if (start < 0) return source;

        const lowerSource = source.toLowerCase();
        const closingIndex = lowerSource.lastIndexOf('</html>');
        const hasClosing = closingIndex >= start;
        const closingEnd = hasClosing
            ? closingIndex + '</html>'.length
            : source.length;
        const prefix = source.slice(0, start);
        const documentSource = source.slice(start, closingEnd);
        const suffix = source.slice(closingEnd);
        const beforeFence = prefix && !/\r?\n$/.test(prefix)
            ? prefix + '\n'
            : prefix;
        const afterDocument = /\r?\n$/.test(documentSource)
            ? ''
            : '\n';

        return beforeFence +
            '```html\n' +
            documentSource +
            (hasClosing ? afterDocument + '```' : '') +
            suffix;
    }

    /**
     * 清理模型流式回复中的思考标签，并把波浪线围栏统一为反引号围栏。
     *
     * 部分模型只返回思考块的结尾：
     *
     * ~~~
     * </think>
     * ```html
     *
     * 旧逻辑会把第一个 ~~~ 转换成孤立的 ```。Markdown 解析器随后把它
     * 当作一个无语言代码块的开始，因此真正的 ```html 会作为普通文字显示。
     * 对没有起始 <think> 的结尾标记必须直接移除，不能制造新的关闭围栏。
     */
    function preprocess(content) {
        const source = String(content == null ? '' : content);
        let result = source.replace(
            /(\[\d+\])(?=\[\d+\])/g,
            '$1 ');

        result = convertThinkBlocks(result);
        result = removeOrphanThinkEndings(result);

        // 容错清理畸形或被截断后仍残留的单独标签。
        result = result.replace(/<\/?think\b[^>]*>/gi, '');
        result = result.replace(/^~~~(\w+)/gm, '```$1');
        result = result.replace(/^~~~\s*$/gm, '```');

        /*
         * 兼容已经被旧逻辑处理过并保存到会话中的内容：
         * 删除紧邻有类型围栏之前的空白、无类型孤立围栏。
         * 多加空行不能改变 Markdown 的配对关系，所以必须删除孤立围栏本身。
         */
        result = result.replace(
            /(^|\r?\n)[ \t]*```[ \t]*(?:\r?\n[ \t]*)+(?=```[ \t]*[A-Za-z0-9_+#.-]+\b)/g,
            '$1');

        return wrapStandaloneHtmlDocument(result);
    }

    /**
     * 流式显示时只为真正尚未关闭的围栏补结束标记。
     *
     * 旧实现仅统计全文中 “```” 的出现次数，会把代码字符串内的反引号、
     * 不同长度围栏和带语言的开头混在一起。这里按 CommonMark 的行级围栏
     * 规则维护开关状态，避免把 ```html 错判为前一个 plaintext 围栏的结尾。
     */
    function completeOpenFences(content) {
        const source = String(content == null ? '' : content);
        if (!source) return source;

        const lines = source.split(/\r?\n/);
        let openFence = null;
        for (const line of lines) {
            const match = /^[ \t]{0,3}(`{3,}|~{3,})(.*)$/.exec(line);
            if (!match) continue;

            const marker = match[1];
            const markerChar = marker.charAt(0);
            const tail = match[2] || '';
            if (!openFence) {
                openFence = {
                    markerChar,
                    length: marker.length
                };
                continue;
            }

            const isClosing =
                markerChar === openFence.markerChar &&
                marker.length >= openFence.length &&
                tail.trim() === '';
            if (isClosing) {
                openFence = null;
            }
        }

        if (!openFence) return source;
        return source +
            '\n' +
            openFence.markerChar.repeat(openFence.length);
    }

    return Object.freeze({
        preprocess,
        completeOpenFences
    });
});
