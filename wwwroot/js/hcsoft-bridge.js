(function () {
    'use strict';

    const PROTOCOL_VERSION = '1.0';
    const REQUEST_TIMEOUT_MS = 5000;
    // context.get / context.summary.get 可能在桌面端显示人工授权窗口，
    // 不能沿用普通桥接消息的5秒超时。
    // 其他请求仍保持5秒，以便桥断开时快速退化为普通聊天。
    const CONTEXT_REQUEST_TIMEOUT_MS = 5 * 60 * 1000;
    const MAX_TARGETS = 600;
    const MAX_NAVIGATION_TARGETS = 3000;
    const MAX_CONTEXT_BYTES = 1024 * 1024;
    const MAX_SEARCH_PAGE_SIZE = 50;
    const MAX_DETAIL_TARGETS = 20;
    const MAX_STAGED_CONTEXT_ITEMS = 500;
    const MAX_SEARCH_PAGES = 12;
    const MAX_STRUCTURE_CONTEXT_ITEMS = 5;
    const MAX_MODEL_SEARCH_QUERIES = 10;
    const MAX_MODEL_SEARCH_ROUNDS = 10;
    const MAX_ANALYSIS_CHUNK_RECORDS = 50;
    const MAX_ANALYSIS_RESULT_BYTES = 512 * 1024;
    const MAX_STRUCTURED_QUERY_REQUEST_BYTES = 64 * 1024;
    const MAX_STRUCTURED_QUERY_RESULT_BYTES = 256 * 1024;
    const MAX_STRUCTURED_QUERY_RECORDS = 200;
    const MAX_STRUCTURED_QUERY_GROUPS = 200;
    const MAX_STRUCTURED_QUERY_RESULTS = 6;
    const MAX_MODEL_STRUCTURED_QUERIES = 3;
    const STRUCTURED_QUERY_DATASETS = new Set([
        'nodes',
        'materials',
        'fees',
        'laborMaterialMachinery',
        'laborMaterialMachinerySections',
        'config',
        'unitCostRates',
        'sections',
        'project'
    ]);
    const STRUCTURED_QUERY_FIELDS = new Set([
        'targetId', 'parentTargetId', 'sectionName', 'rowType', 'sequence',
        'code', 'name', 'parentName', 'path', 'specification', 'unit',
        'quantityText', 'submittedName', 'submittedUnit', 'treeOrdinal',
        'level', 'sectionIndex', 'childCount', 'isLeaf', 'filterOut',
        'lumpSumItems', 'unitPriceFromSubItemSum', 'actualQuantity',
        'submittedActualQuantity', 'ordinal', 'budgetPriceText',
        'marketPriceText', 'basePriceText', 'category', 'budgetPrice',
        'marketPrice', 'basePrice', 'rate', 'formula', 'value',
        'submittedValue', 'rateNumber', 'valueNumber',
        'submittedValueNumber', 'isTreeScoped', 'feeCategoryId',
        'treeSequence', 'treeCode', 'treeName', 'sectionCount', 'sectionNames',
        'originalCode', 'quantity', 'unitProjectQuantity', 'budgetAmount',
        'marketAmount', 'unitPriceDifference', 'differenceAmount', 'scope',
        'keyId', 'value1', 'value2', 'value3', 'note', 'rateName',
        'nodeCount', 'configCount', 'quotaSystem', 'templateName',
        'pricingMode', 'buildType', 'generalDescription', 'formInstructions',
        'constructionFacilities',
        ...['unitPrice', 'totalPrice', 'submittedUnitPrice', 'submittedTotalPrice']
            .flatMap(prefix => [
                'calculatedCost', 'civilOrInstallation', 'total', 'equipment',
                'labor', 'material', 'machinery', 'other', 'mainMaterial',
                'priceDifference', 'otherDirectFees', 'siteOverheads',
                'measuresFees', 'overheadCosts', 'overheads', 'profit',
                'taxes', 'priceEscalation'
            ].map(field => `${prefix}.${field}`))
    ]);
    // 只表达“整个工程节点集合”，避免桌面端把“材料”等字样解释成行类型过滤。
    const FULL_SCAN_QUERY = '全部工程节点';
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
            // 完整计价详情最多暂存500个工程节点；轻量名称目录独立分页读取，
            // 不再受该详情上限截断。握手后仍按桌面端实际上限向下兼容。
            this.maxTargets = MAX_TARGETS;
            this.maxNavigationTargets = MAX_NAVIGATION_TARGETS;
            this.maxContextBytes = MAX_CONTEXT_BYTES;
            this.maxStagedContextItems = MAX_STAGED_CONTEXT_ITEMS;
            this.maxSearchPages = MAX_SEARCH_PAGES;
            this.maxAnalysisChunkRecords = MAX_ANALYSIS_CHUNK_RECORDS;
            this.maxAnalysisResultBytes = MAX_ANALYSIS_RESULT_BYTES;
            this.maxStructuredQueryBytes = MAX_STRUCTURED_QUERY_REQUEST_BYTES;
            this.maxStructuredQueryRecords = MAX_STRUCTURED_QUERY_RECORDS;
            this.maxStructuredQueryResultBytes = MAX_STRUCTURED_QUERY_RESULT_BYTES;
            this.analysisPolicyVersion = '';
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
            this.applyAdvertisedLimits(payload);
            // “桥已连接”不等于“工程数据已附加”。默认仍以未附加状态等待用户主动点击。
            this.setStatus('detached');
            return payload;
        }

        applyAdvertisedLimits(payload) {
            const bounded = (value, fallback, maximum) =>
                Number.isInteger(value) && value > 0
                    ? Math.min(value, maximum)
                    : fallback;
            const limits = payload || {};
            this.maxTargets = bounded(
                limits.maxDetailRecords,
                this.maxTargets,
                MAX_TARGETS);
            this.maxNavigationTargets = bounded(
                limits.maxNavigationTargets,
                this.maxNavigationTargets,
                MAX_NAVIGATION_TARGETS);
            this.maxContextBytes = bounded(
                limits.maxContextBytes,
                this.maxContextBytes,
                MAX_CONTEXT_BYTES);
            this.maxStagedContextItems = bounded(
                limits.maxStagedContextItems,
                this.maxStagedContextItems,
                MAX_STAGED_CONTEXT_ITEMS);
            this.maxAnalysisChunkRecords = bounded(
                limits.maxAnalysisChunkRecords,
                this.maxAnalysisChunkRecords,
                MAX_ANALYSIS_CHUNK_RECORDS);
            this.maxAnalysisResultBytes = bounded(
                limits.maxAnalysisResultBytes,
                this.maxAnalysisResultBytes,
                MAX_ANALYSIS_RESULT_BYTES);
            this.maxStructuredQueryBytes = bounded(
                limits.maxStructuredQueryBytes,
                this.maxStructuredQueryBytes,
                MAX_STRUCTURED_QUERY_REQUEST_BYTES);
            this.maxStructuredQueryRecords = bounded(
                limits.maxStructuredQueryRecords,
                this.maxStructuredQueryRecords,
                MAX_STRUCTURED_QUERY_RECORDS);
            this.maxStructuredQueryResultBytes = bounded(
                limits.maxStructuredQueryResultBytes,
                this.maxStructuredQueryResultBytes,
                MAX_STRUCTURED_QUERY_RESULT_BYTES);
            this.analysisPolicyVersion =
                typeof limits.analysisPolicyVersion === 'string'
                    ? limits.analysisPolicyVersion.slice(0, 64)
                    : '';
            this.maxSearchPages = Math.min(
                MAX_SEARCH_PAGES,
                Math.max(
                    1,
                    Math.ceil(
                        this.maxStagedContextItems /
                        MAX_SEARCH_PAGE_SIZE) + 1));
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

        get supportsStructuredQuery() {
            return this.capabilities.includes('context.structured-query-v1');
        }

        /**
         * 新版桌面端能够在本地逐片处理任意总量工程记录，并只返回精确累计结果。
         */
        get supportsChunkedAnalysis() {
            return this.capabilities.includes('analysis.chunked-v1') &&
                this.analysisPolicyVersion === 'cost-policy-v1';
        }

        /**
         * 旧桌面端兼容路径：仍按桌面端返回的完整上下文及协商上限校验。
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

            const finalTargetIds = this.getContextTargetIds(
                detailState.context);
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
         * 最新检索结果优先进入上下文；达到协商后的条数或字节上限时，
         * 会淘汰较早的低优先级详情，而不会突破既有安全边界。
         */
        async extendContext(queries, currentContext, options) {
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
            const scanAll = Boolean(options && options.scanAll === true);
            if (normalizedQueries.length === 0 && !scanAll) {
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

            const searches = scanAll
                ? [{ query: FULL_SCAN_QUERY, scanAll: true }]
                : normalizedQueries.map(query => ({ query, scanAll: false }));
            for (const search of searches) {
                const searchState = await this.readRelevantSearchPages(
                    summaryResult,
                    search.query,
                    search.scanAll);
                if (searchState.status !== 'ok') {
                    return {
                        status: searchState.status,
                        context: mergedContext,
                        addedRecords
                    };
                }

                const detailState = await this.readDetailBatches(
                    summaryResult,
                    search.query,
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
            const addedCatalogRecords = this.countNewCatalogRecords(
                currentContext,
                mergedContext);
            const finalTargetIds = this.getContextTargetIds(mergedContext);
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
                addedRecords,
                addedCatalogRecords
            };
        }

        countNewContextRecords(originalContext, mergedContext) {
            const originalItems = new Set(
                originalContext.items.map(item => item.targetId));
            const originalMaterials = new Set(
                originalContext.materials.map(item => this.getMaterialKey(item)));
            const originalFees = new Set(
                originalContext.fees.map(item => this.getFeeKey(item)));
            const originalLaborMaterialMachinery =
                this.getLaborMaterialMachineryCoverageMap(originalContext);
            return mergedContext.items.filter(
                item => !originalItems.has(item.targetId)).length +
                mergedContext.materials.filter(
                    item => !originalMaterials.has(this.getMaterialKey(item))).length +
                mergedContext.fees.filter(
                    item => !originalFees.has(this.getFeeKey(item))).length +
                this.getLaborMaterialMachineryRecords(mergedContext).filter(item => {
                    const key = this.getLaborMaterialMachineryKey(item);
                    if (!key || !originalLaborMaterialMachinery.has(key)) {
                        return Boolean(key);
                    }
                    const originalSections = originalLaborMaterialMachinery.get(key);
                    return this.getLaborMaterialMachinerySectionKeys(item)
                        .some(sectionKey => !originalSections.has(sectionKey));
                }).length;
        }

        countNewCatalogRecords(originalContext, mergedContext) {
            const originalKeys = new Set(
                this.getCatalogRecords(originalContext)
                    .map(item => this.getCatalogKey(item))
                    .filter(Boolean));
            return this.getCatalogRecords(mergedContext).filter(item => {
                const key = this.getCatalogKey(item);
                return Boolean(key) && !originalKeys.has(key);
            }).length;
        }

        mergeContextsWithLatestPriority(existingContext, latestContext) {
            const oldItemIds = new Set(existingContext.items.map(item => item.targetId));
            const oldMaterialKeys = new Set(
                existingContext.materials.map(item => this.getMaterialKey(item)));
            const oldFeeKeys = new Set(
                existingContext.fees.map(item => this.getFeeKey(item)));
            const oldLaborMaterialMachineryKeys = new Set(
                this.getLaborMaterialMachineryRecords(existingContext)
                    .map(item => this.getLaborMaterialMachineryKey(item)));

            // 最新检索的摘要、轻量目录和检索元数据优先；完整详情仍按“最新在前”
            // 合并旧上下文，确保目录不会被上一轮有限取样覆盖。
            const merged = JSON.parse(JSON.stringify(latestContext));
            if (!merged.laborMaterialMachineryTotals &&
                existingContext.laborMaterialMachineryTotals) {
                merged.laborMaterialMachineryTotals =
                    JSON.parse(JSON.stringify(
                        existingContext.laborMaterialMachineryTotals));
            }
            if (!merged.laborMaterialMachineryRatios &&
                existingContext.laborMaterialMachineryRatios) {
                merged.laborMaterialMachineryRatios =
                    JSON.parse(JSON.stringify(
                        existingContext.laborMaterialMachineryRatios));
            }
            merged.items = [];
            merged.materials = [];
            merged.fees = [];
            merged.laborMaterialMachinery = [];
            delete merged.queryResults;
            this.mergeStructuredQueryResultsIntoContext(
                merged,
                latestContext,
                existingContext);

            const itemIds = new Set();
            const materialKeys = new Set();
            const feeKeys = new Set();
            const laborMaterialMachineryKeys = new Set();
            const laborMaterialMachineryIndexes = new Map();
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
            const appendLaborMaterialMachinery = item => {
                const key = this.getLaborMaterialMachineryKey(item);
                if (!key) return;
                if (laborMaterialMachineryKeys.has(key)) {
                    const index = laborMaterialMachineryIndexes.get(key);
                    const current = merged.laborMaterialMachinery[index];
                    const combined = this.mergeLaborMaterialMachineryRecords(
                        current,
                        item);
                    merged.laborMaterialMachinery[index] = combined;
                    if (this.getContextByteLength(merged) > this.maxContextBytes) {
                        merged.laborMaterialMachinery[index] = current;
                    }
                    return;
                }
                if (this.tryAppendContextRecord(
                        merged,
                        'laborMaterialMachinery',
                        item)) {
                    laborMaterialMachineryKeys.add(key);
                    laborMaterialMachineryIndexes.set(
                        key,
                        merged.laborMaterialMachinery.length - 1);
                    if (!oldLaborMaterialMachineryKeys.has(key)) addedRecords++;
                }
            };

            latestContext.items.forEach(appendItem);
            latestContext.materials.forEach(appendMaterial);
            latestContext.fees.forEach(appendFee);
            this.getLaborMaterialMachineryRecords(latestContext)
                .forEach(appendLaborMaterialMachinery);
            existingContext.items.forEach(appendItem);
            existingContext.materials.forEach(appendMaterial);
            existingContext.fees.forEach(appendFee);
            this.getLaborMaterialMachineryRecords(existingContext)
                .forEach(appendLaborMaterialMachinery);

            if (merged.selection &&
                merged.selection.targetId &&
                !itemIds.has(merged.selection.targetId)) {
                merged.selection.targetId = null;
            }
            const totalAvailableItems = Math.max(
                Number(latestContext.search &&
                    latestContext.search.totalAvailableItems) || 0,
                Number(existingContext.search &&
                    existingContext.search.totalAvailableItems) || 0,
                merged.items.length);
            merged.search = {
                totalAvailableItems,
                returnedItems: merged.items.length,
                hasMore:
                    merged.items.length < totalAvailableItems &&
                    Boolean(
                        (existingContext.search && existingContext.search.hasMore) ||
                        (latestContext.search && latestContext.search.hasMore))
            };
            if (latestContext.search &&
                typeof latestContext.search.catalogQuery === 'string' &&
                Number.isInteger(latestContext.search.catalogTotalItems) &&
                Array.isArray(merged.catalogItems)) {
                merged.search.catalogQuery = latestContext.search.catalogQuery;
                merged.search.catalogTotalItems =
                    latestContext.search.catalogTotalItems;
                merged.search.catalogReturnedItems = merged.catalogItems.length;
                merged.search.catalogComplete = Boolean(
                    latestContext.search.catalogComplete &&
                    merged.catalogItems.length ===
                        latestContext.search.catalogTotalItems);
            } else {
                delete merged.catalogItems;
            }
            return { context: merged, addedRecords };
        }

        mergeStructuredQueryResultsIntoContext(context, ...sources) {
            const results = [];
            const keys = new Set();
            for (const source of sources) {
                const values = source && Array.isArray(source.queryResults)
                    ? source.queryResults
                    : [];
                for (const result of values) {
                    if (!this.isValidStructuredQueryResult(result)) continue;
                    const key = this.getStructuredQueryResultKey(result);
                    if (keys.has(key) || results.length >= MAX_STRUCTURED_QUERY_RESULTS) {
                        continue;
                    }
                    context.queryResults = [
                        ...results,
                        JSON.parse(JSON.stringify(result))
                    ];
                    if (this.getContextByteLength(context) > this.maxContextBytes) {
                        delete context.queryResults;
                        if (results.length > 0) {
                            context.queryResults = results;
                        }
                        continue;
                    }
                    results.push(JSON.parse(JSON.stringify(result)));
                    keys.add(key);
                }
            }
            if (results.length > 0) {
                context.queryResults = results;
            } else {
                delete context.queryResults;
            }
        }

        tryAppendContextRecord(context, collectionName, record) {
            if (!record ||
                !['materials', 'fees', 'laborMaterialMachinery'].includes(
                    collectionName) ||
                this.getContextDetailCount(context) >= this.maxTargets) {
                return false;
            }

            context[collectionName].push(record);
            if (this.getContextByteLength(context) > this.maxContextBytes) {
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
                item.sectionIndex || 0,
                item.sectionName || '',
                item.isTreeScoped === true ? 'tree' : 'section',
                item.feeCategoryId || 0,
                item.treeSequence || '',
                item.treeCode || '',
                item.treeName || '',
                item.code || '',
                item.name || ''
            ].join('\u001f');
        }

        getCatalogRecords(context) {
            return context && Array.isArray(context.catalogItems)
                ? context.catalogItems
                : [];
        }

        getCatalogKey(item) {
            if (!item) return '';
            return [
                item.sectionName || '',
                item.rowType || '',
                item.sequence || '',
                item.code || '',
                item.name || '',
                item.specification || '',
                item.unit || ''
            ].join('\u001f');
        }

        /**
         * 旧桌面端没有 laborMaterialMachinery 字段；统一返回空数组可保持网页先行部署兼容。
         */
        getLaborMaterialMachineryRecords(context) {
            return context && Array.isArray(context.laborMaterialMachinery)
                ? context.laborMaterialMachinery
                : [];
        }

        /**
         * 《工料分析表》允许同编码、同名称但规格、类别或单价不同的记录分别存在，
         * 因此去重键必须覆盖报表合并键中的全部业务字段。
         */
        getLaborMaterialMachineryKey(item) {
            if (!item) return '';
            return [
                item.code || '',
                item.name || '',
                item.specification || '',
                item.unit || '',
                item.category || '',
                String(item.budgetPrice ?? ''),
                String(item.marketPrice ?? '')
            ].join('\u001f');
        }

        /**
         * 返回一条工料机已经覆盖的标段键。没有分标段数据的旧客户端记录使用
         * record 占位键，使同一业务记录仍能稳定参与新增事实判断。
         */
        getLaborMaterialMachinerySectionKeys(item) {
            const sections = item && Array.isArray(item.sectionQuantities)
                ? item.sectionQuantities
                : [];
            const keys = sections
                .map(section => {
                    if (!section) return '';
                    if (Number.isInteger(section.index) && section.index > 0) {
                        return `index:${section.index}`;
                    }
                    const name = typeof section.name === 'string'
                        ? section.name.trim()
                        : '';
                    return name ? `name:${name}` : '';
                })
                .filter(Boolean);
            return keys.length > 0 ? Array.from(new Set(keys)) : ['record'];
        }

        getLaborMaterialMachineryCoverageMap(context) {
            const result = new Map();
            for (const item of this.getLaborMaterialMachineryRecords(context)) {
                const key = this.getLaborMaterialMachineryKey(item);
                if (!key) continue;
                if (!result.has(key)) result.set(key, new Set());
                const sections = result.get(key);
                this.getLaborMaterialMachinerySectionKeys(item)
                    .forEach(sectionKey => sections.add(sectionKey));
            }
            return result;
        }

        /**
         * 合并同一工料机在多次“某个标段”检索中的分标段用量。
         * current 来自最新上下文，字段优先；supplemental 只补充尚未出现的标段。
         * 同一标段重复返回时覆盖而不相加，避免模型十轮检索造成用量翻倍。
         */
        mergeLaborMaterialMachineryRecords(current, supplemental) {
            const result = {
                ...(supplemental || {}),
                ...(current || {})
            };
            const sections = new Map();
            const appendSections = item => {
                const values = item && Array.isArray(item.sectionQuantities)
                    ? item.sectionQuantities
                    : [];
                for (const section of values) {
                    if (!section) continue;
                    const key = Number.isInteger(section.index) && section.index > 0
                        ? `index:${section.index}`
                        : `name:${String(section.name || '').trim()}`;
                    if (key === 'name:') continue;
                    sections.set(key, { ...section });
                }
            };
            appendSections(supplemental);
            appendSections(current);

            if (sections.size === 0) return result;
            result.sectionQuantities = Array.from(sections.values()).sort(
                (left, right) =>
                    (Number(left.index) || Number.MAX_SAFE_INTEGER) -
                    (Number(right.index) || Number.MAX_SAFE_INTEGER));
            const quantity = result.sectionQuantities.reduce(
                (sum, section) => sum + (Number(section.quantity) || 0),
                0);
            const budgetPrice = Number(result.budgetPrice) || 0;
            const marketPrice = Number(result.marketPrice) || 0;
            const roundMoney = value => Math.round(
                (value + Math.sign(value) * Number.EPSILON) * 100) / 100;
            result.quantity = quantity;
            result.budgetAmount = roundMoney(budgetPrice * quantity);
            result.marketAmount = roundMoney(marketPrice * quantity);
            result.unitPriceDifference = marketPrice - budgetPrice;
            result.differenceAmount = roundMoney(
                result.unitPriceDifference * quantity);
            return result;
        }

        getContextDetailCount(context) {
            if (!context) return Number.MAX_SAFE_INTEGER;
            return context.items.length +
                context.materials.length +
                context.fees.length +
                this.getLaborMaterialMachineryRecords(context).length;
        }

        async readRelevantSearchPages(summaryResult, query, scanAll) {
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
            const catalogItems = [];
            const seenCatalogTargets = new Set();
            let cursor = '';
            let threshold = null;
            let totalCount = 0;
            let pagesRead = 0;
            let hasMore = false;
            let exhaustive = Boolean(scanAll);
            let stoppedByDetailLimit = false;
            let stoppedByPageLimit = false;
            let catalogSelectionTruncated = false;
            let relevanceExhausted = false;
            let pageBudget = this.maxSearchPages;

            while (pagesRead < pageBudget) {
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
                exhaustive = exhaustive || result.exhaustive === true;
                // totalCount 由桌面端当前快照生成，可据此完整读取任意数量的轻量目录页；
                // 固定页数仅作为首包返回前的兼容下限，不再形成 500 条截断。
                pageBudget = Math.max(
                    pageBudget,
                    Math.ceil(totalCount / MAX_SEARCH_PAGE_SIZE) + 1);

                if (!exhaustive && threshold == null) {
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

                    if (exhaustive) {
                        pageHasRelevantItem = true;
                        catalogItems.push(item);
                        if (relevantItems.length < this.maxStagedContextItems) {
                            relevantItems.push(item);
                        } else {
                            stoppedByDetailLimit = true;
                        }
                        continue;
                    }

                    const rank = Number.isInteger(item.matchRank)
                        ? item.matchRank
                        : 0;
                    if (threshold === 0) {
                        if (relevantItems.length < MAX_DETAIL_TARGETS) {
                            relevantItems.push(item);
                            catalogItems.push(item);
                        } else {
                            catalogSelectionTruncated = true;
                        }
                        continue;
                    }

                    if (rank >= threshold) {
                        pageHasRelevantItem = true;
                        catalogItems.push(item);
                        if (relevantItems.length < this.maxStagedContextItems) {
                            relevantItems.push(item);
                        } else {
                            stoppedByDetailLimit = true;
                        }
                    } else if (
                        structureItems.length < MAX_STRUCTURE_CONTEXT_ITEMS &&
                        ['BD', 'TREE', 'GROUP'].includes(item.rowType)) {
                        structureItems.push(item);
                    }
                }

                if (!result.hasMore) {
                    hasMore = false;
                    break;
                }
                if (!exhaustive &&
                    (threshold === 0 || !pageHasRelevantItem)) {
                    relevanceExhausted = threshold > 0 && !pageHasRelevantItem;
                    break;
                }
                cursor = result.nextCursor;
            }

            if (pagesRead >= pageBudget && hasMore) {
                stoppedByPageLimit = true;
            }

            const targetIds = [];
            const appendTarget = targetId => {
                if (OPAQUE_ID_PATTERN.test(targetId) &&
                    !targetIds.includes(targetId) &&
                    targetIds.length < this.maxStagedContextItems) {
                    targetIds.push(targetId);
                }
            };
            selectedIds.forEach(appendTarget);
            relevantItems.forEach(item => appendTarget(item.targetId));
            structureItems.forEach(item => appendTarget(item.targetId));

            const candidateTargetCount = new Set([
                ...selectedIds,
                ...relevantItems.map(item => item.targetId),
                ...structureItems.map(item => item.targetId)
            ]).size;
            const catalogComplete =
                !stoppedByPageLimit &&
                !catalogSelectionTruncated &&
                (exhaustive
                    ? !hasMore
                    : (threshold > 0 && (relevanceExhausted || !hasMore)));
            const catalogTotalCount = catalogComplete
                ? catalogItems.length
                : totalCount;

            return {
                status: 'ok',
                targetIds,
                totalCount,
                catalogReturnedCount: seenCatalogTargets.size,
                catalogItems,
                catalogTotalCount,
                catalogComplete,
                catalogQuery: String(query || '').slice(0, 4000),
                exhaustive,
                hasMore:
                    stoppedByDetailLimit ||
                    stoppedByPageLimit ||
                    catalogSelectionTruncated ||
                    targetIds.length < candidateTargetCount ||
                    (!catalogComplete && hasMore)
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
                    this.attachCatalogToContext(mergedContext, searchState);
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
                ...(mergedContext.search || {}),
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

        /**
         * 把分页搜索目录压缩后附加到模型上下文。目录只保留名称、编号、层级等
         * 轻量字段，不含价格，也不授予定位权限；因此即使完整详情达到上限，模型
         * 仍能看到“全部清单/项目”的完整名称目录。
         */
        attachCatalogToContext(context, searchState) {
            if (!context || !searchState) return;

            const sourceItems = Array.isArray(searchState.catalogItems)
                ? searchState.catalogItems
                : [];
            const compactItems = sourceItems.map((item, index) =>
                this.toCompactCatalogItem(item, index + 1));
            context.catalogItems = compactItems;
            context.search = {
                ...(context.search || {}),
                catalogQuery: String(searchState.catalogQuery || '').slice(0, 4000),
                catalogTotalItems: Number.isInteger(searchState.catalogTotalCount)
                    ? searchState.catalogTotalCount
                    : compactItems.length,
                catalogReturnedItems: compactItems.length,
                catalogComplete: Boolean(searchState.catalogComplete)
            };

            // 对明确的穷举请求优先保留完整轻量目录。只回收首批中的低优先级
            // 详情/公共取样；后续详情会在目录占用空间之后继续按剩余字节追加。
            if (searchState.exhaustive &&
                this.getContextByteLength(context) > this.maxContextBytes) {
                while (context.items.length > 5 &&
                       this.getContextByteLength(context) > this.maxContextBytes) {
                    context.items.pop();
                }
                for (const collectionName of [
                    'materials',
                    'fees',
                    'laborMaterialMachinery'
                ]) {
                    while (Array.isArray(context[collectionName]) &&
                           context[collectionName].length > 0 &&
                           this.getContextByteLength(context) > this.maxContextBytes) {
                        context[collectionName].pop();
                    }
                }
            }

            if (this.getContextByteLength(context) > this.maxContextBytes) {
                // 即使只保留轻量字段，极端大工程仍可能超过协商字节上限。
                // 用二分法保留能完整序列化的最大前缀，并明确 catalogComplete=false。
                let low = 0;
                let high = compactItems.length;
                while (low < high) {
                    const middle = Math.ceil((low + high) / 2);
                    context.catalogItems = compactItems.slice(0, middle);
                    context.search.catalogReturnedItems = middle;
                    context.search.catalogComplete = false;
                    if (this.getContextByteLength(context) <= this.maxContextBytes) {
                        low = middle;
                    } else {
                        high = middle - 1;
                    }
                }
                context.catalogItems = compactItems.slice(0, low);
                context.search.catalogReturnedItems = low;
                context.search.catalogComplete = false;
            }

            if (this.getContextByteLength(context) > this.maxContextBytes) {
                // 原详情包可能恰好贴近上限，连目录元数据都无法容纳；此时恢复为
                // 合法的纯详情上下文，并保留 hasMore 让模型继续精确检索。
                delete context.catalogItems;
                delete context.search.catalogQuery;
                delete context.search.catalogTotalItems;
                delete context.search.catalogReturnedItems;
                delete context.search.catalogComplete;
            }
        }

        toCompactCatalogItem(item, ordinal) {
            const result = { ordinal };
            const copyString = (sourceName, targetName = sourceName) => {
                if (item && typeof item[sourceName] === 'string' && item[sourceName]) {
                    result[targetName] = item[sourceName];
                }
            };
            if (item && Number.isInteger(item.level) && item.level >= 0) {
                result.level = item.level;
            }
            copyString('sectionName');
            copyString('rowType');
            copyString('sequence');
            copyString('code');
            copyString('name');
            copyString('specification');
            copyString('unit');
            return result;
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
                context.items.length >= this.maxStagedContextItems ||
                this.getContextDetailCount(context) >= this.maxTargets) {
                return false;
            }

            context.items.push(item);
            if (this.getContextByteLength(context) > this.maxContextBytes) {
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
            if (window.hcsoftAnalysisController &&
                typeof window.hcsoftAnalysisController.cancel === 'function') {
                window.hcsoftAnalysisController.cancel();
            }
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
            let scanAll = false;
            let foundTag = false;
            let cleanContent = String(content || '').replace(
                /<hcsoft_search>([\s\S]*?)<\/hcsoft_search>/g,
                (tag, json) => {
                    foundTag = true;
                    try {
                        const request = JSON.parse(json);
                        const allowedKeys = new Set([
                            'queries',
                            'reason',
                            'scanAll'
                        ]);
                        if (!request ||
                            !Object.keys(request).every(key => allowedKeys.has(key)) ||
                            !Array.isArray(request.queries) ||
                            (request.scanAll != null &&
                             typeof request.scanAll !== 'boolean') ||
                            (request.reason != null &&
                             (typeof request.reason !== 'string' ||
                              request.reason.length > 200))) {
                            return '';
                        }
                        scanAll = scanAll || request.scanAll === true;

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
                scanAll,
                queries,
                content: cleanContent.replace(/\n{3,}/g, '\n\n').trimEnd()
            };
        }

        /**
         * 解析模型生成的结构化只读查询 AST。未知字段、超限值和任何非白名单
         * 结构都会在进入桌面桥之前被拒绝；桌面端仍会独立执行完整验证。
         */
        extractStructuredQueryRequests(content) {
            const queries = [];
            const keys = [];
            const errors = [];
            let foundTag = false;
            let cleanContent = String(content || '').replace(
                /<hcsoft_query>([\s\S]*?)<\/hcsoft_query>/g,
                (tag, json) => {
                    foundTag = true;
                    if (this.getUtf8ByteLength(json) >
                        this.maxStructuredQueryBytes) {
                        errors.push('结构化查询超过允许的字节上限。');
                        return '';
                    }
                    try {
                        const query = JSON.parse(json);
                        const validation = this.validateStructuredQueryAst(query);
                        if (!validation.valid) {
                            errors.push(...validation.errors);
                            return '';
                        }
                        const key = this.stableStringify(query);
                        if (!keys.includes(key) &&
                            queries.length < MAX_MODEL_STRUCTURED_QUERIES) {
                            queries.push(query);
                            keys.push(key);
                        }
                    } catch (error) {
                        errors.push(`结构化查询 JSON 无效：${error.message}`);
                    }
                    return '';
                });

            const danglingIndex = cleanContent.indexOf('<hcsoft_query>');
            if (danglingIndex >= 0) {
                foundTag = true;
                cleanContent = cleanContent.slice(0, danglingIndex);
                errors.push('结构化查询标签未闭合。');
            }

            return {
                foundTag,
                queries,
                keys,
                errors: errors.slice(0, 12),
                content: cleanContent.replace(/\n{3,}/g, '\n\n').trimEnd()
            };
        }

        validateStructuredQueryAst(query) {
            const errors = [];
            const addError = message => {
                if (errors.length < 12) errors.push(message);
            };
            if (!this.isPlainObject(query) ||
                !this.hasOnlyKeys(query, [
                    'version', 'queryId', 'from', 'scope', 'where', 'select',
                    'groupBy', 'aggregate', 'orderBy', 'page'
                ])) {
                return {
                    valid: false,
                    errors: ['query 必须是只包含协议白名单字段的对象。']
                };
            }
            if (query.version !== PROTOCOL_VERSION) {
                addError('query.version 必须为 1.0。');
            }
            if (typeof query.queryId !== 'string' ||
                query.queryId.length > 64 ||
                (query.queryId && !/^[\p{L}\p{N}_-]+$/u.test(query.queryId))) {
                addError('queryId 只能包含字母、数字、下划线和连字符，且最长64字符。');
            }
            if (!STRUCTURED_QUERY_DATASETS.has(query.from)) {
                addError('from 不是受支持的数据集。');
            }

            this.validateStructuredQueryScope(query.scope, query.from, addError);
            const filterState = { count: 0 };
            this.validateStructuredQueryFilter(
                query.where,
                query.from,
                0,
                filterState,
                addError);
            this.validateStructuredFieldArray(
                query.select,
                query.from,
                40,
                'select',
                addError);
            this.validateStructuredFieldArray(
                query.groupBy,
                query.from,
                3,
                'groupBy',
                addError);

            const aliases = new Set();
            let returnRecordCount = 0;
            if (query.aggregate !== undefined) {
                if (!Array.isArray(query.aggregate) ||
                    query.aggregate.length > 10) {
                    addError('aggregate 必须是最多10项的数组。');
                } else {
                    for (const aggregate of query.aggregate) {
                        if (!this.isPlainObject(aggregate) ||
                            !this.hasOnlyKeys(aggregate, [
                                'operation', 'field', 'as', 'returnRecord'
                            ])) {
                            addError('aggregate 包含未支持的结构。');
                            continue;
                        }
                        if (![
                            'count', 'distinctCount', 'sum', 'avg', 'min', 'max'
                        ].includes(aggregate.operation)) {
                            addError('aggregate.operation 不受支持。');
                        }
                        if (aggregate.operation !== 'count' ||
                            aggregate.field !== undefined) {
                            if (typeof aggregate.field !== 'string' ||
                                !this.isStructuredQueryField(
                                    query.from,
                                    aggregate.field)) {
                                addError('aggregate.field 不属于当前数据集。');
                            }
                        }
                        const generatedAlias = aggregate.field
                            ? `${aggregate.operation}_${aggregate.field.replace(/\./g, '_')}`
                            : String(aggregate.operation || '');
                        const alias = aggregate.as === undefined
                            ? generatedAlias.slice(0, 48)
                            : aggregate.as;
                        if (typeof alias !== 'string' ||
                            !/^[\p{L}_][\p{L}\p{N}_]{0,47}$/u.test(alias) ||
                            aliases.has(alias.toLocaleLowerCase())) {
                            addError('aggregate.as 无效或重复。');
                        } else {
                            aliases.add(alias.toLocaleLowerCase());
                        }
                        if (aggregate.returnRecord !== undefined &&
                            typeof aggregate.returnRecord !== 'boolean') {
                            addError('aggregate.returnRecord 必须是布尔值。');
                        }
                        if (aggregate.returnRecord === true &&
                            !['min', 'max'].includes(aggregate.operation)) {
                            addError('returnRecord 只允许用于 min 或 max。');
                        }
                        if (aggregate.returnRecord === true &&
                            ++returnRecordCount > 1) {
                            addError('每个查询最多一个聚合项可使用 returnRecord。');
                        }
                    }
                }
            }

            if (query.orderBy !== undefined) {
                if (!Array.isArray(query.orderBy) || query.orderBy.length > 5) {
                    addError('orderBy 必须是最多5项的数组。');
                } else {
                    for (const order of query.orderBy) {
                        if (!this.isPlainObject(order) ||
                            !this.hasOnlyKeys(order, ['field', 'direction']) ||
                            typeof order.field !== 'string' ||
                            (!this.isStructuredQueryField(query.from, order.field) &&
                             !aliases.has(order.field.toLocaleLowerCase())) ||
                            (order.direction !== undefined &&
                             !['asc', 'desc'].includes(order.direction))) {
                            addError('orderBy 包含无效字段或排序方向。');
                        }
                    }
                }
            }

            if (query.page !== undefined &&
                (!this.isPlainObject(query.page) ||
                 !this.hasOnlyKeys(query.page, ['offset', 'limit']) ||
                 (query.page.offset !== undefined &&
                  (!Number.isInteger(query.page.offset) ||
                   query.page.offset < 0 || query.page.offset > 1000000)) ||
                 (query.page.limit !== undefined &&
                  (!Number.isInteger(query.page.limit) ||
                   query.page.limit < 0 ||
                   query.page.limit > this.maxStructuredQueryRecords)))) {
                addError('page.offset 或 page.limit 超出允许范围。');
            }

            if (this.getUtf8ByteLength(JSON.stringify(query)) >
                this.maxStructuredQueryBytes) {
                addError('结构化查询超过允许的字节上限。');
            }
            return { valid: errors.length === 0, errors };
        }

        validateStructuredQueryScope(scope, dataset, addError) {
            if (scope === undefined) return;
            if (!this.isPlainObject(scope) ||
                !this.hasOnlyKeys(scope, ['path', 'targetId', 'relation'])) {
                addError('scope 包含未支持的结构。');
                return;
            }
            if (scope.path !== undefined &&
                (!Array.isArray(scope.path) || scope.path.length > 12 ||
                 !scope.path.every(segment =>
                    typeof segment === 'string' &&
                    segment.trim().length > 0 && segment.length <= 256))) {
                addError('scope.path 必须是最多12段的非空文本数组。');
            }
            if (scope.targetId !== undefined &&
                (typeof scope.targetId !== 'string' ||
                 (scope.targetId && !OPAQUE_ID_PATTERN.test(scope.targetId)))) {
                addError('scope.targetId 不是有效的 opaque ID。');
            }
            if (scope.targetId && !['nodes', 'fees'].includes(dataset)) {
                addError('只有 nodes 和 fees 数据集允许使用 scope.targetId。');
            }
            if (scope.relation !== undefined &&
                !['all', 'self', 'children', 'descendants',
                  'selfAndDescendants', 'ancestors'].includes(scope.relation)) {
                addError('scope.relation 不受支持。');
            }
            if (dataset !== 'nodes' &&
                scope.relation !== undefined &&
                !['all', 'self'].includes(scope.relation)) {
                addError('非 nodes 数据集的 relation 只能是 all 或 self。');
            }
        }

        validateStructuredQueryFilter(
            filter,
            dataset,
            depth,
            state,
            addError) {
            if (filter === undefined) return;
            if (!this.isPlainObject(filter) || depth > 6 || ++state.count > 64) {
                addError('where 不是有效对象，或超过嵌套/条件上限。');
                return;
            }
            if (!this.hasOnlyKeys(filter, [
                'and', 'or', 'not', 'field', 'op', 'value'
            ])) {
                addError('where 条件包含未支持字段。');
                return;
            }
            const shapes = [
                Object.prototype.hasOwnProperty.call(filter, 'and'),
                Object.prototype.hasOwnProperty.call(filter, 'or'),
                Object.prototype.hasOwnProperty.call(filter, 'not'),
                Object.prototype.hasOwnProperty.call(filter, 'field') ||
                    Object.prototype.hasOwnProperty.call(filter, 'op')
            ].filter(Boolean).length;
            if (shapes !== 1) {
                addError('每个 where 节点只能是 and、or、not 或叶子条件之一。');
                return;
            }
            if (Array.isArray(filter.and) || Array.isArray(filter.or)) {
                const children = filter.and || filter.or;
                if (children.length === 0) {
                    addError('and/or 至少需要一个子条件。');
                    return;
                }
                children.forEach(child => this.validateStructuredQueryFilter(
                    child,
                    dataset,
                    depth + 1,
                    state,
                    addError));
                return;
            }
            if (Object.prototype.hasOwnProperty.call(filter, 'and') ||
                Object.prototype.hasOwnProperty.call(filter, 'or')) {
                addError('and/or 必须是条件数组。');
                return;
            }
            if (Object.prototype.hasOwnProperty.call(filter, 'not')) {
                this.validateStructuredQueryFilter(
                    filter.not,
                    dataset,
                    depth + 1,
                    state,
                    addError);
                return;
            }

            const operators = [
                'eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'contains',
                'notContains', 'startsWith', 'endsWith', 'in', 'notIn',
                'between', 'isNull', 'isNotNull'
            ];
            if (typeof filter.field !== 'string' ||
                !this.isStructuredQueryField(dataset, filter.field)) {
                addError('where.field 不属于当前数据集。');
            }
            if (!operators.includes(filter.op)) {
                addError('where.op 不受支持。');
            }
            const needsValue = !['isNull', 'isNotNull'].includes(filter.op);
            if (needsValue &&
                !Object.prototype.hasOwnProperty.call(filter, 'value')) {
                addError('该 where 运算符必须提供 value。');
            }
            if (['in', 'notIn', 'between'].includes(filter.op) &&
                !Array.isArray(filter.value)) {
                addError('in/notIn/between 的 value 必须是数组。');
            }
            if (filter.op === 'between' &&
                Array.isArray(filter.value) && filter.value.length !== 2) {
                addError('between 必须恰好包含两个边界值。');
            }
            if (Object.prototype.hasOwnProperty.call(filter, 'value') &&
                !this.isValidStructuredPrimitiveValue(filter.value, true)) {
                addError('where.value 的类型、长度或数组大小无效。');
            }
        }

        validateStructuredFieldArray(value, dataset, maximum, label, addError) {
            if (value === undefined) return;
            if (!Array.isArray(value) || value.length > maximum ||
                !value.every(field =>
                    typeof field === 'string' &&
                    this.isStructuredQueryField(dataset, field))) {
                addError(`${label} 包含无效字段或超过数量上限。`);
            }
        }

        isStructuredQueryField(dataset, field) {
            if (!STRUCTURED_QUERY_DATASETS.has(dataset) ||
                !STRUCTURED_QUERY_FIELDS.has(field)) {
                return false;
            }
            const breakdown = (prefixes) => prefixes.some(prefix =>
                field.startsWith(`${prefix}.`));
            switch (dataset) {
                case 'nodes':
                    return new Set([
                        'targetId', 'parentTargetId', 'sectionName', 'rowType',
                        'sequence', 'code', 'name', 'parentName', 'path',
                        'specification', 'unit', 'quantityText', 'submittedName',
                        'submittedUnit', 'treeOrdinal', 'level', 'sectionIndex',
                        'childCount', 'isLeaf', 'filterOut', 'lumpSumItems',
                        'unitPriceFromSubItemSum', 'actualQuantity',
                        'submittedActualQuantity'
                    ]).has(field) || breakdown([
                        'unitPrice', 'totalPrice', 'submittedUnitPrice',
                        'submittedTotalPrice'
                    ]);
                case 'materials':
                    return new Set([
                        'ordinal', 'code', 'name', 'specification', 'unit',
                        'budgetPriceText', 'marketPriceText', 'basePriceText',
                        'category', 'budgetPrice', 'marketPrice', 'basePrice'
                    ]).has(field);
                case 'fees':
                    return new Set([
                        'ordinal', 'targetId', 'sectionIndex', 'sectionName',
                        'isTreeScoped', 'feeCategoryId', 'treeSequence',
                        'treeCode', 'treeName', 'path', 'code', 'name',
                        'rate', 'formula', 'value', 'submittedValue',
                        'rateNumber', 'valueNumber', 'submittedValueNumber'
                    ]).has(field);
                case 'laborMaterialMachinery':
                case 'laborMaterialMachinerySections':
                    return new Set([
                        'ordinal', 'sectionIndex', 'sectionCount', 'sectionName',
                        'sectionNames', 'code', 'originalCode', 'name',
                        'specification', 'unit', 'category', 'quantity',
                        'unitProjectQuantity', 'budgetPrice', 'budgetAmount',
                        'marketPrice', 'marketAmount', 'unitPriceDifference',
                        'differenceAmount'
                    ]).has(field);
                case 'config':
                    return new Set([
                        'ordinal', 'sectionIndex', 'keyId', 'scope',
                        'sectionName', 'name', 'value', 'value1', 'value2',
                        'value3', 'note'
                    ]).has(field);
                case 'unitCostRates':
                    return new Set([
                        'ordinal', 'category', 'rateName', 'rate', 'rateNumber'
                    ]).has(field);
                case 'sections':
                    return new Set([
                        'targetId', 'sectionName', 'sectionIndex', 'nodeCount',
                        'configCount'
                    ]).has(field) || breakdown(['totalPrice']);
                case 'project':
                    return new Set([
                        'name', 'quotaSystem', 'templateName', 'pricingMode',
                        'buildType', 'generalDescription', 'formInstructions',
                        'constructionFacilities'
                    ]).has(field) || breakdown(['totalPrice']);
                default:
                    return false;
            }
        }

        async executeStructuredQueries(queries, currentContext) {
            if (!this.supportsStructuredQuery ||
                !this.currentSummary ||
                !this.isValidContext(currentContext) ||
                currentContext.schemaVersion !== '2.0' ||
                currentContext.snapshotId !== this.currentSnapshotId ||
                currentContext.unitProjectKey !== this.currentUnitProjectKey) {
                return {
                    status: 'unsupported',
                    context: currentContext,
                    addedQueryResults: 0,
                    feedback: ['当前客户端不支持结构化工程查询。'],
                    outcomes: []
                };
            }

            const working = JSON.parse(JSON.stringify(currentContext));
            const outcomes = [];
            let addedQueryResults = 0;
            for (const query of Array.isArray(queries)
                ? queries.slice(0, MAX_MODEL_STRUCTURED_QUERIES)
                : []) {
                const validation = this.validateStructuredQueryAst(query);
                if (!validation.valid) {
                    outcomes.push({
                        status: 'invalid_query',
                        queryId: query && query.queryId || '',
                        errors: validation.errors
                    });
                    continue;
                }

                let response;
                try {
                    const payload = {
                        snapshotId: this.currentSnapshotId,
                        query
                    };
                    if (this.getUtf8ByteLength(JSON.stringify(payload)) >
                        this.maxStructuredQueryBytes) {
                        outcomes.push({
                            status: 'invalid_query',
                            queryId: query.queryId,
                            errors: ['结构化查询连同快照信封超过允许大小。']
                        });
                        continue;
                    }
                    response = await this.request(
                        'context.query',
                        payload,
                        CONTEXT_REQUEST_TIMEOUT_MS);
                } catch (error) {
                    outcomes.push({
                        status: 'error',
                        queryId: query.queryId,
                        errors: [error.message]
                    });
                    continue;
                }

                if (response && response.status === 'invalid_query') {
                    const responseErrors = Array.isArray(response.errors)
                        ? response.errors.filter(item =>
                            typeof item === 'string' && item.length <= 5120)
                            .slice(0, 12)
                        : [];
                    outcomes.push({
                        status: 'invalid_query',
                        queryId: query.queryId,
                        errors: responseErrors.length > 0
                            ? responseErrors
                            : [String(response.message || '查询未通过桌面端验证。')]
                    });
                    continue;
                }
                if (!this.isValidStructuredQueryResponse(response)) {
                    outcomes.push({
                        status: response && typeof response.status === 'string'
                            ? response.status
                            : 'error',
                        queryId: query.queryId,
                        errors: ['桌面端返回了无效或不匹配的结构化查询结果。']
                    });
                    continue;
                }

                const before = Array.isArray(working.queryResults)
                    ? working.queryResults.length
                    : 0;
                this.appendStructuredQueryResult(working, response.result);
                const after = Array.isArray(working.queryResults)
                    ? working.queryResults.length
                    : 0;
                if (after > before ||
                    working.queryResults.some(result =>
                        result.queryId === response.result.queryId &&
                        result.dataset === response.result.dataset)) {
                    addedQueryResults++;
                }
                outcomes.push({
                    status: response.status,
                    queryId: response.result.queryId,
                    dataset: response.result.dataset,
                    scannedCount: response.result.scannedCount,
                    matchedCount: response.result.matchedCount,
                    returnedCount: response.result.returnedCount,
                    ambiguityCount: response.result.ambiguities.length,
                    message: String(response.message || '').slice(0, 5120)
                });
            }

            if (!this.fitContextAfterStructuredQuery(working) ||
                !this.isValidContext(working)) {
                return {
                    status: 'error',
                    context: currentContext,
                    addedQueryResults: 0,
                    feedback: ['结构化查询结果无法安全合并到工程上下文。'],
                    outcomes
                };
            }
            const finalTargetIds = this.getContextTargetIds(working);
            let commitResult;
            try {
                commitResult = await this.request(
                    'context.targets.commit',
                    {
                        snapshotId: this.currentSnapshotId,
                        targetIds: finalTargetIds
                    },
                    CONTEXT_REQUEST_TIMEOUT_MS);
            } catch (error) {
                return {
                    status: error.code || 'error',
                    context: currentContext,
                    addedQueryResults: 0,
                    feedback: [`结构化查询定位白名单提交失败：${error.message}`],
                    outcomes
                };
            }
            if (!commitResult ||
                commitResult.status !== 'ok' ||
                commitResult.unitProjectKey !== this.currentUnitProjectKey ||
                commitResult.acceptedCount !== finalTargetIds.length) {
                return {
                    status: commitResult && commitResult.status || 'error',
                    context: currentContext,
                    addedQueryResults: 0,
                    feedback: ['结构化查询定位白名单未被桌面端接受。'],
                    outcomes
                };
            }
            this.rememberTargets(working);
            return {
                status: outcomes.some(item =>
                    item.status === 'ok' || item.status === 'ambiguous')
                    ? 'ok'
                    : 'no_result',
                context: working,
                addedQueryResults,
                feedback: outcomes.map(item =>
                    this.formatStructuredQueryOutcome(item)),
                outcomes
            };
        }

        isValidStructuredQueryResponse(response) {
            if (!this.isPlainObject(response) ||
                !this.hasOnlyKeys(response, [
                    'status', 'message', 'snapshotId', 'unitProjectKey',
                    'result', 'errors'
                ]) ||
                !['ok', 'ambiguous'].includes(response.status) ||
                response.snapshotId !== this.currentSnapshotId ||
                response.unitProjectKey !== this.currentUnitProjectKey ||
                typeof response.message !== 'string' ||
                response.message.length > 5120 ||
                !Array.isArray(response.errors) ||
                response.errors.length > 12 ||
                !response.errors.every(item =>
                    typeof item === 'string' && item.length <= 5120) ||
                !this.isValidStructuredQueryResult(response.result)) {
                return false;
            }
            const ambiguous = response.result.ambiguities.length > 0;
            return response.status === (ambiguous ? 'ambiguous' : 'ok');
        }

        isValidStructuredQueryResult(result) {
            if (!this.isPlainObject(result) ||
                !this.hasOnlyKeys(result, [
                    'schemaVersion', 'queryId', 'dataset', 'executionComplete',
                    'scannedCount', 'matchedCount', 'returnedCount',
                    'recordsComplete', 'hasMore', 'nextOffset',
                    'selectedFields', 'records', 'aggregates', 'groupCount',
                    'groupsComplete', 'groups', 'ambiguities'
                ]) ||
                result.schemaVersion !== PROTOCOL_VERSION ||
                typeof result.queryId !== 'string' ||
                result.queryId.length > 64 ||
                (result.queryId &&
                 !/^[\p{L}\p{N}_-]+$/u.test(result.queryId)) ||
                !STRUCTURED_QUERY_DATASETS.has(result.dataset) ||
                typeof result.executionComplete !== 'boolean' ||
                !Number.isInteger(result.scannedCount) ||
                result.scannedCount < 0 ||
                !Number.isInteger(result.matchedCount) ||
                result.matchedCount < 0 ||
                result.matchedCount > result.scannedCount ||
                !Number.isInteger(result.returnedCount) ||
                result.returnedCount < 0 ||
                typeof result.recordsComplete !== 'boolean' ||
                typeof result.hasMore !== 'boolean' ||
                !Array.isArray(result.selectedFields) ||
                result.selectedFields.length > 40 ||
                !result.selectedFields.every(field =>
                    typeof field === 'string' &&
                    this.isStructuredQueryField(result.dataset, field)) ||
                new Set(result.selectedFields).size !==
                    result.selectedFields.length ||
                !Array.isArray(result.records) ||
                result.records.length !== result.returnedCount ||
                result.records.length > this.maxStructuredQueryRecords ||
                result.returnedCount > result.matchedCount ||
                !result.records.every(record =>
                    this.isValidStructuredRecord(
                        record,
                        result.dataset,
                        result.selectedFields)) ||
                !Array.isArray(result.aggregates) ||
                result.aggregates.length > 10 ||
                !result.aggregates.every(aggregate =>
                    this.isValidStructuredAggregate(
                        aggregate,
                        result.dataset,
                        result.selectedFields)) ||
                !Number.isInteger(result.groupCount) ||
                result.groupCount < 0 ||
                typeof result.groupsComplete !== 'boolean' ||
                !Array.isArray(result.groups) ||
                result.groups.length > MAX_STRUCTURED_QUERY_GROUPS ||
                result.groups.length > result.groupCount ||
                !result.groups.every(group =>
                    this.isValidStructuredGroup(
                        group,
                        result.dataset,
                        result.selectedFields)) ||
                !Array.isArray(result.ambiguities) ||
                result.ambiguities.length > 20 ||
                !result.ambiguities.every(item =>
                    this.isValidStructuredAmbiguity(item))) {
                return false;
            }
            if (result.hasMore) {
                if (!Number.isInteger(result.nextOffset) ||
                    result.nextOffset < result.returnedCount) {
                    return false;
                }
            } else if (result.nextOffset !== undefined &&
                       result.nextOffset !== null) {
                return false;
            }
            if (result.recordsComplete &&
                (result.hasMore || result.returnedCount !== result.matchedCount)) {
                return false;
            }
            if (result.groupsComplete &&
                result.groups.length !== result.groupCount) {
                return false;
            }
            if (result.ambiguities.length > 0 &&
                (result.executionComplete || result.matchedCount !== 0 ||
                 result.returnedCount !== 0 || result.records.length !== 0)) {
                return false;
            }
            return this.getUtf8ByteLength(JSON.stringify(result)) <=
                this.maxStructuredQueryResultBytes;
        }

        isValidStructuredRecord(record, dataset, selectedFields) {
            return this.isPlainObject(record) &&
                // 桌面端会在 select 之外补充最多6个身份字段；极值记录还会
                // 确保包含聚合字段，因此保留2个协议余量。
                Object.keys(record).length <= selectedFields.length + 8 &&
                Object.keys(record).every(field =>
                    this.isStructuredQueryField(dataset, field) &&
                    this.isValidStructuredPrimitiveValue(record[field], false));
        }

        isValidStructuredAggregate(aggregate, dataset, selectedFields) {
            if (!this.isPlainObject(aggregate) ||
                !this.hasOnlyKeys(aggregate, [
                    'alias', 'operation', 'field', 'value', 'record'
                ]) ||
                typeof aggregate.alias !== 'string' ||
                !/^[\p{L}_][\p{L}\p{N}_]{0,47}$/u.test(aggregate.alias) ||
                !['count', 'distinctCount', 'sum', 'avg', 'min', 'max']
                    .includes(aggregate.operation) ||
                typeof aggregate.field !== 'string' ||
                (aggregate.field &&
                 !this.isStructuredQueryField(dataset, aggregate.field)) ||
                (Object.prototype.hasOwnProperty.call(aggregate, 'value') &&
                 !this.isValidStructuredPrimitiveValue(
                    aggregate.value,
                    false))) {
                return false;
            }
            return aggregate.record === undefined ||
                aggregate.record === null ||
                this.isValidStructuredRecord(
                    aggregate.record,
                    dataset,
                    selectedFields);
        }

        isValidStructuredGroup(group, dataset, selectedFields) {
            return this.isPlainObject(group) &&
                this.hasOnlyKeys(group, ['keys', 'count', 'aggregates']) &&
                Array.isArray(group.keys) && group.keys.length <= 3 &&
                group.keys.every(key =>
                    this.isPlainObject(key) &&
                    this.hasOnlyKeys(key, ['field', 'value']) &&
                    typeof key.field === 'string' &&
                    this.isStructuredQueryField(dataset, key.field) &&
                    (!Object.prototype.hasOwnProperty.call(key, 'value') ||
                     this.isValidStructuredPrimitiveValue(key.value, false))) &&
                Number.isInteger(group.count) && group.count >= 0 &&
                Array.isArray(group.aggregates) &&
                group.aggregates.length <= 10 &&
                group.aggregates.every(aggregate =>
                    this.isValidStructuredAggregate(
                        aggregate,
                        dataset,
                        selectedFields));
        }

        isValidStructuredAmbiguity(item) {
            return this.isPlainObject(item) &&
                this.hasOnlyKeys(item, [
                    'targetId', 'path', 'sectionName', 'rowType', 'code', 'name'
                ]) &&
                OPAQUE_ID_PATTERN.test(item.targetId) &&
                ['path', 'sectionName', 'rowType', 'code', 'name'].every(key =>
                    typeof item[key] === 'string' && item[key].length <= 5120);
        }

        appendStructuredQueryResult(context, result) {
            const results = Array.isArray(context.queryResults)
                ? context.queryResults.filter(item =>
                    this.isValidStructuredQueryResult(item))
                : [];
            const key = this.getStructuredQueryResultKey(result);
            const deduplicated = results.filter(item =>
                this.getStructuredQueryResultKey(item) !== key);
            deduplicated.push(JSON.parse(JSON.stringify(result)));
            context.queryResults = deduplicated.slice(-MAX_STRUCTURED_QUERY_RESULTS);
        }

        fitContextAfterStructuredQuery(context) {
            if (!context) return false;
            while (this.getContextTargetIds(context).length >
                       this.maxNavigationTargets &&
                   Array.isArray(context.queryResults) &&
                   context.queryResults.length > 1) {
                context.queryResults.shift();
            }
            while (this.getContextByteLength(context) > this.maxContextBytes &&
                   Array.isArray(context.queryResults) &&
                   context.queryResults.length > 1) {
                context.queryResults.shift();
            }
            if (this.getContextByteLength(context) > this.maxContextBytes &&
                Array.isArray(context.catalogItems)) {
                delete context.catalogItems;
                if (context.search) {
                    delete context.search.catalogQuery;
                    delete context.search.catalogTotalItems;
                    delete context.search.catalogReturnedItems;
                    delete context.search.catalogComplete;
                }
            }
            for (const collectionName of [
                'laborMaterialMachinery', 'fees', 'materials', 'items'
            ]) {
                while (Array.isArray(context[collectionName]) &&
                       context[collectionName].length > 0 &&
                       this.getContextByteLength(context) > this.maxContextBytes) {
                    context[collectionName].pop();
                }
            }
            if (context.search) {
                context.search.returnedItems = context.items.length;
                context.search.hasMore =
                    context.search.hasMore ||
                    context.items.length < context.search.totalAvailableItems;
            }
            if (context.selection && context.selection.targetId &&
                !context.items.some(item =>
                    item.targetId === context.selection.targetId)) {
                context.selection.targetId = null;
            }
            return this.getContextByteLength(context) <= this.maxContextBytes;
        }

        formatStructuredQueryOutcome(outcome) {
            if (outcome.status === 'ok') {
                return `${outcome.queryId || 'query'}：已完整扫描 ${outcome.scannedCount}` +
                    ` 条，命中 ${outcome.matchedCount} 条，返回 ${outcome.returnedCount} 条证据。`;
            }
            if (outcome.status === 'ambiguous') {
                return `${outcome.queryId || 'query'}：路径存在 ${outcome.ambiguityCount}` +
                    ' 个同等候选，请依据 queryResults.ambiguities 的完整路径细化后重查。';
            }
            const errors = Array.isArray(outcome.errors)
                ? outcome.errors.join('；')
                : '查询执行失败。';
            return `${outcome.queryId || 'query'}：${errors}`;
        }

        getStructuredQueryResultKey(result) {
            return `${result && result.dataset || ''}\u001f` +
                `${result && result.queryId || ''}`;
        }

        isValidStructuredPrimitiveValue(value, allowArray) {
            if (value === null || typeof value === 'boolean') return true;
            if (typeof value === 'string') return value.length <= 5120;
            if (typeof value === 'number') return Number.isFinite(value);
            return Boolean(
                allowArray &&
                Array.isArray(value) &&
                value.length <= 100 &&
                value.every(item =>
                    !Array.isArray(item) &&
                    !this.isPlainObject(item) &&
                    this.isValidStructuredPrimitiveValue(item, false)));
        }

        isPlainObject(value) {
            return Boolean(
                value &&
                typeof value === 'object' &&
                !Array.isArray(value) &&
                Object.prototype.toString.call(value) === '[object Object]');
        }

        hasOnlyKeys(value, allowedKeys) {
            const allowed = new Set(allowedKeys);
            return this.isPlainObject(value) &&
                Object.keys(value).every(key => allowed.has(key));
        }

        stableStringify(value) {
            if (Array.isArray(value)) {
                return `[${value.map(item => this.stableStringify(item)).join(',')}]`;
            }
            if (this.isPlainObject(value)) {
                return `{${Object.keys(value).sort().map(key =>
                    `${JSON.stringify(key)}:${this.stableStringify(value[key])}`
                ).join(',')}}`;
            }
            return JSON.stringify(value);
        }

        getUtf8ByteLength(value) {
            return new TextEncoder().encode(String(value || '')).length;
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
            const optionalCountKeys = [
                'laborMaterialMachinery',
                'unitCostRates'
            ];
            return countKeys.every(key =>
                Number.isInteger(summary.counts[key]) &&
                summary.counts[key] >= 0) &&
                optionalCountKeys.every(key =>
                    summary.counts[key] === undefined ||
                    (Number.isInteger(summary.counts[key]) &&
                     summary.counts[key] >= 0));
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
                (result.exhaustive === undefined ||
                    typeof result.exhaustive === 'boolean') &&
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
                 !OPAQUE_ID_PATTERN.test(context.snapshotId) ||
                 !this.isValidContextSearchMetadata(context))) {
                return false;
            }
            if (context.counts !== undefined &&
                !this.isValidContextCounts(context.counts)) {
                return false;
            }
            if (context.laborMaterialMachineryTotals !== undefined &&
                !this.isValidLaborMaterialMachineryTotals(
                    context.laborMaterialMachineryTotals)) {
                return false;
            }
            if (context.laborMaterialMachineryRatios !== undefined &&
                !this.isValidLaborMaterialMachineryRatios(
                    context.laborMaterialMachineryRatios)) {
                return false;
            }

            if (context.laborMaterialMachinery !== undefined &&
                !Array.isArray(context.laborMaterialMachinery)) {
                return false;
            }
            if (context.catalogItems !== undefined &&
                (!Array.isArray(context.catalogItems) ||
                 !context.catalogItems.every(item =>
                    item &&
                    Object.keys(item).every(key => [
                        'ordinal',
                        'level',
                        'sectionName',
                        'rowType',
                        'sequence',
                        'code',
                        'name',
                        'specification',
                        'unit'
                    ].includes(key)) &&
                    Number.isInteger(item.ordinal) &&
                    item.ordinal > 0 &&
                    (item.level === undefined ||
                        (Number.isInteger(item.level) && item.level >= 0)) &&
                    [
                        'sectionName',
                        'rowType',
                        'sequence',
                        'code',
                        'name',
                        'specification',
                        'unit'
                    ].every(key =>
                        item[key] === undefined ||
                        (typeof item[key] === 'string' &&
                         item[key].length <= 5120))))) {
                return false;
            }
            if (context.queryResults !== undefined &&
                (!Array.isArray(context.queryResults) ||
                 context.queryResults.length > MAX_STRUCTURED_QUERY_RESULTS ||
                 !context.queryResults.every(result =>
                    this.isValidStructuredQueryResult(result)))) {
                return false;
            }
            if (this.getContextTargetIds(context).length >
                this.maxNavigationTargets) {
                return false;
            }

            const detailCount = this.getContextDetailCount(context);
            if (detailCount > this.maxTargets ||
                !context.items.every(item =>
                    item && typeof item.targetId === 'string' && OPAQUE_ID_PATTERN.test(item.targetId))) {
                return false;
            }

            try {
                return new TextEncoder().encode(JSON.stringify(context)).length <=
                    this.maxContextBytes;
            } catch {
                return false;
            }
        }

        isValidLaborMaterialMachineryTotals(totals) {
            if (!this.isPlainObject(totals) ||
                !Number.isInteger(totals.recordCount) ||
                totals.recordCount < 0 ||
                !['budgetAmount', 'marketAmount', 'differenceAmount']
                    .every(key =>
                        typeof totals[key] === 'number' &&
                        Number.isFinite(totals[key])) ||
                !Array.isArray(totals.categoryTotals) ||
                !Array.isArray(totals.sectionTotals)) {
                return false;
            }

            const isCategoryTotal = total =>
                this.isPlainObject(total) &&
                typeof total.category === 'string' &&
                total.category.length <= 128 &&
                Number.isInteger(total.recordCount) &&
                total.recordCount >= 0 &&
                ['budgetAmount', 'marketAmount', 'differenceAmount']
                    .every(key =>
                        typeof total[key] === 'number' &&
                        Number.isFinite(total[key]));
            if (totals.categoryTotals.length > 3 ||
                !totals.categoryTotals.every(isCategoryTotal)) {
                return false;
            }

            return totals.sectionTotals.every(section =>
                this.isPlainObject(section) &&
                Number.isInteger(section.index) &&
                section.index > 0 &&
                typeof section.name === 'string' &&
                section.name.length <= 5120 &&
                Number.isInteger(section.recordCount) &&
                section.recordCount >= 0 &&
                ['budgetAmount', 'marketAmount', 'differenceAmount']
                    .every(key =>
                        typeof section[key] === 'number' &&
                        Number.isFinite(section[key])) &&
                Array.isArray(section.categoryTotals) &&
                section.categoryTotals.length <= 3 &&
                section.categoryTotals.every(isCategoryTotal));
        }

        isValidLaborMaterialMachineryRatios(ratios) {
            if (!this.isPlainObject(ratios) ||
                !['budgetTotalAmount', 'marketTotalAmount',
                    'preferredTotalAmount']
                    .every(key =>
                        typeof ratios[key] === 'number' &&
                        Number.isFinite(ratios[key])) ||
                !['budgetAmount', 'marketAmount']
                    .includes(ratios.preferredAmountField) ||
                !Array.isArray(ratios.categoryRatios) ||
                !Array.isArray(ratios.sectionRatios)) {
                return false;
            }

            const isCategoryRatio = ratio =>
                this.isPlainObject(ratio) &&
                typeof ratio.category === 'string' &&
                ratio.category.length <= 128 &&
                ['budgetAmount', 'budgetRatio', 'marketAmount',
                    'marketRatio', 'preferredAmount', 'preferredRatio']
                    .every(key =>
                        typeof ratio[key] === 'number' &&
                        Number.isFinite(ratio[key]));
            if (ratios.categoryRatios.length > 3 ||
                !ratios.categoryRatios.every(isCategoryRatio)) {
                return false;
            }

            return ratios.sectionRatios.every(section =>
                this.isPlainObject(section) &&
                Number.isInteger(section.index) &&
                section.index > 0 &&
                typeof section.name === 'string' &&
                section.name.length <= 5120 &&
                ['budgetTotalAmount', 'marketTotalAmount',
                    'preferredTotalAmount']
                    .every(key =>
                        typeof section[key] === 'number' &&
                        Number.isFinite(section[key])) &&
                ['budgetAmount', 'marketAmount']
                    .includes(section.preferredAmountField) &&
                Array.isArray(section.categoryRatios) &&
                section.categoryRatios.length <= 3 &&
                section.categoryRatios.every(isCategoryRatio));
        }

        isValidContextCounts(counts) {
            if (!this.isPlainObject(counts)) return false;
            const required = [
                'nodes', 'materials', 'fees',
                'unitConfigItems', 'sectionConfigItems'
            ];
            const optional = ['laborMaterialMachinery', 'unitCostRates'];
            if (!required.every(key =>
                    Number.isInteger(counts[key]) && counts[key] >= 0) ||
                !optional.every(key =>
                    counts[key] === undefined ||
                    (Number.isInteger(counts[key]) && counts[key] >= 0))) {
                return false;
            }
            if (counts.total !== undefined) {
                const total = [...required, ...optional]
                    .reduce((sum, key) => sum + (counts[key] || 0), 0);
                if (!Number.isInteger(counts.total) || counts.total !== total) {
                    return false;
                }
            }
            return Object.keys(counts).every(key =>
                [...required, ...optional, 'total'].includes(key));
        }

        isValidContextSearchMetadata(context) {
            const search = context && context.search;
            if (!search ||
                !Number.isInteger(search.totalAvailableItems) ||
                search.totalAvailableItems < 0 ||
                !Number.isInteger(search.returnedItems) ||
                search.returnedItems !== context.items.length ||
                search.returnedItems > search.totalAvailableItems ||
                typeof search.hasMore !== 'boolean') {
                return false;
            }

            const catalogKeys = [
                'catalogQuery',
                'catalogTotalItems',
                'catalogReturnedItems',
                'catalogComplete'
            ];
            const hasCatalogMetadata = catalogKeys.some(key =>
                Object.prototype.hasOwnProperty.call(search, key));
            if (!hasCatalogMetadata) {
                return !Array.isArray(context.catalogItems) ||
                    context.catalogItems.length === 0;
            }

            if (!Array.isArray(context.catalogItems) ||
                typeof search.catalogQuery !== 'string' ||
                search.catalogQuery.length > 4000 ||
                !Number.isInteger(search.catalogTotalItems) ||
                search.catalogTotalItems < 0 ||
                !Number.isInteger(search.catalogReturnedItems) ||
                search.catalogReturnedItems !== context.catalogItems.length ||
                search.catalogReturnedItems > search.catalogTotalItems ||
                typeof search.catalogComplete !== 'boolean') {
                return false;
            }
            return !search.catalogComplete ||
                search.catalogReturnedItems === search.catalogTotalItems;
        }

        getContextTargetIds(context) {
            const targetIds = new Set();
            const add = targetId => {
                if (typeof targetId === 'string' &&
                    OPAQUE_ID_PATTERN.test(targetId)) {
                    targetIds.add(targetId);
                }
            };
            for (const item of context && Array.isArray(context.items)
                ? context.items
                : []) {
                add(item && item.targetId);
            }
            const visit = value => {
                if (Array.isArray(value)) {
                    value.forEach(visit);
                    return;
                }
                if (!this.isPlainObject(value)) return;
                add(value.targetId);
                if (value.field === 'targetId') add(value.value);
                Object.values(value).forEach(visit);
            };
            for (const result of context && Array.isArray(context.queryResults)
                ? context.queryResults
                : []) {
                visit(result);
            }
            return Array.from(targetIds);
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
            for (const targetId of this.getContextTargetIds(context)) {
                this.validTargets.add(targetId);
            }
        }

        /**
         * 全量分析只激活最终重点证据节点的定位能力，不能因为桌面端扫描过全部节点，
         * 就让模型定位未进入最终证据集合的任意 targetId。
         */
        rememberAnalysisTargets(analysis) {
            this.validTargets.clear();
            this.currentUnitProjectKey =
                analysis && typeof analysis.unitProjectKey === 'string'
                    ? analysis.unitProjectKey
                    : '';
            this.currentSnapshotId =
                analysis && typeof analysis.snapshotId === 'string'
                    ? analysis.snapshotId
                    : '';

            this.addAnalysisTargets(analysis);
        }

        /**
         * 在普通关键词检索更新摘要上下文以后，重新合并全量分析证据节点。
         * 这里只合并本次快照内已经出现在最终证据中的 ID，不会放开全部扫描节点。
         */
        addAnalysisTargets(analysis) {
            if (!analysis ||
                analysis.unitProjectKey !== this.currentUnitProjectKey ||
                analysis.snapshotId !== this.currentSnapshotId) {
                return;
            }

            const evidence = analysis && Array.isArray(analysis.itemEvidence)
                ? analysis.itemEvidence
                : [];
            for (const entry of evidence) {
                const targetId = entry && entry.item && entry.item.targetId;
                if (typeof targetId === 'string' &&
                    OPAQUE_ID_PATTERN.test(targetId)) {
                    this.validTargets.add(targetId);
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

        setStatus(status, details) {
            this.status = status;
            this.ensureStatusButton();
            if (!this.statusButton) return;

            const labels = {
                connecting: '工程数据：连接中',
                detached: '工程数据：未附加',
                attaching: '工程数据：附加中',
                attached: '工程数据：已附加',
                scanning: '工程数据：扫描中',
                analyzing: '工程数据：分析中',
                rendering: '工程数据：报告生成中',
                denied: '工程数据：未授权（点击重试）',
                no_active_unit_project: '工程数据：无当前单位工程',
                timeout: '工程数据：连接超时',
                error: '工程数据：不可用',
                unavailable: '工程数据：未连接'
            };
            let label = labels[status] || labels.error;
            if (status === 'scanning' &&
                details &&
                Number.isInteger(details.processed) &&
                Number.isInteger(details.total)) {
                label = details.total > 0
                    ? `工程数据：扫描中 ${details.processed} / ${details.total}`
                    : '工程数据：扫描准备中';
            }
            this.statusButton.textContent = label;
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
