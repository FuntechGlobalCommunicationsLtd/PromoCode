// IIS-compatible admin proxy for Mito Admin (Layer 1).
//
// In production Mito Admin does not talk to the referral/promo/bonus engine directly: it calls
//   {FURP}/api/v1/Affiliate/AdminSettings/Rewards[/engine/<engine path>]
// and FURP (behind IIS, which rejects PUT / PATCH / DELETE with 405) forwards to the engine. To get around IIS,
// Mito Admin "tunnels" every non-GET verb through POST:
//
//   POST /api/v1/Affiliate/AdminSettings/Rewards/engine
//   { "method": "PUT", "path": "/api/referral-rules/7", "query": { "x": 1 }, "body": { ... } }
//
// This module makes the prototype server speak exactly that dialect so Mito Admin can be pointed at it unchanged:
//
//   GET  .../Rewards                       -> programme switches (read-only defaults, Result<T> envelope)
//   POST .../Rewards                       -> echo/update of the in-memory switches (prototype only), or a tunnelled call
//   GET  .../Rewards/engine/<path>         -> forwarded as GET /api/<path>
//   POST .../Rewards/engine[/<path>]       -> verb-tunnelled { method, path, query, body } OR a plain POST /api/<path>
//   PUT | PATCH | DELETE .../Rewards/...   -> 405 (what IIS does) with a hint to use the POST tunnel
//
// Forwarding is done by rewriting the request in place and letting the normal Express stack answer it, so every
// existing /api/* route (and its auth / validation) behaves exactly as before. Only JSON responses are wrapped in
// FURP's Result<T> envelope: { succeeded, code, message, data }. CSV / text responses pass through untouched.
//
// Purely additive: requests that do not start with the admin prefix are never touched.

const PREFIX = '/api/v1/affiliate/adminsettings/rewards';
const TUNNEL_VERBS = new Set(['GET', 'POST', 'PUT', 'PATCH', 'DELETE']);

// In-memory programme switches (the prototype has no per-affiliate settings table; FURP keeps these on
// dbo.AffiliateAllowedService / dbo.LookUpApplication).
const settings = {
    isRewards: true,
    isRewardReferral: true,
    isRewardBonusScheme: String(process.env.BONUS_ENABLED ?? 'true').toLowerCase() !== 'false',
    isRewardPromo: String(process.env.PROMO_ENABLED ?? 'true').toLowerCase() !== 'false',
};

function resultBody(status, payload) {
    const ok = status >= 200 && status < 400;
    if (payload && typeof payload === 'object' && !Array.isArray(payload) && typeof payload.succeeded === 'boolean') return payload;
    const message = (payload && typeof payload === 'object' && !Array.isArray(payload) && (payload.message || (typeof payload.error === 'string' && payload.error)))
        || (ok ? 'Success' : 'Request failed');
    return { succeeded: ok, code: status, message: String(message), data: payload === undefined ? null : payload };
}

function sendResult(res, status, payload) {
    res.status(status).json(resultBody(status, payload));
}

// Turn a caller supplied engine path into a safe in-app path, or null when it must not be forwarded.
function enginePath(raw) {
    let p = String(raw || '').trim();
    if (!p || /^[a-z][a-z0-9+.-]*:/i.test(p) || p.startsWith('//') || p.includes('..') || p.includes('\\')) return null;
    const q = p.indexOf('?');
    const query = q >= 0 ? p.slice(q + 1) : '';
    p = q >= 0 ? p.slice(0, q) : p;
    if (!p.startsWith('/')) p = '/' + p;
    if (!/^\/api(\/|$)/i.test(p)) p = '/api' + p;
    // Never loop back into this proxy
    if (p.toLowerCase().startsWith('/api/v1/affiliate/adminsettings')) return null;
    return { path: p, query };
}

function toQueryString(...parts) {
    const params = new URLSearchParams();
    for (const part of parts) {
        if (!part) continue;
        if (typeof part === 'string') {
            for (const [k, v] of new URLSearchParams(part)) params.set(k, v);
        } else if (typeof part === 'object') {
            for (const [k, v] of Object.entries(part)) {
                if (v === undefined || v === null) continue;
                if (Array.isArray(v)) v.forEach((x) => params.append(k, String(x)));
                else params.set(k, String(v));
            }
        }
    }
    const s = params.toString();
    return s ? '?' + s : '';
}

// Wrap res.json so the downstream route's JSON answer leaves as a Result<T>.
function wrapJson(res) {
    const original = res.json.bind(res);
    res.json = (body) => original(resultBody(res.statusCode || 200, body));
}

function furpAdminCompat(req, res, next) {
    const pathOnly = String(req.originalUrl || req.url || '').split('?')[0];
    const lower = pathOnly.toLowerCase().replace(/\/+$/, '');
    if (lower !== PREFIX && !lower.startsWith(PREFIX + '/')) return next();

    const method = String(req.method || 'GET').toUpperCase();
    if (method === 'OPTIONS') return next(); // cors() answers pre-flights

    if (method === 'PUT' || method === 'PATCH' || method === 'DELETE') {
        return sendResult(res, 405, { message: `${method} is blocked on this host (IIS). POST { method, path, query, body } to ${pathOnly.replace(/\/engine.*$/i, '')}/engine instead.` });
    }

    const rest = lower === PREFIX ? '' : pathOnly.slice(PREFIX.length).replace(/^\/+/, '').replace(/\/+$/, '');
    const search = String(req.originalUrl || '').includes('?') ? String(req.originalUrl).slice(String(req.originalUrl).indexOf('?') + 1) : '';
    const body = req.body && typeof req.body === 'object' ? req.body : {};
    const isTunnel = method === 'POST' && typeof body.method === 'string' && typeof body.path === 'string';

    // Verb tunnel: valid on the Rewards root and on /engine[/*]
    if (isTunnel) {
        const verb = body.method.toUpperCase();
        if (!TUNNEL_VERBS.has(verb)) return sendResult(res, 400, { message: `Unsupported tunnelled method ${body.method}.` });
        const target = enginePath(body.path);
        if (!target) return sendResult(res, 400, { message: 'Invalid engine path.' });
        req.method = verb;
        req.url = target.path + toQueryString(target.query, body.query);
        req.body = body.body !== undefined && body.body !== null ? body.body : {};
        wrapJson(res);
        return next();
    }

    const isEngine = rest === 'engine' || rest.startsWith('engine/');
    if (!isEngine) {
        // .../Rewards root
        if (rest !== '') return sendResult(res, 404, { message: 'Unknown rewards admin endpoint.' });
        if (method === 'GET') return sendResult(res, 200, { ...settings });
        if (method === 'POST') {
            for (const k of Object.keys(settings)) if (typeof body[k] === 'boolean') settings[k] = body[k];
            return sendResult(res, 200, { ...settings });
        }
        return sendResult(res, 405, { message: `${method} is not supported.` });
    }

    // Plain (non-tunnelled) GET / POST on /engine/<path>
    const sub = rest === 'engine' ? '' : rest.slice('engine/'.length);
    if (!sub) return sendResult(res, 400, { message: 'Provide an engine path, e.g. /engine/referral-rules, or POST { method, path, query, body }.' });
    const target = enginePath('/' + sub);
    if (!target) return sendResult(res, 400, { message: 'Invalid engine path.' });
    req.url = target.path + toQueryString(search);
    wrapJson(res);
    return next();
}

module.exports = { furpAdminCompat, PREFIX, resultBody };
