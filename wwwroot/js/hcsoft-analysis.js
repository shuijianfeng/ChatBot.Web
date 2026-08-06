(function (root, factory) {
    'use strict';

    const api = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = api;
    }

    if (root) {
        root.HcsoftAnalysisController = api.HcsoftAnalysisController;
        if (root.hcsoftBridge) {
            root.hcsoftAnalysisController =
                new api.HcsoftAnalysisController(root.hcsoftBridge);
        }
    }
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
    'use strict';

    const OPAQUE_ID_PATTERN = /^[a-f0-9]{24}$/;
    const POLICY_VERSION = 'cost-policy-v1';
    const REQUEST_TIMEOUT_MS = 60 * 1000;
    const MAX_RESULT_BYTES = 512 * 1024;
    const MAX_RETRIES = 2;

    /**
     * 控制一次“桌面分片扫描、桌面精确累计、网页只接收紧凑结果”的全量分析。
     *
     * 本控制器不会保存每个原始分片。桌面返回一片进度后，网页只保留最新覆盖率；
     * 扫描结束时才接收汇总值和重点证据，从根源上避免重新形成超大 JavaScript 数组。
     */
    class HcsoftAnalysisController {
        constructor(bridge) {
            this.bridge = bridge || null;
            this.activeAnalysisSessionId = '';
            this.cancelRequested = false;
        }

        get isSupported() {
            return Boolean(
                this.bridge &&
                this.bridge.supportsChunkedAnalysis === true &&
                typeof this.bridge.request === 'function');
        }

        /**
         * 根据当前选择的报告技能确定累计用途。
         * 用途只影响最终提示和证据优先级，所有金额仍使用同一 cost-policy-v1。
         */
        resolveAnalysisType(skillName) {
            const normalized = String(skillName || '').toLowerCase();
            if (normalized.includes('audit')) return 'audit';
            if (normalized.includes('comparison')) return 'comparison';
            return 'analysis';
        }

        /**
         * 扫描当前摘要快照的全部节点、材料、费用和配置。
         * 数据片数量不占模型的十轮限制；十轮只约束模型问答轮次。
         */
        async scan(skillName) {
            if (!this.isSupported) {
                return {
                    status: 'unsupported',
                    analysis: null,
                    addedRecords: 0
                };
            }

            const snapshotId = this.bridge.currentSnapshotId;
            if (!OPAQUE_ID_PATTERN.test(snapshotId)) {
                return {
                    status: 'stale_context',
                    analysis: null,
                    addedRecords: 0
                };
            }

            this.cancelRequested = false;
            this.bridge.setStatus('scanning', {
                processed: 0,
                total: 0
            });

            let ready;
            try {
                ready = await this.requestWithRetry('analysis.start', {
                    snapshotId,
                    analysisType: this.resolveAnalysisType(skillName),
                    scope: 'unitProject',
                    policyVersion: POLICY_VERSION
                });
            } catch (error) {
                this.bridge.setStatus('error');
                throw error;
            }

            if (!this.isValidReady(ready, snapshotId)) {
                const status = ready && ready.status
                    ? ready.status
                    : 'invalid_analysis_result';
                this.bridge.setStatus('error');
                return { status, analysis: null, addedRecords: 0 };
            }

            this.activeAnalysisSessionId = ready.analysisSessionId;
            const expectedTotal = ready.manifest.total;
            let cursor = ready.cursor;
            let processedTotal = 0;
            let hasMore = true;
            let chunkCount = 0;
            // 每片至少消费一个非空数据集记录；额外余量用于五个空数据集的跳过片。
            const loopGuard = Math.max(10, expectedTotal + 10);

            try {
                while (hasMore) {
                    if (this.cancelRequested) {
                        await this.cancel();
                        return {
                            status: 'cancelled',
                            analysis: null,
                            addedRecords: 0
                        };
                    }
                    if (++chunkCount > loopGuard) {
                        throw this.createError(
                            'ANALYSIS_LOOP_GUARD',
                            '全量分析分片游标没有正常结束。');
                    }

                    const chunk = await this.requestWithRetry(
                        'analysis.chunk.next',
                        {
                            analysisSessionId: ready.analysisSessionId,
                            cursor
                        });
                    if (!this.isValidChunk(chunk, ready, processedTotal)) {
                        const status = chunk && chunk.status
                            ? chunk.status
                            : 'invalid_analysis_chunk';
                        await this.cancel();
                        this.bridge.setStatus('error');
                        return {
                            status,
                            analysis: null,
                            addedRecords: 0
                        };
                    }

                    processedTotal = chunk.coverage.processedTotal;
                    hasMore = chunk.hasMore;
                    cursor = chunk.nextCursor || '';
                    this.bridge.setStatus('scanning', {
                        processed: processedTotal,
                        total: expectedTotal
                    });
                }

                const result = await this.requestWithRetry(
                    'analysis.result.get',
                    {
                        analysisSessionId: ready.analysisSessionId
                    });
                if (!this.isValidResult(result, ready)) {
                    const status = result && result.status
                        ? result.status
                        : 'invalid_analysis_result';
                    await this.cancel();
                    this.bridge.setStatus('error');
                    return {
                        status,
                        analysis: null,
                        addedRecords: 0
                    };
                }

                this.bridge.rememberAnalysisTargets(result.analysis);
                this.bridge.setStatus('attached');
                return {
                    status: 'ok',
                    analysis: result.analysis,
                    addedRecords: result.analysis.coverage.processedTotal
                };
            } catch (error) {
                this.bridge.setStatus(
                    error && error.code === 'TIMEOUT' ? 'timeout' : 'error');
                throw error;
            }
        }

        /**
         * 主动取消当前桌面分析会话。重复取消是安全的。
         */
        async cancel() {
            this.cancelRequested = true;
            const analysisSessionId = this.activeAnalysisSessionId;
            this.activeAnalysisSessionId = '';
            if (!this.bridge ||
                !OPAQUE_ID_PATTERN.test(analysisSessionId)) {
                return { cancelled: false };
            }

            try {
                return await this.bridge.request(
                    'analysis.cancel',
                    { analysisSessionId },
                    REQUEST_TIMEOUT_MS);
            } catch (error) {
                console.warn(
                    'HCSoft analysis cancellation failed:',
                    error.message);
                return { cancelled: false };
            }
        }

        /**
         * 同一 opaque cursor 最多重试两次。
         * 桌面端按 chunkId 幂等累计，因此超时后重发不会造成金额重复。
         */
        async requestWithRetry(type, payload) {
            let lastError;
            for (let attempt = 0; attempt <= MAX_RETRIES; attempt++) {
                try {
                    return await this.bridge.request(
                        type,
                        payload,
                        REQUEST_TIMEOUT_MS);
                } catch (error) {
                    lastError = error;
                    if (attempt >= MAX_RETRIES ||
                        (error && error.code &&
                         !['TIMEOUT', 'BRIDGE_ERROR'].includes(error.code))) {
                        throw error;
                    }
                }
            }
            throw lastError || this.createError(
                'ANALYSIS_FAILED',
                '全量分析请求失败。');
        }

        isValidReady(result, snapshotId) {
            const manifest = result && result.manifest;
            const countKeys = [
                'nodes',
                'materials',
                'fees',
                'unitConfigItems',
                'sectionConfigItems',
                'total'
            ];
            return Boolean(
                result &&
                result.status === 'ok' &&
                OPAQUE_ID_PATTERN.test(result.analysisSessionId) &&
                result.snapshotId === snapshotId &&
                OPAQUE_ID_PATTERN.test(result.unitProjectKey) &&
                result.policyVersion === POLICY_VERSION &&
                OPAQUE_ID_PATTERN.test(result.cursor) &&
                manifest &&
                OPAQUE_ID_PATTERN.test(manifest.sourceFingerprint) &&
                manifest.rowTypes &&
                typeof manifest.rowTypes === 'object' &&
                countKeys.every(key =>
                    Number.isInteger(manifest[key]) &&
                    manifest[key] >= 0) &&
                manifest.total ===
                    manifest.nodes +
                    manifest.materials +
                    manifest.fees +
                    manifest.unitConfigItems +
                    manifest.sectionConfigItems &&
                this.isValidManifestRowTypes(manifest)
            );
        }

        isValidManifestRowTypes(manifest) {
            const required = [
                'BD',
                'TREE',
                'GROUP',
                'XM',
                'QD',
                'DE',
                'CL'
            ];
            if (!manifest || !manifest.rowTypes ||
                !required.every(name =>
                    Number.isInteger(manifest.rowTypes[name]) &&
                    manifest.rowTypes[name] >= 0)) {
                return false;
            }

            const entries = Object.entries(manifest.rowTypes);
            return entries.every(([name, count]) =>
                name.length > 0 &&
                name.length <= 32 &&
                Number.isInteger(count) &&
                count >= 0) &&
                entries.reduce(
                    (sum, entry) => sum + entry[1],
                    0) === manifest.nodes;
        }

        isValidChunk(result, ready, previousProcessed) {
            const coverage = result && result.coverage;
            if (!result ||
                result.status !== 'ok' ||
                result.analysisSessionId !== ready.analysisSessionId ||
                result.snapshotId !== ready.snapshotId ||
                result.unitProjectKey !== ready.unitProjectKey ||
                !OPAQUE_ID_PATTERN.test(result.chunkId) ||
                !Number.isInteger(result.ordinal) ||
                result.ordinal < 1 ||
                !Number.isInteger(result.recordCount) ||
                result.recordCount < 0 ||
                result.recordCount >
                    (this.bridge.maxAnalysisChunkRecords || 50) ||
                typeof result.hasMore !== 'boolean' ||
                (result.hasMore
                    ? !OPAQUE_ID_PATTERN.test(result.nextCursor)
                    : result.nextCursor != null) ||
                !coverage ||
                !Number.isInteger(coverage.expectedTotal) ||
                coverage.expectedTotal !== ready.manifest.total ||
                !Number.isInteger(coverage.processedTotal) ||
                coverage.processedTotal < previousProcessed ||
                coverage.processedTotal > coverage.expectedTotal ||
                typeof coverage.complete !== 'boolean' ||
                !coverage.datasets ||
                !coverage.rowTypes ||
                !this.isValidCoverage(
                    coverage,
                    ready.manifest,
                    !result.hasMore)) {
                return false;
            }

            if (!result.hasMore &&
                (!coverage.complete ||
                 coverage.processedTotal !== coverage.expectedTotal)) {
                return false;
            }
            return true;
        }

        isValidResult(result, ready) {
            const analysis = result && result.analysis;
            if (!result ||
                result.status !== 'ok' ||
                !analysis ||
                analysis.schemaVersion !== 'analysis-1.0' ||
                analysis.analysisSessionId !== ready.analysisSessionId ||
                analysis.snapshotId !== ready.snapshotId ||
                analysis.unitProjectKey !== ready.unitProjectKey ||
                analysis.policyVersion !== POLICY_VERSION ||
                analysis.sourceFingerprint !==
                    ready.manifest.sourceFingerprint ||
                !analysis.coverage ||
                analysis.coverage.complete !== true ||
                analysis.coverage.processedTotal !==
                    ready.manifest.total ||
                !this.isValidCoverage(
                    analysis.coverage,
                    ready.manifest,
                    true) ||
                !analysis.aggregates ||
                !Array.isArray(analysis.itemEvidence) ||
                !Array.isArray(analysis.materialEvidence) ||
                !Array.isArray(analysis.feeEvidence) ||
                analysis.itemEvidence.length > 180 ||
                analysis.materialEvidence.length > 50 ||
                analysis.feeEvidence.length > 60 ||
                !analysis.itemEvidence.every(evidence =>
                    evidence &&
                    evidence.item &&
                    OPAQUE_ID_PATTERN.test(evidence.item.targetId))) {
                return false;
            }

            try {
                const maximum = Number.isInteger(
                    this.bridge.maxAnalysisResultBytes)
                    ? this.bridge.maxAnalysisResultBytes
                    : MAX_RESULT_BYTES;
                return new TextEncoder().encode(
                    JSON.stringify(analysis)).length <= maximum;
            } catch {
                return false;
            }
        }

        /**
         * 逐项核对五类数据集和标准行类型的 expected/processed。
         * 不能只相信一个 total 或 complete 布尔值，否则缺少某个数据集时仍可能
         * 被错误地当成“全量扫描完成”。
         */
        isValidCoverage(coverage, manifest, requireComplete) {
            if (!coverage || !manifest ||
                coverage.expectedTotal !== manifest.total) {
                return false;
            }

            const datasetNames = [
                'nodes',
                'materials',
                'fees',
                'unitConfigItems',
                'sectionConfigItems'
            ];
            let expectedTotal = 0;
            let processedTotal = 0;
            for (const name of datasetNames) {
                const item = coverage.datasets &&
                    coverage.datasets[name];
                if (!item ||
                    !Number.isInteger(item.expected) ||
                    !Number.isInteger(item.processed) ||
                    item.expected !== manifest[name] ||
                    item.processed < 0 ||
                    item.processed > item.expected ||
                    (requireComplete &&
                     item.processed !== item.expected)) {
                    return false;
                }
                expectedTotal += item.expected;
                processedTotal += item.processed;
            }
            if (expectedTotal !== coverage.expectedTotal ||
                processedTotal !== coverage.processedTotal ||
                (requireComplete && coverage.complete !== true)) {
                return false;
            }

            const manifestRowTypes = manifest.rowTypes || {};
            for (const name of Object.keys(manifestRowTypes)) {
                const item = coverage.rowTypes &&
                    coverage.rowTypes[name];
                if (!Number.isInteger(manifestRowTypes[name]) ||
                    manifestRowTypes[name] < 0 ||
                    !item ||
                    item.expected !== manifestRowTypes[name] ||
                    !Number.isInteger(item.processed) ||
                    item.processed < 0 ||
                    item.processed > item.expected ||
                    (requireComplete &&
                     item.processed !== item.expected)) {
                    return false;
                }
            }
            return true;
        }

        createError(code, message) {
            const error = new Error(message);
            error.code = code;
            return error;
        }
    }

    return { HcsoftAnalysisController };
});
