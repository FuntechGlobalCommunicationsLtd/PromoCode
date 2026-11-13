// IIS-405-safe FURP Rewards Admin API client for mito-admin (Staging -> Live).
//
// Why this client exists:
//   1. Routes all Rewards admin traffic through furp02-core's authenticated gateway controller:
//        /api/v1/Affiliate/AdminSettings/Rewards
//   2. Tunnels non-GET mutations (POST, PUT, PATCH, DELETE) over HTTP POST to
//        POST /api/v1/Affiliate/AdminSettings/Rewards/engine
//      with envelope { method, path, query, body } so Windows Server IIS WebDAV/verb
//      filtering never rejects PUT/PATCH/DELETE with HTTP 405 Method Not Allowed.
//   3. Automatically unwraps FURP's Result<T> envelope ({ succeeded, code, message, data }).
//   4. Zero environment-specific hardcoding: reads mito-admin's existing API base URL
//      env var (Staging Pipeline 74 -> Live Pipeline 65 without code edits).

export const REWARDS_ADMIN_PREFIX = '/api/v1/Affiliate/AdminSettings/Rewards';
export const DEFAULT_STAGING_FURP_BASE_URL = 'https://furp02-staging.funtechcom.com';

let clientConfig = {
    getBaseUrl: null,
    getAccessToken: null,
    sameOrigin: false,
};

/**
 * Optional one-time configuration hook if mito-admin stores its API origin or JWT
 * in an in-memory store (e.g. Redux store or Axios instance) rather than storage.
 *
 * @param {{ getBaseUrl?: () => string, getAccessToken?: () => string | null, sameOrigin?: boolean }} options
 */
export function configureFurpRewardsClient(options = {}) {
    clientConfig = { ...clientConfig, ...options };
}

function readEnvBaseUrl() {
    if (clientConfig.sameOrigin) {
        return '';
    }
    if (typeof clientConfig.getBaseUrl === 'function') {
        const custom = clientConfig.getBaseUrl();
        if (custom !== undefined && custom !== null) return String(custom);
    }
    if (typeof globalThis !== 'undefined' && globalThis.__MITO_ADMIN_FURP_URL__) {
        return String(globalThis.__MITO_ADMIN_FURP_URL__);
    }
    if (typeof process !== 'undefined' && process && process.env) {
        const fromEnv =
            process.env.REACT_APP_FURP_BASE_URL ||
            process.env.REACT_APP_API_URL ||
            process.env.VITE_FURP_BASE_URL ||
            process.env.VITE_API_URL;
        if (fromEnv && String(fromEnv).trim()) {
            return String(fromEnv);
        }
    }
    return DEFAULT_STAGING_FURP_BASE_URL;
}

/**
 * Normalises any base URL input (e.g. "https://furp02-staging.funtechcom.com/",
 * "https://furp02-staging.funtechcom.com/api/v1", or
 * "https://furp02-staging.funtechcom.com/api/v1/") to a bare origin
 * ("https://furp02-staging.funtechcom.com") so REWARDS_ADMIN_PREFIX never produces
 * "//api/v1" or "/api/v1/api/v1".
 */
export function normaliseFurpOrigin(input) {
    const raw = String(input || '').trim();
    if (!raw || raw === '/') return '';
    return raw
        .replace(/\/+$/, '')
        .replace(/\/api\/v1$/i, '')
        .replace(/\/api$/i, '')
        .replace(/\/+$/, '');
}

/**
 * Resolves the active FURP origin (defaults to Staging https://furp02-staging.funtechcom.com
 * when no env override is set; Live Pipeline 65 overrides via REACT_APP_API_URL / REACT_APP_FURP_BASE_URL).
 */
export function resolveFurpOrigin() {
    return normaliseFurpOrigin(readEnvBaseUrl());
}

function readTokenFromPersistRoot(storage) {
    try {
        const raw = storage.getItem('persist:root') || storage.getItem('persist:auth');
        if (!raw) return '';
        const outer = JSON.parse(raw);
        for (const sliceKey of ['auth', 'user', 'session', 'admin']) {
            if (!outer[sliceKey]) continue;
            const slice = typeof outer[sliceKey] === 'string' ? JSON.parse(outer[sliceKey]) : outer[sliceKey];
            const tok =
                slice?.token ||
                slice?.accessToken ||
                slice?.access_token ||
                slice?.jwt ||
                slice?.user?.token ||
                slice?.user?.accessToken;
            if (tok && typeof tok === 'string') return tok;
        }
    } catch {
        // Ignore malformed storage entries
    }
    return '';
}

/**
 * Resolves the active Mito Admin Bearer JWT from configured getter or browser storage.
 */
export function resolveAdminBearerToken() {
    if (typeof clientConfig.getAccessToken === 'function') {
        const tok = clientConfig.getAccessToken();
        if (tok) return String(tok).replace(/^Bearer\s+/i, '').trim();
    }
    if (typeof window === 'undefined') return '';

    const candidateKeys = [
        'token',
        'accessToken',
        'access_token',
        'authToken',
        'jwt',
        'id_token',
        'mito_admin_jwt',
    ];

    for (const store of [window.sessionStorage, window.localStorage]) {
        if (!store) continue;
        try {
            for (const key of candidateKeys) {
                const val = store.getItem(key);
                if (val && typeof val === 'string' && val.trim()) {
                    const cleaned = val.trim().replace(/^"|"$/g, '').replace(/^Bearer\s+/i, '').trim();
                    if (cleaned && cleaned !== 'null' && cleaned !== 'undefined') return cleaned;
                }
            }
            const persisted = readTokenFromPersistRoot(store);
            if (persisted) return persisted.replace(/^Bearer\s+/i, '').trim();
        } catch {
            // Storage may be unavailable in restricted contexts
        }
    }

    try {
        const override = window.sessionStorage?.getItem('mito_admin_token');
        if (override && override.trim()) {
            return override.trim().replace(/^Bearer\s+/i, '').trim();
        }
    } catch {
        // Ignore
    }

    return '';
}

function normalisePathAndQuery(rawPath, extraQuery) {
    const str = String(rawPath || '').trim();
    const qIdx = str.indexOf('?');
    const pathnamePart = qIdx >= 0 ? str.slice(0, qIdx) : str;
    const inlineQs = qIdx >= 0 ? str.slice(qIdx + 1) : '';

    const cleanPath = pathnamePart
        .replace(/^\/+/, '')
        .replace(/^api\/+/i, '')
        .replace(/^\/+/, '');

    const mergedQuery = {};
    if (inlineQs) {
        const sp = new URLSearchParams(inlineQs);
        sp.forEach((v, k) => {
            if (v !== undefined && v !== null && v !== '') mergedQuery[k] = v;
        });
    }

    if (extraQuery) {
        if (typeof URLSearchParams !== 'undefined' && extraQuery instanceof URLSearchParams) {
            extraQuery.forEach((v, k) => {
                if (v !== undefined && v !== null && v !== '') mergedQuery[k] = v;
            });
        } else if (typeof extraQuery === 'object') {
            Object.entries(extraQuery).forEach(([k, v]) => {
                if (v !== undefined && v !== null && v !== '') {
                    mergedQuery[k] = String(v);
                }
            });
        }
    }

    const qsParams = new URLSearchParams();
    Object.entries(mergedQuery).forEach(([k, v]) => qsParams.set(k, String(v)));
    const qs = qsParams.toString();

    return {
        cleanPath,
        mergedQuery,
        queryString: qs ? `?${qs}` : '',
    };
}

function buildHeaders(extraHeaders = {}) {
    const headers = {
        Accept: 'application/json',
        'Content-Type': 'application/json',
        ...extraHeaders,
    };

    if (!headers.Authorization && !headers.authorization) {
        const token = resolveAdminBearerToken();
        if (token) {
            headers.Authorization = `Bearer ${token}`;
        }
    }
    return headers;
}

async function unwrapFurpResponse(res) {
    const json = await res.json().catch(() => null);
    const isEnvelope = Boolean(json && typeof json === 'object' && typeof json.succeeded === 'boolean');
    const failed = !res.ok || (isEnvelope && json.succeeded === false);

    if (failed) {
        const inner =
            isEnvelope && json.data && typeof json.data === 'object'
                ? json.data
                : json && typeof json === 'object'
                  ? json
                  : {};
        const status = (isEnvelope && Number(json.code)) || res.status || 500;
        const message =
            inner.message ||
            (typeof inner.error === 'string' &&
            !['VALIDATION', 'DUPLICATE_NAME', 'DUPLICATE_CURRENCY', 'DUPLICATE_CORRIDOR'].includes(inner.error)
                ? inner.error
                : '') ||
            (isEnvelope && json.message ? json.message : '') ||
            inner.error ||
            `Request failed (${status})`;

        const err = new Error(message);
        err.status = status;
        err.code = inner.code ?? (isEnvelope ? json.code : status);
        err.error = inner.error ?? message;
        err.fields = inner.fields ?? undefined;
        err.data = inner;
        throw err;
    }

    return isEnvelope ? json.data : json;
}

/**
 * Calls a FURP Rewards Engine admin endpoint through the IIS-405-safe gateway.
 *
 * - GET     -> GET  {FURP}/api/v1/Affiliate/AdminSettings/Rewards/engine/<cleanPath>?<query>
 * - Non-GET -> POST {FURP}/api/v1/Affiliate/AdminSettings/Rewards/engine
 *              body: { method, path: "/api/<cleanPath>", query, body }
 *
 * Never sends PUT, PATCH, or DELETE on the HTTP wire.
 */
export async function call(method, path, options = {}) {
    const upper = String(method || 'GET').toUpperCase();
    const { query, body, headers: extraHeaders } = options || {};
    const { cleanPath, mergedQuery, queryString } = normalisePathAndQuery(path, query);
    const prefix = `${resolveFurpOrigin()}${REWARDS_ADMIN_PREFIX}`;
    const headers = buildHeaders(extraHeaders);

    let res;
    if (upper === 'GET') {
        res = await fetch(`${prefix}/engine/${cleanPath}${queryString}`, {
            method: 'GET',
            headers,
        });
    } else {
        res = await fetch(`${prefix}/engine`, {
            method: 'POST',
            headers,
            body: JSON.stringify({
                method: upper,
                path: `/api/${cleanPath}`,
                query: mergedQuery,
                body: body !== undefined ? body : {},
            }),
        });
    }

    return unwrapFurpResponse(res);
}

/**
 * Reads master affiliate/site programme switches:
 * GET {FURP}/api/v1/Affiliate/AdminSettings/Rewards
 */
export async function getProgrammeSettings(affiliateId) {
    const prefix = `${resolveFurpOrigin()}${REWARDS_ADMIN_PREFIX}`;
    const qs = affiliateId ? `?affiliateId=${encodeURIComponent(affiliateId)}` : '';
    const res = await fetch(`${prefix}${qs}`, {
        method: 'GET',
        headers: buildHeaders(),
    });
    return unwrapFurpResponse(res);
}

/**
 * Updates master affiliate/site programme switches:
 * POST {FURP}/api/v1/Affiliate/AdminSettings/Rewards
 */
export async function updateProgrammeSettings(payload) {
    const prefix = `${resolveFurpOrigin()}${REWARDS_ADMIN_PREFIX}`;
    const res = await fetch(prefix, {
        method: 'POST',
        headers: buildHeaders(),
        body: JSON.stringify(payload || {}),
    });
    return unwrapFurpResponse(res);
}

export const get = (path, query, options = {}) => call('GET', path, { ...options, query });
export const post = (path, body, options = {}) => call('POST', path, { ...options, body });
export const put = (path, body, options = {}) => call('PUT', path, { ...options, body });
export const patch = (path, body, options = {}) => call('PATCH', path, { ...options, body });
export const del = (path, options = {}) => call('DELETE', path, options);

const furpRewardsAdminClient = {
    REWARDS_ADMIN_PREFIX,
    DEFAULT_STAGING_FURP_BASE_URL,
    configure: configureFurpRewardsClient,
    normaliseFurpOrigin,
    resolveFurpOrigin,
    resolveAdminBearerToken,
    call,
    get,
    post,
    put,
    patch,
    del,
    delete: del,
    getProgrammeSettings,
    updateProgrammeSettings,
};

export default furpRewardsAdminClient;
