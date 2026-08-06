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
                output += wrapThoughts(unwrapThoughtsEnvelope(
                    source.slice(bodyStart, nestedOpening.index)));
                cursor = nestedOpening.index;
                continue;
            }

            if (closing) {
                output += wrapThoughts(unwrapThoughtsEnvelope(
                    source.slice(bodyStart, closing.index)));
                cursor = closing.index + closing[0].length;
                continue;
            }

            // 流式回复尚未收到结束标签：把当前已有内容临时包成完整围栏。
            output += wrapThoughts(unwrapThoughtsEnvelope(
                source.slice(bodyStart)));
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

        return result;
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
