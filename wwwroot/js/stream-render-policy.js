(function (root, factory) {
    if (typeof module === 'object' && module.exports) {
        module.exports = factory();
    } else {
        root.hcsoftStreamRenderPolicy = factory();
    }
}(typeof globalThis !== 'undefined' ? globalThis : this, function () {
    'use strict';

    const MIN_DELAY_MS = 80;
    const MAX_DELAY_MS = 750;
    const RENDER_TIME_MULTIPLIER = 4;

    function getDelay(lastRenderDuration) {
        const duration = Number(lastRenderDuration) || 0;
        if (duration <= 0) return MIN_DELAY_MS;

        return Math.min(
            MAX_DELAY_MS,
            Math.max(MIN_DELAY_MS, Math.ceil(duration * RENDER_TIME_MULTIPLIER)));
    }

    return Object.freeze({
        MIN_DELAY_MS,
        MAX_DELAY_MS,
        RENDER_TIME_MULTIPLIER,
        getDelay
    });
}));
