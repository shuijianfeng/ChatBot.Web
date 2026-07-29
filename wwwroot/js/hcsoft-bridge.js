(function () {
    'use strict';

    const PROTOCOL_VERSION = '1.0';
    const REQUEST_TIMEOUT_MS = 5000;
    // context.get / context.summary.get 可能在桌面端显示人工授权窗口，
    // 不能沿用普通桥接消息的5秒超时。
    // 其他请求仍保持5秒，以便桥断开时快速退化为普通聊天。
    const CONTEXT_REQUEST_TIMEOUT_MS = 5 * 60 * 1000;
    const MAX_TARGETS = 300;
    const MAX_CONTEXT_BYTES = 512 * 1024;
    const MAX_SEARCH_PAGE_SIZE = 50;
    const MAX_DETAIL_TARGETS = 20;
    const MAX_STAGED_CONTEXT_ITEMS = 220;
    const MAX_SEARCH_PAGES = 6;
    const MAX_STRUCTURE_CONTEXT_ITEMS = 5;
    const MAX_MODEL_SEARCH_QUERIES = 10;
    const MAX_MODEL_SEARCH_ROUNDS = 10;
    const OPAQUE_ID_PATTERN = /^[a-f0-9]{24}$/;

    class HcsoftBridge {
        constructor() {
            const query = new URLSearchParams(window.location.search);
            this.bridgeSessionId = query.get('bridgeSession') || '';
            this.webview = window.chrome && window.chrome.webview;
            this.pending = new Map();
            this.validTargets = new Set();
            this.currentUnitProjectKey = '';
            this.currentSnapshotId = '';
            this.currentSummary = null;
            this.lastQuery = '';
            this.connected = false;
            this.capabilities = [];
            // 工程数据默认关闭。即使 WebView2 桥已经握手成功，也必须由用户点击状态按钮后
            // 才允许请求和附加工程上下文。
            this.contextAttachmentEnabled = false;
            this.statusButton = null;
            this.status = 'unavailable';

            if (!this.webview || !this.bridgeSessionId) {
                return;
            }

            this.webview.addEventListener('message', event => this.handleMessage(event.data));
            this.setStatus('connecting');
            this.hello().catch(error => {
                console.warn('HCSoft bridge handshake failed:', error.message);
                this.setStatus('unavailable');
            });
        }

        get isAvailable() {
            return Boolean(this.webview && this.bridgeSessionId);
        }

        get maxModelSearchRounds() {
            return MAX_MODEL_SEARCH_ROUNDS;
        }

        async hello() {
            const payload = await this.request('bridge.hello', {});
            this.connected = true;
            this.capabilities = Array.isArray(payload.capabilities) ? payload.capabilities : [];
            // “桥已连接”不等于“工程数据已附加”。默认仍以未附加状态等待用户主动点击。
            this.setStatus('detached');
            return payload;
        }

        get shouldAttachContext() {
            return this.contextAttachmentEnabled;
        }

        get supportsStagedContext() {
            return this.capabilities.includes('context.staged-read');
        }

        get supportsPagedQueryV2() {
            return this.capabilities.includes('context.paged-query-v2');
        }

        /**
         * 旧桌面端兼容路径：一次读取最多300条、512 KiB的完整上下文。
         * 新桌面端不再优先调用本方法，但保留它可以保证 ChatBot.Web 先发布时不影响旧客户端。
         */
        async getLegacyContext(query, forcePrompt) {
            this.lastQuery = typeof query === 'string' ? query : '';
            if (!this.isAvailable) {
                return { status: 'unavailable', context: null };
            }

            this.setStatus('attaching');
            try {
                const result = await this.request(
                    'context.get',
                    {
                        query: this.lastQuery.slice(0, 4000),
                        forcePrompt: Boolean(forcePrompt)
                    },
                    CONTEXT_REQUEST_TIMEOUT_MS);

                if (result && result.status === 'ok' && this.isValidContext(result.context)) {
                    this.rememberTargets(result.context);
                    this.contextAttachmentEnabled = true;
                    this.setStatus('attached');
                    return result;
                }

                // 授权被拒绝、没有当前单位工程或返回数据非法时关闭自动附加。
                // 用户可以在条件具备后再次点击状态按钮重试。
                this.contextAttachmentEnabled = false;
                this.clearTargets();
                this.setStatus(result && result.status ? result.status : 'error');
                return result || { status: 'error', context: null };
            } catch (error) {
                this.contextAttachmentEnabled = false;
                this.clearTargets();
                this.setStatus(error.code === 'TIMEOUT' ? 'timeout' : 'error');
                throw error;
            }
        }

        /**
         * 创建轻量工程摘要和10分钟内存快照。
         * 摘要只含工程概要、汇总和记录数量；全部清单/定额不会在此阶段传到网页。
         */
        async getSummary(query, forcePrompt) {
            this.lastQuery = typeof query === 'string' ? query : '';
            const result = await this.request(
                'context.summary.get',
                {
                    forcePrompt: Boolean(forcePrompt)
                },
                CONTEXT_REQUEST_TIMEOUT_MS);

            if (!result || result.status !== 'ok') {
                return result || { status: 'error', summary: null };
            }
            if (!this.isValidSummaryResult(result)) {
                return { status: 'error', summary: null };
            }

            // 新摘要会让桌面端旧快照失效；网页同步清理旧回答可用的定位 ID。
            this.clearTargets();
            this.currentSnapshotId = result.snapshotId;
            this.currentUnitProjectKey = result.unitProjectKey;
            this.currentSummary = result.summary;
            return result;
        }

        /**
         * 为一次提问按顺序读取“最新摘要 → 相关目录 → 少量完整详情”。
         * 任一步拒绝、超时或校验失败都返回空上下文，由聊天逻辑继续普通问答。
         */
        async prepareContext(query) {
            this.lastQuery = typeof query === 'string' ? query : '';
            if (!this.isAvailable) {
                return { status: 'unavailable', context: null };
            }

            if (!this.supportsStagedContext) {
                return this.getLegacyContext(this.lastQuery, false);
            }

            // 分段请求进行期间禁用状态按钮，避免用户在快照重建到一半时主动释放它。
            this.setStatus('attaching');
            try {
                // 每次提问重新取得摘要，以便识别单位工程切换和工程快照过期。
                // 同一单位工程已有 Allowed 状态时桌面端不会重复弹出授权框。
                const summaryResult = await this.getSummary(this.lastQuery, false);
                if (!summaryResult || summaryResult.status !== 'ok') {
                    this.handleContextFailure(
                        summaryResult && summaryResult.status
                            ? summaryResult.status
                            : 'error');
                    return summaryResult || { status: 'error', context: null };
                }

                if (this.supportsPagedQueryV2) {
                    const pagedResult = await this.preparePagedStagedContext(
                        summaryResult,
                        this.lastQuery);
                    if (!pagedResult ||
                        pagedResult.status !== 'ok' ||
                        !this.isValidContext(pagedResult.context)) {
                        const status = pagedResult && pagedResult.status
                            ? pagedResult.status
                            : 'error';
                        this.handleContextFailure(status);
                        return pagedResult || { status, context: null };
                    }

                    this.rememberTargets(pagedResult.context);
                    this.contextAttachmentEnabled = true;
                    this.setStatus('attached');
                    return pagedResult;
                }

                const searchResult = await this.request('context.search', {
                    snapshotId: summaryResult.snapshotId,
                    query: this.lastQuery.slice(0, 4000),
                    cursor: '',
                    pageSize: MAX_SEARCH_PAGE_SIZE
                });
                if (!this.isValidSearchResult(searchResult, summaryResult)) {
                    const status = searchResult && searchResult.status
                        ? searchResult.status
                        : 'error';
                    this.handleContextFailure(status);
                    return { status, context: null };
                }

                const targetIds = [];
                const appendTarget = targetId => {
                    if (OPAQUE_ID_PATTERN.test(targetId) &&
                        !targetIds.includes(targetId) &&
                        targetIds.length < MAX_DETAIL_TARGETS) {
                        targetIds.push(targetId);
                    }
                };

                // 当前选中节点可能不在某个自定义搜索页中，摘要已经合法暴露它，
                // 因此优先保证其详情进入最终上下文。
                const selectedTargetId =
                    summaryResult.summary &&
                    summaryResult.summary.selection &&
                    summaryResult.summary.selection.targetId;
                if (typeof selectedTargetId === 'string') {
                    appendTarget(selectedTargetId);
                }
                for (const item of searchResult.items) {
                    appendTarget(item.targetId);
                }

                const detailsResult = await this.request('context.details.get', {
                    snapshotId: summaryResult.snapshotId,
                    query: this.lastQuery.slice(0, 4000),
                    targetIds
                });
                if (!detailsResult ||
                    detailsResult.status !== 'ok' ||
                    !this.isValidContext(detailsResult.context) ||
                    detailsResult.context.schemaVersion !== '2.0' ||
                    detailsResult.context.snapshotId !== summaryResult.snapshotId ||
                    detailsResult.context.unitProjectKey !== summaryResult.unitProjectKey) {
                    const status = detailsResult && detailsResult.status
                        ? detailsResult.status
                        : 'error';
                    this.handleContextFailure(status);
                    return { status, context: null };
                }

                this.rememberTargets(detailsResult.context);
                this.contextAttachmentEnabled = true;
                this.setStatus('attached');
                return detailsResult;
            } catch (error) {
                this.handleContextFailure(error.code === 'TIMEOUT' ? 'timeout' : 'error');
                throw error;
            }
        }

        /**
         * 保留原公开方法名，供尚未更新的 chat.js 或其他页面代码调用。
         */
        /**
         * 新版分段检索：根据桌面端返回的 matchRank 自动读取后续目录页，
         * 再把目标按每批最多 20 条展开，并在网页端合并、去重和执行最终大小限制。
         */
        async preparePagedStagedContext(summaryResult, query) {
            const searchState = await this.readRelevantSearchPages(
                summaryResult,
                query);
            if (searchState.status !== 'ok') {
                return { status: searchState.status, context: null };
            }

            const detailState = await this.readDetailBatches(
                summaryResult,
                query,
                searchState);
            if (detailState.status !== 'ok' || !detailState.context) {
                return {
                    status: detailState.status || 'error',
                    context: null
                };
            }

            const finalTargetIds = detailState.context.items
                .map(item => item && item.targetId)
                .filter(targetId =>
                    typeof targetId === 'string' &&
                    OPAQUE_ID_PATTERN.test(targetId));
            const commitResult = await this.request('context.targets.commit', {
                snapshotId: summaryResult.snapshotId,
                targetIds: finalTargetIds
            });
            if (!commitResult ||
                commitResult.status !== 'ok' ||
                commitResult.unitProjectKey !== summaryResult.unitProjectKey ||
                commitResult.acceptedCount !== finalTargetIds.length) {
                return {
                    status: commitResult && commitResult.status
                        ? commitResult.status
                        : 'error',
                    context: null
                };
            }

            return {
                status: 'ok',
                message: '已按问题检索并分批附加工程数据。',
                context: detailState.context
            };
        }

        /**
         * 根据模型在回答中提出的补充检索词继续读取同一桌面快照。
         * 最新检索结果优先进入上下文；达到 300 条或 512 KiB 时，
         * 会淘汰较早的低优先级详情，而不会突破既有安全边界。
         */
        async extendContext(queries, currentContext) {
            if (!this.supportsPagedQueryV2 ||
                !this.currentSummary ||
                !this.isValidContext(currentContext) ||
                currentContext.schemaVersion !== '2.0' ||
                currentContext.snapshotId !== this.currentSnapshotId ||
                currentContext.unitProjectKey !== this.currentUnitProjectKey) {
                return { status: 'unavailable', context: currentContext, addedRecords: 0 };
            }

            const normalizedQueries = Array.from(new Set(
                (Array.isArray(queries) ? queries : [])
                    .filter(query => typeof query === 'string')
                    .map(query => query.trim().slice(0, 4000))
                    .filter(query => query.length >= 2)))
                .slice(0, MAX_MODEL_SEARCH_QUERIES);
            if (normalizedQueries.length === 0) {
                return { status: 'invalid_query', context: currentContext, addedRecords: 0 };
            }

            const summaryResult = {
                status: 'ok',
                snapshotId: this.currentSnapshotId,
                unitProjectKey: this.currentUnitProjectKey,
                summary: this.currentSummary
            };
            let mergedContext = JSON.parse(JSON.stringify(currentContext));
            let addedRecords = 0;

            for (const query of normalizedQueries) {
                const searchState = await this.readRelevantSearchPages(
                    summaryResult,
                    query);
                if (searchState.status !== 'ok') {
                    return {
                        status: searchState.status,
                        context: mergedContext,
                        addedRecords
                    };
                }

                const detailState = await this.readDetailBatches(
                    summaryResult,
                    query,
                    searchState);
                if (detailState.status !== 'ok' || !detailState.context) {
                    return {
                        status: detailState.status || 'error',
                        context: mergedContext,
                        addedRecords
                    };
                }

                const mergeResult = this.mergeContextsWithLatestPriority(
                    mergedContext,
                    detailState.context);
                mergedContext = mergeResult.context;
                addedRecords += mergeResult.addedRecords;
            }

            if (!this.isValidContext(mergedContext)) {
                return {
                    status: 'context_too_large',
                    context: currentContext,
                    addedRecords: 0
                };
            }

            addedRecords = this.countNewContextRecords(
                currentContext,
                mergedContext);
            const finalTargetIds = mergedContext.items.map(item => item.targetId);
            const commitResult = await this.request('context.targets.commit', {
                snapshotId: this.currentSnapshotId,
                targetIds: finalTargetIds
            });
            if (!commitResult ||
                commitResult.status !== 'ok' ||
                commitResult.unitProjectKey !== this.currentUnitProjectKey ||
                commitResult.acceptedCount !== finalTargetIds.length) {
                return {
                    status: commitResult && commitResult.status
                        ? commitResult.status
                        : 'error',
                    context: currentContext,
                    addedRecords: 0
                };
            }

            this.rememberTargets(mergedContext);
            return {
                status: 'ok',
                context: mergedContext,
                addedRecords
            };
        }

        countNewContextRecords(originalContext, mergedContext) {
            const originalItems = new Set(
                originalContext.items.map(item => item.targetId));
            const originalMaterials = new Set(
                originalContext.materials.map(item => this.getMaterialKey(item)));
            const originalFees = new Set(
                originalContext.fees.map(item => this.getFeeKey(item)));
            return mergedContext.items.filter(
                item => !originalItems.has(item.targetId)).length +
                mergedContext.materials.filter(
                    item => !originalMaterials.has(this.getMaterialKey(item))).length +
                mergedContext.fees.filter(
                    item => !originalFees.has(this.getFeeKey(item))).length;
        }

        mergeContextsWithLatestPriority(existingContext, latestContext) {
            const oldItemIds = new Set(existingContext.items.map(item => item.targetId));
            const oldMaterialKeys = new Set(
                existingContext.materials.map(item => this.getMaterialKey(item)));
            const oldFeeKeys = new Set(
                existingContext.fees.map(item => this.getFeeKey(item)));

            const merged = JSON.parse(JSON.stringify(existingContext));
            merged.items = [];
            merged.materials = [];
            merged.fees = [];

            const itemIds = new Set();
            const materialKeys = new Set();
            const feeKeys = new Set();
            let addedRecords = 0;

            const appendItem = item => {
                if (!item || itemIds.has(item.targetId)) return;
                if (this.tryAppendContextItem(merged, item)) {
                    itemIds.add(item.targetId);
                    if (!oldItemIds.has(item.targetId)) addedRecords++;
                }
            };
            const appendMaterial = item => {
                const key = this.getMaterialKey(item);
                if (!key || materialKeys.has(key)) return;
                if (this.tryAppendContextRecord(merged, 'materials', item)) {
                    materialKeys.add(key);
                    if (!oldMaterialKeys.has(key)) addedRecords++;
                }
            };
            const appendFee = item => {
                const key = this.getFeeKey(item);
                if (!key || feeKeys.has(key)) return;
                if (this.tryAppendContextRecord(merged, 'fees', item)) {
                    feeKeys.add(key);
                    if (!oldFeeKeys.has(key)) addedRecords++;
                }
            };

            latestContext.items.forEach(appendItem);
            latestContext.materials.forEach(appendMaterial);
            latestContext.fees.forEach(appendFee);
            existingContext.items.forEach(appendItem);
            existingContext.materials.forEach(appendMaterial);
            existingContext.fees.forEach(appendFee);

            if (merged.selection &&
                merged.selection.targetId &&
                !itemIds.has(merged.selection.targetId)) {
                merged.selection.targetId = null;
            }
            merged.search = {
                totalAvailableItems: Math.max(
                    Number(existingContext.search &&
                        existingContext.search.totalAvailableItems) || 0,
                    Number(latestContext.search &&
                        latestContext.search.totalAvailableItems) || 0),
                returnedItems: merged.items.length,
                hasMore: Boolean(
                    (existingContext.search && existingContext.search.hasMore) ||
                    (latestContext.search && latestContext.search.hasMore))
            };
            return { context: merged, addedRecords };
        }

        tryAppendContextRecord(context, collectionName, record) {
            if (!record ||
                !['materials', 'fees'].includes(collectionName) ||
                context.items.length + context.materials.length + context.fees.length >=
                    MAX_TARGETS) {
                return false;
            }

            context[collectionName].push(record);
            if (this.getContextByteLength(context) > MAX_CONTEXT_BYTES) {
                context[collectionName].pop();
                return false;
            }
            return true;
        }

        getMaterialKey(item) {
            if (!item) return '';
            return [
                item.code || '',
                item.name || '',
                item.specification || '',
                item.unit || ''
            ].join('\u001f');
        }

        getFeeKey(item) {
            if (!item) return '';
            return [
                item.sectionName || '',
                item.code || '',
                item.name || ''
            ].join('\u001f');
        }

        async readRelevantSearchPages(summaryResult, query) {
            const selectedTargetId =
                summaryResult.summary &&
                summaryResult.summary.selection &&
                summaryResult.summary.selection.targetId;
            const selectedIds = [];
            if (typeof selectedTargetId === 'string' &&
                OPAQUE_ID_PATTERN.test(selectedTargetId)) {
                selectedIds.push(selectedTargetId);
            }

            const relevantItems = [];
            const structureItems = [];
            const seenCatalogTargets = new Set();
            let cursor = '';
            let threshold = null;
            let totalCount = 0;
            let pagesRead = 0;
            let hasMore = false;
            let stoppedByLimit = false;

            while (pagesRead < MAX_SEARCH_PAGES) {
                const result = await this.request('context.search', {
                    snapshotId: summaryResult.snapshotId,
                    query: String(query || '').slice(0, 4000),
                    cursor,
                    pageSize: MAX_SEARCH_PAGE_SIZE
                });
                if (!this.isValidSearchResult(result, summaryResult)) {
                    return {
                        status: result && result.status
                            ? result.status
                            : 'error'
                    };
                }

                pagesRead++;
                totalCount = result.totalCount;
                hasMore = result.hasMore;

                if (threshold == null) {
                    const highestRank = result.items.reduce(
                        (rank, item) => Math.max(
                            rank,
                            Number.isInteger(item.matchRank)
                                ? item.matchRank
                                : 0),
                        0);
                    threshold = highestRank >= 2
                        ? 2
                        : (highestRank === 1 ? 1 : 0);
                }

                let pageHasRelevantItem = false;
                for (const item of result.items) {
                    if (!item || seenCatalogTargets.has(item.targetId)) {
                        continue;
                    }
                    seenCatalogTargets.add(item.targetId);

                    const rank = Number.isInteger(item.matchRank)
                        ? item.matchRank
                        : 0;
                    if (threshold === 0) {
                        if (relevantItems.length < MAX_DETAIL_TARGETS) {
                            relevantItems.push(item);
                        }
                        continue;
                    }

                    if (rank >= threshold) {
                        pageHasRelevantItem = true;
                        if (relevantItems.length < MAX_STAGED_CONTEXT_ITEMS) {
                            relevantItems.push(item);
                        } else {
                            stoppedByLimit = true;
                        }
                    } else if (
                        structureItems.length < MAX_STRUCTURE_CONTEXT_ITEMS &&
                        ['BD', 'TREE', 'GROUP'].includes(item.rowType)) {
                        structureItems.push(item);
                    }
                }

                if (threshold === 0 ||
                    !result.hasMore ||
                    stoppedByLimit ||
                    !pageHasRelevantItem) {
                    break;
                }
                cursor = result.nextCursor;
            }

            if (pagesRead >= MAX_SEARCH_PAGES && hasMore) {
                stoppedByLimit = true;
            }

            const targetIds = [];
            const appendTarget = targetId => {
                if (OPAQUE_ID_PATTERN.test(targetId) &&
                    !targetIds.includes(targetId) &&
                    targetIds.length < MAX_STAGED_CONTEXT_ITEMS) {
                    targetIds.push(targetId);
                }
            };
            selectedIds.forEach(appendTarget);
            relevantItems.forEach(item => appendTarget(item.targetId));
            structureItems.forEach(item => appendTarget(item.targetId));

            return {
                status: 'ok',
                targetIds,
                totalCount,
                catalogReturnedCount: seenCatalogTargets.size,
                hasMore: hasMore || stoppedByLimit
            };
        }

        async readDetailBatches(summaryResult, query, searchState) {
            const targetIds = searchState.targetIds;
            const batches = [];
            for (let index = 0; index < targetIds.length; index += MAX_DETAIL_TARGETS) {
                batches.push(targetIds.slice(index, index + MAX_DETAIL_TARGETS));
            }
            if (batches.length === 0) {
                // 即使没有工程树命中，也读取一次公共的材料、费用、汇总和配置数据。
                batches.push([]);
            }

            let mergedContext = null;
            let stoppedByPayloadLimit = false;
            for (let batchIndex = 0; batchIndex < batches.length; batchIndex++) {
                const detailsResult = await this.request('context.details.get', {
                    snapshotId: summaryResult.snapshotId,
                    query: String(query || '').slice(0, 4000),
                    targetIds: batches[batchIndex],
                    includeSupplementaryData: batchIndex === 0,
                    deferTargetActivation: true
                });
                if (!this.isValidStagedDetailsResult(
                        detailsResult,
                        summaryResult)) {
                    return {
                        status: detailsResult && detailsResult.status
                            ? detailsResult.status
                            : 'error',
                        context: null
                    };
                }

                if (!mergedContext) {
                    mergedContext = JSON.parse(
                        JSON.stringify(detailsResult.context));
                    continue;
                }

                const existingIds = new Set(
                    mergedContext.items.map(item => item.targetId));
                for (const item of detailsResult.context.items) {
                    if (existingIds.has(item.targetId)) continue;
                    if (!this.tryAppendContextItem(mergedContext, item)) {
                        stoppedByPayloadLimit = true;
                        break;
                    }
                    existingIds.add(item.targetId);
                }

                if (stoppedByPayloadLimit) break;
            }

            if (!mergedContext) {
                return { status: 'error', context: null };
            }

            mergedContext.search = {
                totalAvailableItems: searchState.totalCount,
                returnedItems: mergedContext.items.length,
                hasMore:
                    searchState.hasMore ||
                    stoppedByPayloadLimit ||
                    searchState.targetIds.length > mergedContext.items.length ||
                    searchState.totalCount > mergedContext.items.length
            };

            if (!this.isValidContext(mergedContext)) {
                return { status: 'error', context: null };
            }
            return { status: 'ok', context: mergedContext };
        }

        isValidStagedDetailsResult(result, summaryResult) {
            return Boolean(
                result &&
                result.status === 'ok' &&
                this.isValidContext(result.context) &&
                result.context.schemaVersion === '2.0' &&
                result.context.snapshotId === summaryResult.snapshotId &&
                result.context.unitProjectKey === summaryResult.unitProjectKey
            );
        }

        tryAppendContextItem(context, item) {
            if (!item ||
                !OPAQUE_ID_PATTERN.test(item.targetId) ||
                context.items.length >= MAX_STAGED_CONTEXT_ITEMS ||
                context.items.length + context.materials.length + context.fees.length >=
                    MAX_TARGETS) {
                return false;
            }

            context.items.push(item);
            if (this.getContextByteLength(context) > MAX_CONTEXT_BYTES) {
                context.items.pop();
                return false;
            }
            return true;
        }

        getContextByteLength(context) {
            try {
                return new TextEncoder().encode(JSON.stringify(context)).length;
            } catch {
                return Number.MAX_SAFE_INTEGER;
            }
        }

        async getContext(query, forcePrompt) {
            this.lastQuery = typeof query === 'string' ? query : '';
            if (forcePrompt) {
                return this.requestAuthorization();
            }
            return this.prepareContext(this.lastQuery);
        }

        async requestAuthorization() {
            if (!this.isAvailable) {
                return { status: 'unavailable', context: null };
            }

            this.setStatus('attaching');
            if (!this.supportsStagedContext) {
                return this.getLegacyContext(this.lastQuery, true);
            }

            try {
                const result = await this.getSummary(this.lastQuery, true);
                if (result && result.status === 'ok') {
                    this.contextAttachmentEnabled = true;
                    this.setStatus('attached');
                    return result;
                }

                this.handleContextFailure(
                    result && result.status ? result.status : 'error');
                return result || { status: 'error', context: null };
            } catch (error) {
                this.handleContextFailure(error.code === 'TIMEOUT' ? 'timeout' : 'error');
                throw error;
            }
        }

        /**
         * 关闭工程数据附加。
         * 清除本次上下文的定位目标，避免关闭后仍可使用旧回答中的 targetId 操作桌面界面。
         */
        detachContext() {
            const snapshotId = this.currentSnapshotId;
            this.contextAttachmentEnabled = false;
            this.clearTargets();
            this.setStatus('detached');

            // 释放只是内存清理，不等待其结果，避免点击状态按钮时阻塞界面。
            // 窗口/网页已关闭导致回传失败时桌面端仍会在超时或 GcForm 关闭时释放。
            if (this.isAvailable && OPAQUE_ID_PATTERN.test(snapshotId)) {
                this.request('context.snapshot.release', { snapshotId })
                    .catch(error =>
                        console.warn('HCSoft snapshot release failed:', error.message));
            }
        }

        handleContextFailure(status) {
            this.contextAttachmentEnabled = false;
            this.clearTargets();
            // 桌面端还可能返回 snapshot_expired、stale_context、invalid_cursor 等
            // 细分错误码。状态按钮只展示稳定的用户状态，未知细分码统一归为可点击重试的 error。
            const visibleStatuses = new Set([
                'denied',
                'no_active_unit_project',
                'timeout',
                'unavailable'
            ]);
            this.setStatus(visibleStatuses.has(status) ? status : 'error');
        }

        isValidLocateAction(action) {
            const allowedKeys = new Set(['type', 'unitProjectKey', 'targetId', 'label']);
            return Boolean(
                action &&
                Object.keys(action).every(key => allowedKeys.has(key)) &&
                action.type === 'locate' &&
                typeof action.unitProjectKey === 'string' &&
                action.unitProjectKey === this.currentUnitProjectKey &&
                typeof action.targetId === 'string' &&
                this.validTargets.has(action.targetId) &&
                typeof action.label === 'string' &&
                action.label.trim().length > 0 &&
                action.label.length <= 80
            );
        }

        extractActions(content) {
            const actions = [];
            let foundTag = false;
            let cleanContent = String(content || '').replace(
                /<hcsoft_action>([\s\S]*?)<\/hcsoft_action>/g,
                (tag, json) => {
                    foundTag = true;
                    try {
                        const action = JSON.parse(json);
                        if (actions.length < 10 && this.isValidLocateAction(action)) {
                            actions.push(action);
                        }
                    } catch (error) {
                        console.warn('Ignored invalid HCSoft action:', error.message);
                    }
                    return '';
                });

            const danglingActionIndex = cleanContent.indexOf('<hcsoft_action>');
            if (danglingActionIndex >= 0) {
                foundTag = true;
                cleanContent = cleanContent.slice(0, danglingActionIndex);
            }

            return {
                foundTag,
                actions,
                content: cleanContent.replace(/\n{3,}/g, '\n\n').trimEnd()
            };
        }

        /**
         * 解析模型发出的只读补充检索请求。该标签仅用于网页和桌面桥之间的
         * 自动检索循环，不会显示给用户，也不会作为助手消息写入会话。
         */
        extractSearchRequests(content) {
            const queries = [];
            let foundTag = false;
            let cleanContent = String(content || '').replace(
                /<hcsoft_search>([\s\S]*?)<\/hcsoft_search>/g,
                (tag, json) => {
                    foundTag = true;
                    try {
                        const request = JSON.parse(json);
                        const allowedKeys = new Set(['queries', 'reason']);
                        if (!request ||
                            !Object.keys(request).every(key => allowedKeys.has(key)) ||
                            !Array.isArray(request.queries) ||
                            (request.reason != null &&
                             (typeof request.reason !== 'string' ||
                              request.reason.length > 200))) {
                            return '';
                        }

                        for (const query of request.queries) {
                            if (typeof query !== 'string') continue;
                            const normalized = query.trim();
                            if (normalized.length >= 2 &&
                                normalized.length <= 4000 &&
                                !queries.includes(normalized) &&
                                queries.length < MAX_MODEL_SEARCH_QUERIES) {
                                queries.push(normalized);
                            }
                        }
                    } catch (error) {
                        console.warn(
                            'Ignored invalid HCSoft search request:',
                            error.message);
                    }
                    return '';
                });

            const danglingIndex = cleanContent.indexOf('<hcsoft_search>');
            if (danglingIndex >= 0) {
                foundTag = true;
                cleanContent = cleanContent.slice(0, danglingIndex);
            }

            return {
                foundTag,
                queries,
                content: cleanContent.replace(/\n{3,}/g, '\n\n').trimEnd()
            };
        }

        async navigate(action) {
            if (!this.isValidLocateAction(action)) {
                const error = new Error('定位目标不属于本次工程上下文。');
                error.code = 'INVALID_TARGET';
                throw error;
            }

            const result = await this.request('navigate.request', {
                unitProjectKey: action.unitProjectKey,
                targetId: action.targetId,
                snapshotId: this.currentSnapshotId
            });
            if (!result || result.success !== true) {
                const error = new Error(result && result.message ? result.message : '定位失败。');
                error.code = result && result.code ? result.code : 'NAVIGATION_FAILED';
                throw error;
            }
            return result;
        }

        request(type, payload, timeoutMs = REQUEST_TIMEOUT_MS) {
            if (!this.webview || !this.bridgeSessionId) {
                return Promise.reject(new Error('HCSoft bridge is unavailable.'));
            }

            const id = this.createId();
            const envelope = {
                version: PROTOCOL_VERSION,
                id,
                type,
                bridgeSessionId: this.bridgeSessionId,
                payload: payload || {}
            };

            return new Promise((resolve, reject) => {
                const timer = window.setTimeout(() => {
                    this.pending.delete(id);
                    const error = new Error('HCSoft bridge request timed out.');
                    error.code = 'TIMEOUT';
                    reject(error);
                }, timeoutMs);

                this.pending.set(id, { resolve, reject, timer });
                try {
                    this.webview.postMessage(envelope);
                } catch (error) {
                    window.clearTimeout(timer);
                    this.pending.delete(id);
                    reject(error);
                }
            });
        }

        handleMessage(message) {
            let envelope = message;
            if (typeof message === 'string') {
                try {
                    envelope = JSON.parse(message);
                } catch {
                    return;
                }
            }

            if (!envelope ||
                envelope.version !== PROTOCOL_VERSION ||
                envelope.bridgeSessionId !== this.bridgeSessionId ||
                typeof envelope.id !== 'string') {
                return;
            }

            const pending = this.pending.get(envelope.id);
            if (!pending) return;

            window.clearTimeout(pending.timer);
            this.pending.delete(envelope.id);

            if (envelope.type === 'bridge.error') {
                const error = new Error(envelope.payload && envelope.payload.message
                    ? envelope.payload.message
                    : 'HCSoft bridge error.');
                error.code = envelope.payload && envelope.payload.code
                    ? envelope.payload.code
                    : 'BRIDGE_ERROR';
                pending.reject(error);
                return;
            }

            pending.resolve(envelope.payload || {});
        }

        isValidSummaryResult(result) {
            if (!result || result.status !== 'ok') {
                return false;
            }

            const summary = result.summary;
            if (!OPAQUE_ID_PATTERN.test(result.snapshotId) ||
                !OPAQUE_ID_PATTERN.test(result.unitProjectKey) ||
                !summary ||
                summary.schemaVersion !== '2.0' ||
                summary.unitProjectKey !== result.unitProjectKey ||
                !summary.unitProject ||
                !summary.counts ||
                !Array.isArray(summary.sections)) {
                return false;
            }

            const countKeys = [
                'nodes',
                'materials',
                'fees',
                'unitConfigItems',
                'sectionConfigItems'
            ];
            return countKeys.every(key =>
                Number.isInteger(summary.counts[key]) &&
                summary.counts[key] >= 0);
        }

        isValidSearchResult(result, summaryResult) {
            return Boolean(
                result &&
                result.status === 'ok' &&
                result.snapshotId === summaryResult.snapshotId &&
                result.unitProjectKey === summaryResult.unitProjectKey &&
                Array.isArray(result.items) &&
                result.items.length <= MAX_SEARCH_PAGE_SIZE &&
                Number.isInteger(result.totalCount) &&
                result.totalCount >= result.items.length &&
                typeof result.hasMore === 'boolean' &&
                (result.hasMore
                    ? OPAQUE_ID_PATTERN.test(result.nextCursor)
                    : result.nextCursor == null) &&
                result.items.every(item =>
                    item &&
                    typeof item.targetId === 'string' &&
                    OPAQUE_ID_PATTERN.test(item.targetId) &&
                    (!this.supportsPagedQueryV2 ||
                        (Number.isInteger(item.matchRank) &&
                         item.matchRank >= 0 &&
                         item.matchRank <= 5 &&
                         typeof item.matchKind === 'string' &&
                         item.matchKind.length <= 32)))
            );
        }

        isValidContext(context) {
            if (!context ||
                !['1.0', '2.0'].includes(context.schemaVersion) ||
                typeof context.unitProjectKey !== 'string' ||
                !OPAQUE_ID_PATTERN.test(context.unitProjectKey) ||
                !Array.isArray(context.items) ||
                !Array.isArray(context.materials) ||
                !Array.isArray(context.fees)) {
                return false;
            }

            if (context.schemaVersion === '2.0' &&
                (typeof context.snapshotId !== 'string' ||
                 !OPAQUE_ID_PATTERN.test(context.snapshotId))) {
                return false;
            }

            const detailCount = context.items.length + context.materials.length + context.fees.length;
            if (detailCount > MAX_TARGETS ||
                !context.items.every(item =>
                    item && typeof item.targetId === 'string' && OPAQUE_ID_PATTERN.test(item.targetId))) {
                return false;
            }

            try {
                return new TextEncoder().encode(JSON.stringify(context)).length <= MAX_CONTEXT_BYTES;
            } catch {
                return false;
            }
        }

        rememberTargets(context) {
            this.validTargets.clear();
            this.currentUnitProjectKey = context.unitProjectKey;
            this.currentSnapshotId =
                context.schemaVersion === '2.0' &&
                OPAQUE_ID_PATTERN.test(context.snapshotId)
                    ? context.snapshotId
                    : '';
            if (context.schemaVersion !== '2.0') {
                this.currentSummary = null;
            }
            for (const item of context.items) {
                if (item && typeof item.targetId === 'string' && OPAQUE_ID_PATTERN.test(item.targetId)) {
                    this.validTargets.add(item.targetId);
                }
            }
        }

        clearTargets() {
            this.validTargets.clear();
            this.currentUnitProjectKey = '';
            this.currentSnapshotId = '';
            this.currentSummary = null;
        }

        createId() {
            if (window.crypto && typeof window.crypto.randomUUID === 'function') {
                return window.crypto.randomUUID();
            }
            return `hcsoft_${Date.now()}_${Math.random().toString(36).slice(2)}`;
        }

        setStatus(status) {
            this.status = status;
            this.ensureStatusButton();
            if (!this.statusButton) return;

            const labels = {
                connecting: '工程数据：连接中',
                detached: '工程数据：未附加',
                attaching: '工程数据：附加中',
                attached: '工程数据：已附加',
                denied: '工程数据：未授权（点击重试）',
                no_active_unit_project: '工程数据：无当前单位工程',
                timeout: '工程数据：连接超时',
                error: '工程数据：不可用',
                unavailable: '工程数据：未连接'
            };
            this.statusButton.textContent = labels[status] || labels.error;
            this.statusButton.dataset.status = status;
            this.statusButton.style.display = status === 'unavailable' && !this.webview ? 'none' : 'inline-flex';

            // “未附加”和“已附加”都是可点击状态：前者开启，后者关闭。
            // 上次附加失败时也允许用户点击重试；连接和附加过程中禁止重复请求。
            const clickableStatuses = new Set([
                'detached',
                'attached',
                'denied',
                'no_active_unit_project',
                'timeout',
                'error',
                'unavailable'
            ]);
            this.statusButton.disabled = !clickableStatuses.has(status);
            this.statusButton.style.cursor = this.statusButton.disabled ? 'default' : 'pointer';
            this.statusButton.title = status === 'attached'
                ? '点击停止附加工程数据'
                : (this.statusButton.disabled ? '' : '点击附加当前工程数据');
        }

        ensureStatusButton() {
            if (this.statusButton || !this.webview) return;
            const host = document.querySelector('.chat-input-area') || document.body;
            const button = document.createElement('button');
            button.type = 'button';
            button.id = 'hcsoft-bridge-status';
            button.style.cssText =
                'align-self:flex-start;margin:0 0 6px 4px;padding:3px 9px;border:1px solid #9996;' +
                'border-radius:999px;background:transparent;color:inherit;font-size:12px;line-height:18px;';
            button.addEventListener('click', () => {
                if (button.disabled) return;

                // 已附加时点击表示用户主动关闭，不再弹授权框或发送桥接请求。
                if (button.dataset.status === 'attached') {
                    this.detachContext();
                    return;
                }

                this.requestAuthorization().catch(error =>
                    console.warn('HCSoft context attachment failed:', error.message));
            });
            host.prepend(button);
            this.statusButton = button;
        }
    }

    window.hcsoftBridge = new HcsoftBridge();
})();
