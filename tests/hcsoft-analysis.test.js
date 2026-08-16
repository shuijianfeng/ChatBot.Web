const assert = require('node:assert/strict');
const test = require('node:test');

const {
    HcsoftAnalysisController
} = require('../wwwroot/js/hcsoft-analysis.js');

const snapshotId = '111111111111111111111111';
const unitProjectKey = '222222222222222222222222';
const analysisSessionId = '333333333333333333333333';
const sourceFingerprint = '555555555555555555555555';

function createManifest(overrides = {}) {
    const manifest = {
        nodes: 2,
        materials: 1,
        fees: 1,
        unitConfigItems: 0,
        sectionConfigItems: 0,
        total: 4,
        rowTypes: {
            BD: 0,
            TREE: 0,
            GROUP: 0,
            XM: 0,
            QD: 2,
            DE: 0,
            CL: 0
        },
        sourceFingerprint
    };
    return Object.assign(manifest, overrides);
}

function createCoverage(manifest, processed, complete = false) {
    const datasets = {
        nodes: {
            expected: manifest.nodes,
            processed: processed.nodes || 0
        },
        materials: {
            expected: manifest.materials,
            processed: processed.materials || 0
        },
        fees: {
            expected: manifest.fees,
            processed: processed.fees || 0
        },
        unitConfigItems: {
            expected: manifest.unitConfigItems,
            processed: processed.unitConfigItems || 0
        },
        sectionConfigItems: {
            expected: manifest.sectionConfigItems,
            processed: processed.sectionConfigItems || 0
        }
    };
    const processedTotal = Object.values(datasets)
        .reduce((sum, item) => sum + item.processed, 0);
    return {
        complete,
        expectedTotal: manifest.total,
        processedTotal,
        duplicateChunks: 0,
        filteredItems: 0,
        missingSubmittedItems: 0,
        missingSubmittedFees: 0,
        invalidNumericValues: 0,
        datasets,
        rowTypes: {
            BD: { expected: 0, processed: 0 },
            TREE: { expected: 0, processed: 0 },
            GROUP: { expected: 0, processed: 0 },
            XM: { expected: 0, processed: 0 },
            QD: {
                expected: manifest.rowTypes.QD || 0,
                processed: Math.min(
                    processed.nodes || 0,
                    manifest.rowTypes.QD || 0)
            },
            DE: { expected: 0, processed: 0 },
            CL: { expected: 0, processed: 0 }
        }
    };
}

function createReady(manifest) {
    return {
        status: 'ok',
        analysisSessionId,
        snapshotId,
        unitProjectKey,
        policyVersion: 'cost-policy-v1',
        cursor: '444444444444444444444444',
        manifest
    };
}

function createResult(manifest, coverage) {
    return {
        status: 'ok',
        analysis: {
            schemaVersion: 'analysis-1.0',
            analysisSessionId,
            snapshotId,
            unitProjectKey,
            policyVersion: 'cost-policy-v1',
            sourceFingerprint,
            semanticEvidenceMode: 'prioritized',
            coverage,
            aggregates: {},
            itemEvidence: [{
                evidenceId: 'aaaaaaaaaaaaaaaaaaaaaaaa',
                item: {
                    targetId: 'aaaaaaaaaaaaaaaaaaaaaaaa'
                }
            }],
            materialEvidence: [],
            feeEvidence: []
        }
    };
}

function createBridge(request) {
    const statuses = [];
    const remembered = [];
    return {
        supportsChunkedAnalysis: true,
        currentSnapshotId: snapshotId,
        maxAnalysisChunkRecords: 50,
        maxAnalysisResultBytes: 512 * 1024,
        request,
        setStatus(status, details) {
            statuses.push({ status, details });
        },
        rememberAnalysisTargets(analysis) {
            remembered.push(analysis);
        },
        statuses,
        remembered
    };
}

test('控制器逐片读取五类数据并只返回紧凑累计结果', async () => {
    const manifest = createManifest();
    const calls = [];
    const chunks = [
        {
            processed: { nodes: 2 },
            recordCount: 2,
            hasMore: true,
            nextCursor: '666666666666666666666666'
        },
        {
            processed: { nodes: 2, materials: 1 },
            recordCount: 1,
            hasMore: true,
            nextCursor: '777777777777777777777777'
        },
        {
            processed: { nodes: 2, materials: 1, fees: 1 },
            recordCount: 1,
            hasMore: false,
            nextCursor: null
        }
    ];
    let chunkIndex = 0;
    const bridge = createBridge(async (type, payload) => {
        calls.push({ type, payload: { ...payload } });
        if (type === 'analysis.start') {
            return createReady(manifest);
        }
        if (type === 'analysis.chunk.next') {
            const item = chunks[chunkIndex++];
            return {
                status: 'ok',
                analysisSessionId,
                snapshotId,
                unitProjectKey,
                chunkId: (8 + chunkIndex).toString(16).repeat(24),
                ordinal: chunkIndex,
                recordCount: item.recordCount,
                hasMore: item.hasMore,
                nextCursor: item.nextCursor,
                coverage: createCoverage(
                    manifest,
                    item.processed,
                    !item.hasMore)
            };
        }
        if (type === 'analysis.result.get') {
            return createResult(
                manifest,
                createCoverage(
                    manifest,
                    { nodes: 2, materials: 1, fees: 1 },
                    true));
        }
        throw new Error(`unexpected request: ${type}`);
    });
    const controller = new HcsoftAnalysisController(bridge);

    const result = await controller.scan(
        'engineering-cost-analysis-report');

    assert.equal(result.status, 'ok');
    assert.equal(result.addedRecords, 4);
    assert.equal(bridge.remembered.length, 1);
    assert.deepEqual(
        calls.map(call => call.type),
        [
            'analysis.start',
            'analysis.chunk.next',
            'analysis.chunk.next',
            'analysis.chunk.next',
            'analysis.result.get'
        ]);
    assert.equal(bridge.statuses.at(-1).status, 'attached');
    assert.deepEqual(
        bridge.statuses
            .filter(entry => entry.status === 'scanning')
            .at(-1).details,
        { processed: 4, total: 4 });
});

test('分片超时使用同一 cursor 重试，避免累计重复', async () => {
    const manifest = createManifest({
        nodes: 1,
        materials: 0,
        fees: 0,
        total: 1,
        rowTypes: {
            BD: 0,
            TREE: 0,
            GROUP: 0,
            XM: 0,
            QD: 1,
            DE: 0,
            CL: 0
        }
    });
    const chunkCursors = [];
    let timedOut = false;
    const completeCoverage = createCoverage(
        manifest,
        { nodes: 1 },
        true);
    const bridge = createBridge(async (type, payload) => {
        if (type === 'analysis.start') {
            return createReady(manifest);
        }
        if (type === 'analysis.chunk.next') {
            chunkCursors.push(payload.cursor);
            if (!timedOut) {
                timedOut = true;
                const error = new Error('timeout');
                error.code = 'TIMEOUT';
                throw error;
            }
            return {
                status: 'ok',
                analysisSessionId,
                snapshotId,
                unitProjectKey,
                chunkId: '999999999999999999999999',
                ordinal: 1,
                recordCount: 1,
                hasMore: false,
                nextCursor: null,
                coverage: completeCoverage
            };
        }
        if (type === 'analysis.result.get') {
            return createResult(manifest, completeCoverage);
        }
        throw new Error(`unexpected request: ${type}`);
    });
    const controller = new HcsoftAnalysisController(bridge);

    const result = await controller.scan(
        'engineering-cost-audit-report');

    assert.equal(result.status, 'ok');
    assert.deepEqual(chunkCursors, [
        '444444444444444444444444',
        '444444444444444444444444'
    ]);
});

test('最终结果缺少任一数据集覆盖时拒绝进入报告', async () => {
    const manifest = createManifest();
    const controller = new HcsoftAnalysisController(createBridge(
        async () => {
            throw new Error('not used');
        }));
    const ready = createReady(manifest);
    const invalidCoverage = createCoverage(
        manifest,
        { nodes: 2, materials: 1, fees: 1 },
        true);
    delete invalidCoverage.datasets.fees;

    assert.equal(
        controller.isValidResult(
            createResult(manifest, invalidCoverage),
            ready),
        false);
});
