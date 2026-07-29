const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

function loadBridge() {
    const source = fs.readFileSync(
        path.join(__dirname, '..', 'wwwroot', 'js', 'hcsoft-bridge.js'),
        'utf8');
    const sandbox = {
        URLSearchParams,
        TextEncoder,
        console,
        window: {
            location: { search: '' }
        }
    };
    vm.runInNewContext(source, sandbox, { filename: 'hcsoft-bridge.js' });
    return sandbox.window.hcsoftBridge;
}

function createContext(itemCount = 1) {
    return {
        schemaVersion: '1.0',
        unitProjectKey: '0123456789abcdef01234567',
        items: Array.from({ length: itemCount }, (_, index) => ({
            targetId: index.toString(16).padStart(24, '0')
        })),
        materials: [],
        fees: []
    };
}

function createStagedContext(itemCount = 1) {
    return {
        ...createContext(itemCount),
        schemaVersion: '2.0',
        snapshotId: 'abcdef0123456789abcdef01',
        search: {
            totalAvailableItems: 1000,
            returnedItems: itemCount,
            hasMore: true
        }
    };
}

test('ordinary browser remains usable without the desktop bridge', async () => {
    const bridge = loadBridge();
    assert.equal(bridge.isAvailable, false);
    assert.equal(bridge.maxModelSearchRounds, 10);
    const result = await bridge.getContext('test', false);
    assert.equal(result.status, 'unavailable');
    assert.equal(result.context, null);
});

test('context validation enforces the 300-detail limit', () => {
    const bridge = loadBridge();
    assert.equal(bridge.isValidContext(createContext(300)), true);
    assert.equal(bridge.isValidContext(createContext(301)), false);
    assert.equal(bridge.isValidContext(createStagedContext(20)), true);
    assert.equal(
        bridge.isValidContext({ ...createStagedContext(20), snapshotId: 'invalid' }),
        false);
});

test('staged context reads summary, search and at most 20 details in order', async () => {
    const bridge = loadBridge();
    bridge.webview = {};
    bridge.bridgeSessionId = 'test-session';
    bridge.capabilities = ['context.staged-read'];
    bridge.contextAttachmentEnabled = true;
    bridge.setStatus = () => {};

    const calls = [];
    bridge.request = async (type, payload) => {
        calls.push({ type, payload });
        if (type === 'context.summary.get') {
            return {
                status: 'ok',
                snapshotId: 'abcdef0123456789abcdef01',
                unitProjectKey: '0123456789abcdef01234567',
                summary: {
                    schemaVersion: '2.0',
                    unitProjectKey: '0123456789abcdef01234567',
                    unitProject: { name: '测试工程' },
                    selection: null,
                    sections: [],
                    counts: {
                        nodes: 1000,
                        materials: 10,
                        fees: 5,
                        unitConfigItems: 2,
                        sectionConfigItems: 3
                    }
                }
            };
        }

        if (type === 'context.search') {
            return {
                status: 'ok',
                snapshotId: 'abcdef0123456789abcdef01',
                unitProjectKey: '0123456789abcdef01234567',
                items: Array.from({ length: 50 }, (_, index) => ({
                    targetId: index.toString(16).padStart(24, '0')
                })),
                nextCursor: 'fedcba9876543210fedcba98',
                hasMore: true,
                totalCount: 1000
            };
        }

        if (type === 'context.details.get') {
            assert.equal(payload.targetIds.length, 20);
            return {
                status: 'ok',
                context: createStagedContext(20)
            };
        }

        throw new Error(`unexpected request: ${type}`);
    };

    const result = await bridge.prepareContext('混凝土清单价格');
    assert.equal(result.status, 'ok');
    assert.deepEqual(
        calls.map(call => call.type),
        ['context.summary.get', 'context.search', 'context.details.get']);
    assert.equal(bridge.validTargets.size, 20);
});

test('paged query reads later search pages, expands details in batches and commits final targets', async () => {
    const bridge = loadBridge();
    bridge.webview = {};
    bridge.bridgeSessionId = 'test-session';
    bridge.capabilities = ['context.staged-read', 'context.paged-query-v2'];
    bridge.contextAttachmentEnabled = true;
    bridge.setStatus = () => {};

    const snapshotId = 'abcdef0123456789abcdef01';
    const unitProjectKey = '0123456789abcdef01234567';
    const calls = [];
    bridge.request = async (type, payload) => {
        calls.push({ type, payload });
        if (type === 'context.summary.get') {
            return {
                status: 'ok',
                snapshotId,
                unitProjectKey,
                summary: {
                    schemaVersion: '2.0',
                    unitProjectKey,
                    unitProject: { name: '测试工程' },
                    selection: null,
                    sections: [],
                    counts: {
                        nodes: 80,
                        materials: 1,
                        fees: 1,
                        unitConfigItems: 0,
                        sectionConfigItems: 0
                    }
                }
            };
        }

        if (type === 'context.search') {
            const start = payload.cursor ? 50 : 0;
            const count = payload.cursor ? 20 : 50;
            return {
                status: 'ok',
                snapshotId,
                unitProjectKey,
                items: Array.from({ length: count }, (_, offset) => ({
                    targetId: (start + offset).toString(16).padStart(24, '0'),
                    rowType: 'QD',
                    matchRank: 2,
                    matchKind: 'text'
                })),
                nextCursor: payload.cursor ? null : 'fedcba9876543210fedcba98',
                hasMore: !payload.cursor,
                totalCount: 70
            };
        }

        if (type === 'context.details.get') {
            assert.ok(payload.targetIds.length <= 20);
            return {
                status: 'ok',
                context: {
                    schemaVersion: '2.0',
                    snapshotId,
                    unitProjectKey,
                    unitProject: { name: '测试工程', configItems: [] },
                    totals: {},
                    sections: [],
                    items: payload.targetIds.map(targetId => ({ targetId })),
                    materials: payload.includeSupplementaryData ? [{ code: 'M1' }] : [],
                    fees: payload.includeSupplementaryData ? [{ code: 'F1' }] : [],
                    search: {
                        totalAvailableItems: 70,
                        returnedItems: payload.targetIds.length,
                        hasMore: true
                    }
                }
            };
        }

        if (type === 'context.targets.commit') {
            return {
                status: 'ok',
                unitProjectKey,
                acceptedCount: payload.targetIds.length
            };
        }

        throw new Error(`unexpected request: ${type}`);
    };

    const result = await bridge.prepareContext('查找后部清单');
    assert.equal(result.status, 'ok');
    assert.equal(result.context.items.length, 70);

    const searchCalls = calls.filter(call => call.type === 'context.search');
    const detailCalls = calls.filter(call => call.type === 'context.details.get');
    const commitCalls = calls.filter(call => call.type === 'context.targets.commit');
    assert.equal(searchCalls.length, 2);
    assert.equal(searchCalls[1].payload.cursor, 'fedcba9876543210fedcba98');
    assert.equal(detailCalls.length, 4);
    assert.deepEqual(
        detailCalls.map(call => call.payload.targetIds.length),
        [20, 20, 20, 10]);
    assert.deepEqual(
        detailCalls.map(call => call.payload.includeSupplementaryData),
        [true, false, false, false]);
    assert.ok(detailCalls.every(call => call.payload.deferTargetActivation === true));
    assert.equal(commitCalls.length, 1);
    assert.equal(commitCalls[0].payload.targetIds.length, 70);
    assert.equal(bridge.validTargets.size, 70);
});

test('detaching releases the current staged snapshot and clears locate targets', async () => {
    const bridge = loadBridge();
    const context = createStagedContext();
    bridge.webview = {};
    bridge.bridgeSessionId = 'test-session';
    bridge.contextAttachmentEnabled = true;
    bridge.setStatus = () => {};
    bridge.rememberTargets(context);

    let releasedSnapshotId = '';
    bridge.request = async (type, payload) => {
        assert.equal(type, 'context.snapshot.release');
        releasedSnapshotId = payload.snapshotId;
        return { released: true };
    };

    bridge.detachContext();
    await Promise.resolve();

    assert.equal(releasedSnapshotId, context.snapshotId);
    assert.equal(bridge.contextAttachmentEnabled, false);
    assert.equal(bridge.validTargets.size, 0);
    assert.equal(bridge.currentSnapshotId, '');
});

test('locate actions are accepted only for targets from the latest context', () => {
    const bridge = loadBridge();
    const context = createContext();
    bridge.rememberTargets(context);

    const validAction = {
        type: 'locate',
        unitProjectKey: context.unitProjectKey,
        targetId: context.items[0].targetId,
        label: '定位到清单'
    };
    assert.equal(bridge.isValidLocateAction(validAction), true);
    assert.equal(bridge.isValidLocateAction({ ...validAction, targetId: 'f'.repeat(24) }), false);
    assert.equal(bridge.isValidLocateAction({ ...validAction, unexpected: true }), false);
});

test('action tags assembled from stream fragments are removed and parsed', () => {
    const bridge = loadBridge();
    const context = createContext();
    bridge.rememberTargets(context);

    const fragments = [
        '答复正文\n<hcsoft_',
        'action>{"type":"locate","unitProjectKey":"0123456789abcdef01234567",',
        '"targetId":"000000000000000000000000","label":"定位到清单"}</hcsoft_action>'
    ];
    const result = bridge.extractActions(fragments.join(''));

    assert.equal(result.content, '答复正文');
    assert.equal(result.actions.length, 1);
    assert.equal(result.actions[0].type, 'locate');
});

test('model search tags are parsed as strict read-only query lists', () => {
    const bridge = loadBridge();
    const result = bridge.extractSearchRequests(
        '暂不回答<hcsoft_search>' +
        '{"queries":["A.2下面的节点","阀门 清单"],"reason":"缺少子项"}' +
        '</hcsoft_search>');

    assert.equal(result.foundTag, true);
    assert.deepEqual(
        Array.from(result.queries),
        ['A.2下面的节点', '阀门 清单']);
    assert.equal(result.content, '暂不回答');

    const rejected = bridge.extractSearchRequests(
        '<hcsoft_search>{"queries":["A.2"],"write":true}</hcsoft_search>');
    assert.equal(rejected.foundTag, true);
    assert.equal(rejected.queries.length, 0);
});

test('model-directed searches merge new details and recommit the final locate whitelist', async () => {
    const bridge = loadBridge();
    bridge.webview = {};
    bridge.bridgeSessionId = 'test-session';
    bridge.capabilities = ['context.staged-read', 'context.paged-query-v2'];

    const currentContext = createStagedContext(1);
    currentContext.materials = [{ code: 'OLD', name: '原材料' }];
    bridge.rememberTargets(currentContext);
    bridge.currentSummary = {
        schemaVersion: '2.0',
        unitProjectKey: currentContext.unitProjectKey,
        unitProject: { name: '测试工程' },
        sections: [],
        counts: {
            nodes: 100,
            materials: 10,
            fees: 5,
            unitConfigItems: 0,
            sectionConfigItems: 0
        }
    };

    bridge.readRelevantSearchPages = async () => ({
        status: 'ok',
        targetIds: [],
        totalCount: 100,
        catalogReturnedCount: 1,
        hasMore: true
    });
    let detailIndex = 0;
    bridge.readDetailBatches = async () => {
        detailIndex++;
        const targetId = detailIndex.toString(16).padStart(24, '0');
        return {
            status: 'ok',
            context: {
                ...createStagedContext(0),
                items: [{ targetId }],
                materials: [{ code: `NEW${detailIndex}`, name: '新增材料' }],
                fees: []
            }
        };
    };

    let committedTargets = [];
    bridge.request = async (type, payload) => {
        assert.equal(type, 'context.targets.commit');
        committedTargets = payload.targetIds;
        return {
            status: 'ok',
            unitProjectKey: currentContext.unitProjectKey,
            acceptedCount: payload.targetIds.length
        };
    };

    const result = await bridge.extendContext(
        ['A.2下面的节点', '阀门清单'],
        currentContext);
    assert.equal(result.status, 'ok');
    assert.equal(result.context.items.length, 3);
    assert.equal(result.context.materials.length, 3);
    assert.equal(result.addedRecords, 4);
    assert.equal(committedTargets.length, 3);
    assert.equal(bridge.validTargets.size, 3);
});
