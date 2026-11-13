// Authenticated JSON -> Blob CSV download helper for mito-admin.
//
// Direct browser navigation (<a href="/api/...csv">) cannot attach the Mito Admin
// Authorization: Bearer <JWT> header and hits mito-admin's SPA origin instead of FURP.
// This helper requests { csv, filename, content_type } inside FURP's Result<T> envelope
// via furpRewardsAdminClient (sending Accept: application/json + ?format=json + Bearer)
// and triggers an in-browser Blob download.

import { call } from './furpRewardsAdminClient';

/**
 * Fetches a CSV export endpoint via the authenticated FURP Rewards gateway
 * and triggers a client-side file download.
 *
 * @param {string} path - Engine CSV route (e.g. "promocodes.csv" or "/api/referral/tracking.csv?rule_id=1")
 * @param {Record<string, any> | URLSearchParams} [query] - Optional query filters
 * @param {string} [fallbackFilename] - Fallback filename if server omits filename
 */
export async function downloadCsv(path, query = {}, fallbackFilename = '') {
    const queryObj = {};
    if (query) {
        if (typeof URLSearchParams !== 'undefined' && query instanceof URLSearchParams) {
            query.forEach((v, k) => {
                if (v !== undefined && v !== null && v !== '') queryObj[k] = v;
            });
        } else if (typeof query === 'object') {
            Object.entries(query).forEach(([k, v]) => {
                if (v !== undefined && v !== null && v !== '') queryObj[k] = v;
            });
        }
    }

    const data = await call('GET', path, {
        query: { ...queryObj, format: 'json' },
    });

    const csvText =
        typeof data === 'string'
            ? data
            : typeof data?.csv === 'string'
              ? data.csv
              : '';

    const inferredFromPath =
        String(path || '')
            .split('?')[0]
            .split('/')
            .filter(Boolean)
            .pop() || 'export.csv';

    const filename =
        (data && typeof data === 'object' && data.filename) ||
        fallbackFilename ||
        (inferredFromPath.endsWith('.csv') ? inferredFromPath : `${inferredFromPath}.csv`);

    const contentType =
        (data && typeof data === 'object' && data.content_type) ||
        'text/csv;charset=utf-8';

    const blob = new Blob([csvText], { type: contentType });
    const blobUrl = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = blobUrl;
    a.download = filename;
    a.rel = 'noopener';
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(blobUrl), 1000);
}

/**
 * Drop-in replacement for legacy downloadUrl('/api/...csv?...') calls.
 */
export function downloadCsvUrl(url, fallbackFilename = '') {
    return downloadCsv(url, {}, fallbackFilename);
}

export default downloadCsv;
