// REFERRAL contract: the rules the Referral module (server/referral.js, wired by server/referralHost.js) is implemented to.
// Runs against the real app, so the rewardWallet port really is the bonus module's wallet.
const path = require('path');
const sqlite3 = require('sqlite3');
const request = require('supertest');
const { rule, bootApp, waitForSeed } = require('./_contract');

const app = bootApp('referral');
const bonus = require('../../bonus');
const referral = require('../../referral');
const bonusTime = require('../../bonus/time');

const GM = 'Bearer gm-token';
let seq = 0;
const n = () => ++seq;
// Each rule gets its own send currency from POOL; beforeEach archives prior POOL rules so tests never collide on DUPLICATE_CURRENCY
const POOL = ['EUR', 'CAD', 'AUD', 'CNY', 'INR', 'ZAR', 'KES', 'GHS', 'AED'];
const PAIRS = [];
for (let offset = 1; offset < POOL.length; offset++) {
    for (let i = 0; i < POOL.length; i++) {
        PAIRS.push([POOL[i], POOL[(i + offset) % POOL.length]]);
    }
}
let cseq = 0;
const corridor = () => PAIRS[cseq++ % PAIRS.length];

const setNow = (iso) => { referral.clock.now = () => new Date(iso); bonusTime.clock.now = () => new Date(iso); };
const realNow = () => { referral.clock.now = () => new Date(); bonusTime.clock.now = () => new Date(); };

beforeAll(async () => { await waitForSeed(app, request); });
beforeEach(async () => {
    setNow('2030-01-01T10:00:00Z');
    const existing = (await request(app).get('/api/referral-rules')).body.data || [];
    for (const r of existing.filter((x) => POOL.includes(x.base_currency))) {
        await request(app).delete(`/api/referral-rules/${r.id}`);
    }
});
afterEach(() => { realNow(); jest.restoreAllMocks(); });

const mkRule = async (over = {}) => {
    const [send, receive] = over.corridor || corridor();
    const body = {
        name: `Contract Rule ${n()}`, reward_type: 'BOTH', base_currency: send, receive_currency: receive, referrer_reward: 5, referee_reward: 10,
        min_transaction_threshold: 50, qualification_window_days: 30, bonus_validity_days: 90, ...over,
    };
    delete body.corridor;
    const res = await request(app).post('/api/referral-rules').send(body);
    if (res.status !== 201) throw new Error(`test setup: rule refused: ${JSON.stringify(res.body)}`);
    return { id: res.body.id, send: body.base_currency, receive: body.receive_currency, body };
};
const mkCustomer = async (send, over = {}) => {
    const i = n();
    const res = await request(app).post('/api/referral/customers').send({
        id: `RC-${Date.now().toString(36)}-${i}`, first_name: 'Olayinka', last_name: 'Adebayo', email: `rc${i}-${Date.now()}@example.com`, phone: `+44770${String(1000000 + i)}`,
        send_currency: send, kyc_status: 'PASSED', device_id: `dev-${Date.now()}-${i}`, ...over,
    });
    return res.body.data;
};
const register = (code, referee) => request(app).post('/api/referral/referrals').send({ code, referee });
// A full referral: rule, referrer, and a registered referee
const world = async (ruleOver = {}, { referee = {}, referrer = {} } = {}) => {
    const r = await mkRule(ruleOver);
    const a = await mkCustomer(r.send, referrer);
    const bId = `RB-${Date.now().toString(36)}-${n()}`;
    const reg = await register(a.referral_code, {
        id: bId, first_name: 'Sarah', last_name: 'Smith', email: `${bId}@example.com`, phone: `+44771${String(1000000 + seq)}`,
        send_currency: r.send, receive_currency: r.receive, kyc_status: 'PASSED', device_id: `dev-${bId}`, ...referee,
    });
    return { rule: r, referrer: a, refereeId: bId, referral: reg.body.data, code: a.referral_code };
};
let tseq = 0;
const send = (w, status, over = {}) => request(app).post('/api/referral/transfer-events').send({
    transfer_id: `RT-${Date.now().toString(36)}-${tseq}`, customer_id: w.refereeId, amount: w.rule.body.min_transaction_threshold + 10, currency: w.rule.send,
    receive_currency: w.rule.receive, status, created_at: referral.clock.now().toISOString(), ...over,
});
// qualify (PAID) then complete one transfer
const qualifyAndComplete = async (w, over = {}) => {
    const id = over.transfer_id || `RT-${Date.now().toString(36)}-${tseq++}`;
    await send(w, 'PAID', { ...over, transfer_id: id });
    return (await send(w, 'COMPLETED', { ...over, transfer_id: id })).body.referral;
};
const available = async (id, currency) => {
    const b = (await request(app).get(`/api/wallet/${id}?currency=${currency}`)).body.balances[0];
    return b ? b.available : 0;
};
const walletOf = async (id, currency) => (await request(app).get(`/api/wallet/${id}?currency=${currency}`)).body;
const dbRun = (sql, params = []) => new Promise((resolve, reject) => {
    const db = new sqlite3.Database(path.join(process.cwd(), 'database.sqlite'));
    db.run(sql, params, (err) => { db.close(); return err ? reject(err) : resolve(); });
});
const tracked = async (qs) => (await request(app).get(`/api/referral/tracking?${qs}`)).body;

const FILE = 'server/referral.js';

describe('Rules: which rule applies and who it pays', () => {
    rule('REFERRAL-01', 'The rule is matched by send currency and receive currency; one non-archived rule per send currency is enforced', {
        why: 'Programmes are funded per send currency (with optional receive currency restriction). Only one non-archived rule per send currency is allowed (DUPLICATE_CURRENCY, 409) and rule names must be unique (DUPLICATE_NAME, 409).',
        fix: 'checkDuplicates() enforces one non-archived rule per base_currency (DUPLICATE_CURRENCY, 409) and unique name (DUPLICATE_NAME, 409); liveRuleFor() picks the live rule whose receive_currency equals the referee\'s, otherwise the live rule with no receive_currency.',
        where: [FILE],
    })(async () => {
        const [send, a] = corridor();
        const b = POOL.find((c) => c !== send && c !== a);
        const specific = await mkRule({ corridor: [send, a], referrer_reward: 7, referee_reward: 8 });
        const dup = await request(app).post('/api/referral-rules').send({ ...specific.body, name: `Dup ${n()}` });
        expect(dup.status).toBe(409);
        expect(dup.body.error).toBe('DUPLICATE_CURRENCY');
        const dupAny = await request(app).post('/api/referral-rules').send({ ...specific.body, receive_currency: '', name: `Dup Any ${n()}` });
        expect(dupAny.status).toBe(409);
        expect(dupAny.body.error).toBe('DUPLICATE_CURRENCY');
        const dupName = await request(app).post('/api/referral-rules').send({ ...specific.body, base_currency: b, receive_currency: a, name: specific.body.name });
        expect(dupName.status).toBe(409);
        expect(dupName.body.error).toBe('DUPLICATE_NAME');

        const referrer = await mkCustomer(send);
        const regTo = async (receive) => (await register(referrer.referral_code, {
            id: `RM-${n()}-${Date.now()}`, first_name: 'Mo', email: `m${seq}-${Date.now()}@example.com`, phone: `+44772${String(1000000 + seq)}`, send_currency: send, receive_currency: receive, kyc_status: 'PASSED', device_id: `d${seq}-${Date.now()}`,
        })).body.data;
        expect((await regTo(a)).rule_id).toBe(specific.id);
        expect((await regTo(b)).rule_id).toBeNull();

        await request(app).delete(`/api/referral-rules/${specific.id}`).expect(200);
        const any = await mkRule({ corridor: [send, ''], name: `Any dest ${n()}`, referrer_reward: 1, referee_reward: 2 });
        expect((await regTo(a)).rule_id).toBe(any.id);
        expect((await regTo(b)).rule_id).toBe(any.id);
    });

    rule('REFERRAL-02', 'A referee whose send currency has no live rule is NOT_ELIGIBLE and never paid', {
        why: 'No programme means no budget. The referral is kept for reporting but nothing may be credited, even after a big completed transfer.',
        fix: 'createReferral() sets status NOT_ELIGIBLE with reason "No active referral programme for <corridor>" when no live rule matches; only REGISTERED referrals can become PENDING.',
        where: [FILE],
    })(async () => {
        const w = await world({}, { referee: { send_currency: 'JPY', receive_currency: 'KES' } });
        expect(w.referral).toMatchObject({ status: 'NOT_ELIGIBLE', rule_id: null });
        expect(w.referral.status_reason).toMatch(/No active referral programme/);
        const after = await qualifyAndComplete({ ...w, rule: { ...w.rule, send: 'JPY', receive: 'KES', body: { min_transaction_threshold: 50 } } });
        expect(after.status).toBe('NOT_ELIGIBLE');
        expect(await available(w.refereeId, 'JPY')).toBe(0);
    });

    rule('REFERRAL-03', 'reward_type decides who is paid: REFERRER pays only the referrer, REFEREE only the referee, BOTH pays both', {
        why: 'Marketing chooses who gets the incentive per corridor. Paying the other party burns budget the rule never allowed (regression: referee was credited on REFERRER rules).',
        fix: 'validateRule() zeroes the unrewarded side; tryAward() sets rewardsReferrer = reward_type !== "REFEREE" and rewardsReferee = reward_type !== "REFERRER" and only calls addCredit for those.',
        where: [FILE],
    })(async () => {
        const cases = [['REFERRER', 5, 0], ['REFEREE', 0, 10], ['BOTH', 5, 10]];
        for (const [type, wantReferrer, wantReferee] of cases) {
            const w = await world({ reward_type: type, referrer_reward: type === 'REFEREE' ? 0 : 5, referee_reward: type === 'REFERRER' ? 0 : 10 });
            const done = await qualifyAndComplete(w);
            expect(done.status).toBe('REWARDED');
            expect(done.referrer_credited).toBe(wantReferrer);
            expect(done.referee_credited).toBe(wantReferee);
            expect(await available(w.referrer.id, w.rule.send)).toBe(wantReferrer);
            expect(await available(w.refereeId, w.rule.send)).toBe(wantReferee);
        }
        // The type itself is checked at award time, not only the amounts: a REFERRER snapshot that somehow carries a referee amount still pays no referee
        const w = await world({ reward_type: 'REFERRER', referee_reward: 0 });
        await dbRun('UPDATE referrals SET referee_reward = 10 WHERE id = ?', [w.referral.id]);
        const done = await qualifyAndComplete(w);
        expect(done).toMatchObject({ status: 'REWARDED', referrer_credited: 5, referee_credited: 0 });
        expect(await available(w.refereeId, w.rule.send)).toBe(0);
        const w2 = await world({ reward_type: 'REFEREE', referrer_reward: 0 });
        await dbRun('UPDATE referrals SET referrer_reward = 5 WHERE id = ?', [w2.referral.id]);
        expect(await qualifyAndComplete(w2)).toMatchObject({ referrer_credited: 0, referee_credited: 10 });
        expect(await available(w2.referrer.id, w2.rule.send)).toBe(0);
    });

    rule('REFERRAL-04', 'A rule form cannot save an inconsistent reward: the paid side needs an amount > 0, the unpaid side is stored as 0', {
        why: 'An unrewarded party must never carry a non-zero amount, and a rewarded party with no amount would create empty promises.',
        fix: 'validateRule(): referrer_reward is required unless reward_type is REFEREE; referee_reward unless REFERRER; clean values zero the unpaid side. Unknown reward_type is refused.',
        where: [FILE],
    })(async () => {
        const [s, r] = corridor();
        const base = { name: `Form ${n()}`, base_currency: s, receive_currency: r, min_transaction_threshold: 50 };
        const noReferee = await request(app).post('/api/referral-rules').send({ ...base, reward_type: 'REFEREE', referee_reward: 0 });
        expect(noReferee.status).toBe(400);
        expect(noReferee.body.fields).toHaveProperty('referee_reward');
        const noReferrer = await request(app).post('/api/referral-rules').send({ ...base, name: `Form ${n()}`, reward_type: 'REFERRER', referrer_reward: '' });
        expect(noReferrer.status).toBe(400);
        expect(noReferrer.body.fields).toHaveProperty('referrer_reward');
        expect((await request(app).post('/api/referral-rules').send({ ...base, name: `Form ${n()}`, reward_type: 'EVERYONE', referrer_reward: 1, referee_reward: 1 })).body.fields).toHaveProperty('reward_type');
        const ok = await request(app).post('/api/referral-rules').send({ ...base, name: `Form ${n()}`, reward_type: 'REFEREE', referrer_reward: 99, referee_reward: 10 });
        expect(ok.status).toBe(201);
        expect(ok.body.data).toMatchObject({ reward_type: 'REFEREE', referrer_reward: 0, referee_reward: 10 });
        const same = await request(app).post('/api/referral-rules').send({ ...base, name: `Form ${n()}`, receive_currency: s, reward_type: 'BOTH', referrer_reward: 1, referee_reward: 1 });
        expect(same.body.fields).toHaveProperty('receive_currency'); // corridor needs two different currencies
    });

    rule('REFERRAL-05', 'Rules are archived, never deleted, and an archived rule is read-only', {
        why: 'Referrals, credits and reports point at rules; deleting one would orphan history (AC-1.5.1).',
        fix: 'DELETE and POST /archive set is_archived = 1, is_enabled = 0 and keep the row; archived rules are hidden from the default list, cannot be edited (ARCHIVED, 400) and free their corridor for a new rule.',
        where: [FILE],
    })(async () => {
        const r = await mkRule();
        await request(app).delete(`/api/referral-rules/${r.id}`).expect(200);
        const all = (await request(app).get('/api/referral-rules?include_archived=1')).body.data;
        expect(all.find((x) => x.id === r.id)).toMatchObject({ is_archived: 1, status: 'ARCHIVED' });
        expect((await request(app).get('/api/referral-rules')).body.data.find((x) => x.id === r.id)).toBeUndefined();
        const edit = await request(app).put(`/api/referral-rules/${r.id}`).send({ ...r.body, referrer_reward: 9 });
        expect(edit.status).toBe(400);
        expect(edit.body.error).toBe('ARCHIVED');
        await mkRule({ corridor: [r.send, r.receive] }); // the corridor is free again
    });
});

describe('Registration, qualification and reward', () => {
    rule('REFERRAL-06', 'A referral is registered when the referee signs up with a code (email verification), once, and a link visit alone registers nothing', {
        why: 'Registration is the moment the referee is bound to the referrer and the rule is chosen. Opening the link is only a visit statistic.',
        fix: 'POST /api/referral/referrals -> createReferral() inserts status REGISTERED, idempotent per referee_id (returns the existing row). POST /api/referral/visits only writes referral_link_visits, once per visitor per 24h.',
        where: [FILE],
    })(async () => {
        const r = await mkRule();
        const a = await mkCustomer(r.send);
        const visit = await request(app).post('/api/referral/visits').send({ code: a.referral_code, visitor_id: 'v-1' });
        expect(visit.body.counted).toBe(true);
        expect((await request(app).post('/api/referral/visits').send({ code: a.referral_code, visitor_id: 'v-1' })).body.counted).toBe(false);
        expect((await tracked(`rule_id=${r.id}`)).total).toBe(0);

        const referee = { id: `RR-${n()}-${Date.now()}`, first_name: 'Ngozi', email: `n${seq}-${Date.now()}@example.com`, phone: `+44773${String(1000000 + seq)}`, send_currency: r.send, receive_currency: r.receive, kyc_status: 'PASSED', device_id: `d-${seq}-${Date.now()}` };
        const first = await register(a.referral_code, referee);
        expect(first.status).toBe(201);
        expect(first.body.data).toMatchObject({ status: 'REGISTERED', referrer_id: a.id, referee_id: referee.id, rule_id: r.id });
        const second = await register(a.referral_code, referee);
        expect(second.body.data.id).toBe(first.body.data.id);
        expect((await tracked(`rule_id=${r.id}`)).total).toBe(1);
        expect((await request(app).get('/api/referral/codes/AB-12')).status).toBe(400);
        expect((await request(app).get('/api/referral/codes/ZZZZZZZZ')).status).toBe(404);
    });

    rule('REFERRAL-07', 'The reward terms are snapshotted on the referral row at registration; later rule edits do not change what this referral pays', {
        why: 'The customer was promised these terms when they joined. Changing the rule must only affect people who join afterwards.',
        fix: 'createReferral() copies reward_type, referrer_reward, referee_reward, floor, qualification_window_days, bonus_validity_days and the deadlines onto the referrals row; tryAward() pays r.referrer_reward / r.referee_reward, not the rule.',
        where: [FILE],
    })(async () => {
        const w = await world({ referrer_reward: 5, referee_reward: 10, min_transaction_threshold: 50 });
        expect(w.referral).toMatchObject({ referrer_reward: 5, referee_reward: 10, floor: 50, qualification_window_days: 30, bonus_validity_days: 90, reward_type: 'BOTH' });
        await request(app).put(`/api/referral-rules/${w.rule.id}`).send({ ...w.rule.body, referrer_reward: 50, referee_reward: 100, min_transaction_threshold: 500 }).expect(200);
        const done = await qualifyAndComplete(w, { amount: 60 }); // would not even meet the new floor
        expect(done).toMatchObject({ status: 'REWARDED', referrer_credited: 5, referee_credited: 10 });
        expect(await available(w.referrer.id, w.rule.send)).toBe(5);
        expect(await available(w.refereeId, w.rule.send)).toBe(10);
    });

    rule('REFERRAL-08', 'Qualification floor: a transfer qualifies only at or above the floor, in the rule\'s send and receive currency', {
        why: 'The floor is the minimum real activity the programme pays for (inclusive). Transfers on other corridors say nothing about this programme.',
        fix: 'handleTransferEvent() requires transfer.currency === referral.currency, matching receive_currency when the rule names one, and amount >= referral.floor before moving REGISTERED -> PENDING.',
        where: [FILE],
    })(async () => {
        const w = await world({ min_transaction_threshold: 50 });
        const other = POOL.find((c) => c !== w.rule.send && c !== w.rule.receive);
        expect((await send(w, 'PAID', { amount: 49.99 })).body.referral.status).toBe('REGISTERED');
        expect((await send(w, 'PAID', { amount: 500, currency: other })).body.referral.status).toBe('REGISTERED');
        expect((await send(w, 'PAID', { amount: 500, receive_currency: other })).body.referral.status).toBe('REGISTERED');
        const ok = await send(w, 'PAID', { amount: 50 });
        expect(ok.body.referral).toMatchObject({ status: 'PENDING', qualifying_amount: 50 });
    });

    rule('REFERRAL-09', 'Qualification window: the referrer is only paid inside the window, the referee until bonus validity ends, and unqualified referrals expire', {
        why: 'The window limits the referrer\'s cost; the longer validity lets a slow referee still earn their bonus. After the overall deadline nobody is paid.',
        fix: 'referrerDeadlineFor() = registration + qualification_window_days; overallDeadlineFor() = + max(window, bonus_validity) (window only for REFERRER rules). tryAward() withholds the referrer reward after the referrer deadline; runJobs() expires REGISTERED referrals past qualification_deadline.',
        where: [FILE],
    })(async () => {
        const both = await world({ qualification_window_days: 10, bonus_validity_days: 60 });
        expect(both.referral.referrer_deadline).toBe('2030-03-02');
        expect(both.referral.qualification_deadline).toBe('2030-03-02');
        setNow('2030-01-21T10:00:00Z'); // day 20: inside the 60-day Bonus Validity window
        const within = await qualifyAndComplete(both);
        expect(within).toMatchObject({ status: 'REWARDED', referrer_credited: 5, referee_credited: 10 });

        setNow('2030-01-01T10:00:00Z');
        const only = await world({ reward_type: 'REFERRER', referee_reward: 0, bonus_validity_days: 10 });
        expect(only.referral.qualification_deadline).toBe('2030-01-11');
        setNow('2030-01-21T10:00:00Z');
        expect((await send(only, 'PAID')).body.referral.status).toBe('REGISTERED'); // too late
        await request(app).post('/api/referral/run-jobs').expect(200);
        const expired = (await tracked(`rule_id=${only.rule.id}`)).data[0];
        expect(expired.status).toBe('EXPIRED');
        expect(expired.status_reason).toMatch(/Qualification window ended on 11\/01\/2030/);

        setNow('2030-01-01T10:00:00Z');
        const beyond = await world({ qualification_window_days: 10, bonus_validity_days: 20 });
        setNow('2030-02-15T10:00:00Z'); // past both
        expect((await send(beyond, 'PAID')).body.referral.status).toBe('REGISTERED');
    });

    rule('REFERRAL-10', 'Referee welcome credit is issued at registration (never auto-deducted without choice) while referrer credit is issued only when the first qualifying transfer reaches COMPLETED', {
        why: 'Per product owner approval (2026-11-16), the referee receives their welcome bonus credit immediately at registration and may choose to redeem it on their first valid qualifying transfer (REFERRAL-29), but it is never auto-deducted without customer choice; the referrer credit is issued only when that first qualifying transfer reaches COMPLETED.',
        fix: 'createReferral() issues the referee welcome credit at registration; PAID moves the referral to PENDING; on COMPLETED tryAward() issues the referrer credit via ports.rewardWallet.issueCredit({ creditSource: "REFERRAL", creditSourceDetail: "REFERRER", referenceId: "<referral>:referrer" }) without auto-creating any promo redemption or APPLIED row.',
        where: [FILE, 'server/referralHost.js', 'server/bonus/wallet.js'],
    })(async () => {
        const w = await world();
        const id = `RT-first-${n()}`;
        const paid = await send(w, 'PAID', { transfer_id: id });
        expect(paid.body.referral.status).toBe('PENDING');
        expect(await available(w.referrer.id, w.rule.send)).toBe(0);
        expect(await available(w.refereeId, w.rule.send)).toBe(10); // referee credited at registration
        const done = (await send(w, 'COMPLETED', { transfer_id: id })).body.referral;
        expect(done).toMatchObject({ status: 'REWARDED', qualifying_transfer_id: id });
        expect(done.referrer_credit_id).toBeTruthy();
        expect(done.referee_credit_id).toBeTruthy();

        const refW = await walletOf(w.refereeId, w.rule.send);
        expect(refW.credits[0]).toMatchObject({ credit_source: 'REFERRAL', credit_source_detail: 'REFEREE', amount: 10, remaining: 10 });
        expect((await walletOf(w.referrer.id, w.rule.send)).credits[0]).toMatchObject({ credit_source: 'REFERRAL', credit_source_detail: 'REFERRER', amount: 5 });
        expect(refW.history.filter((h) => h.type === 'APPLIED')).toEqual([]); // nothing was taken off the qualifying transfer
        const promo = (await request(app).get(`/api/promocodes/customers/${w.refereeId}/redemptions`)).body;
        expect(promo.data).toEqual([]);
    });

    rule('REFERRAL-11', 'Only the first qualifying transfer pays; repeated events and later transfers never pay again', {
        why: 'The programme is a one-off acquisition reward per referral.',
        fix: 'tryAward() only acts on status PENDING and sets REWARDED; addCredit() is idempotent on referenceId "<referral id>:referrer|referee"; later transfers find a REWARDED referral and do nothing.',
        where: [FILE],
    })(async () => {
        const w = await world();
        const id = `RT-one-${n()}`;
        await send(w, 'PAID', { transfer_id: id });
        await send(w, 'COMPLETED', { transfer_id: id });
        await send(w, 'COMPLETED', { transfer_id: id }); // Rhemito repeats the event
        const second = await qualifyAndComplete(w, { amount: 500 });
        expect(second.status).toBe('REWARDED');
        expect(await available(w.referrer.id, w.rule.send)).toBe(5);
        expect(await available(w.refereeId, w.rule.send)).toBe(10);
        expect(second.qualifying_transfer_id).toBe(id);
    });

    rule('REFERRAL-12', 'A failing transfer before the reward returns the referral to REGISTERED so a later transfer can still qualify', {
        why: 'A cancelled payment is not a qualifying transfer; the referee should get another chance within the window.',
        fix: 'handleTransferEvent(): CANCELLED/FAILED on the qualifying transfer of a PENDING referral clears qualifying_transfer_id and sets status back to REGISTERED.',
        where: [FILE],
    })(async () => {
        const w = await world();
        const id = `RT-fail-${n()}`;
        expect((await send(w, 'PAID', { transfer_id: id })).body.referral.status).toBe('PENDING');
        const back = (await send(w, 'CANCELLED', { transfer_id: id })).body.referral;
        expect(back).toMatchObject({ status: 'REGISTERED', qualifying_transfer_id: null });
        expect((await qualifyAndComplete(w)).status).toBe('REWARDED');
    });

    rule('REFERRAL-13', 'Referee receives welcome bonus immediately on sign-up regardless of KYC status, while referrer reward waits for referrer KYC to pass', {
        why: 'Approved by Product Owner Mohammad (2026-11-16): the referee gets their welcome bonus as soon as they sign up (Rhemito already gates Send Money payment behind KYC), whereas the referrer must have passed KYC before receiving their referrer reward.',
        fix: 'createReferral() issues the referee credit immediately upon REGISTERED without waiting for referee KYC; tryAward() holds the completed referral in PENDING with status_reason "Awaiting referrer KYC" when the referrer\'s kyc_status !== "PASSED", and upsertCustomer() with kyc_status "PASSED" releases it without double-crediting the referee.',
        where: [FILE],
    })(async () => {
        const w = await world({}, { referrer: { kyc_status: 'PENDING' }, referee: { kyc_status: 'PENDING' } });
        expect(await available(w.refereeId, w.rule.send)).toBe(10);
        expect(await available(w.referrer.id, w.rule.send)).toBe(0);
        const waiting = await qualifyAndComplete(w);
        expect(waiting).toMatchObject({ status: 'PENDING', status_reason: 'Awaiting referrer KYC' });
        expect(await available(w.referrer.id, w.rule.send)).toBe(0);
        expect(await available(w.refereeId, w.rule.send)).toBe(10);
        await request(app).post('/api/referral/customers').send({ id: w.referrer.id, kyc_status: 'PASSED' }).expect(200);
        const t = (await tracked(`rule_id=${w.rule.id}`)).data[0];
        expect(t).toMatchObject({ status: 'REWARDED', referrer_credited: 5, referee_credited: 10 });
        expect(await available(w.referrer.id, w.rule.send)).toBe(5);
        expect(await available(w.refereeId, w.rule.send)).toBe(10);
    });

    rule('REFERRAL-14', 'The referrer cap stops further referrer rewards but the referee is still paid', {
        why: 'The cap protects budget per referrer; it must not punish the new customer who did nothing wrong.',
        fix: 'tryAward() counts rewarded referrals for (referrer, rule) against rule.max_referrals_per_referrer; at the cap it only adds the note "Referrer cap reached" and still credits the referee. GET /api/referral/offer reports cap_reached.',
        where: [FILE],
    })(async () => {
        const w = await world({ max_referrals_per_referrer: 1 });
        await qualifyAndComplete(w);
        const c = `RC2-${n()}-${Date.now()}`;
        await register(w.code, { id: c, first_name: 'Mike', email: `${c}@example.com`, phone: `+44774${String(1000000 + seq)}`, send_currency: w.rule.send, receive_currency: w.rule.receive, kyc_status: 'PASSED', device_id: `d-${c}` });
        const second = await qualifyAndComplete({ ...w, refereeId: c });
        expect(second).toMatchObject({ status: 'REWARDED', referrer_credited: 0, referee_credited: 10, status_reason: 'Referrer cap reached' });
        expect(await available(w.referrer.id, w.rule.send)).toBe(5);
        const offer = (await request(app).get(`/api/referral/offer?customer_id=${w.referrer.id}&currency=${w.rule.send}&receive_currency=${w.rule.receive}`)).body.offer;
        expect(offer.cap_reached).toBe(true);
    });
});

describe('NOT_ELIGIBLE referrals', () => {
    rule('REFERRAL-15', 'Self-referral is NOT_ELIGIBLE by same phone number, device, email, or a code that belongs to the referee', {
        why: 'The classic abuse: sign up a second account and refer yourself. Identity signals are compared across the two accounts, not just the account id.',
        fix: 'sameIdentity() compares email (case-insensitive), phone digits only, device_id and payment fingerprints; createReferral() records NOT_ELIGIBLE with "Self-referral: same <signal>". Using one\'s own code is a 400 OWN_LINK.',
        where: [FILE],
    })(async () => {
        const phoneW = await world({}, { referrer: { phone: '+44 7700 900-111' }, referee: { phone: '+447700900111' } });
        expect(phoneW.referral).toMatchObject({ status: 'NOT_ELIGIBLE', status_reason: 'Self-referral: same phone number' });
        const deviceW = await world({}, { referrer: { device_id: 'shared-device-1' }, referee: { device_id: 'shared-device-1' } });
        expect(deviceW.referral).toMatchObject({ status: 'NOT_ELIGIBLE', status_reason: 'Self-referral: same device' });
        const emailW = await world({}, { referrer: { email: 'Same.Person@Example.com' }, referee: { email: 'same.person@example.com' } });
        expect(emailW.referral).toMatchObject({ status: 'NOT_ELIGIBLE', status_reason: 'Self-referral: same email' });

        // not eligible means not paid, however the referee behaves afterwards
        const after = await qualifyAndComplete(phoneW);
        expect(after.status).toBe('NOT_ELIGIBLE');
        expect(await available(phoneW.referrer.id, phoneW.rule.send)).toBe(0);
        expect(await available(phoneW.refereeId, phoneW.rule.send)).toBe(0);

        const r = await mkRule();
        const me = await mkCustomer(r.send);
        const own = await register(me.referral_code, { id: me.id, send_currency: r.send });
        expect(own.status).toBe(400);
        expect(own.body.error).toBe('OWN_LINK');
    });

    rule('REFERRAL-16', 'A returning customer (same email or phone as a closed account) is NOT_ELIGIBLE', {
        why: 'The programme rewards new customers only; closing an account and re-registering must not earn a welcome reward.',
        fix: 'createReferral() looks for a CLOSED customer with the same email or phone and records NOT_ELIGIBLE with reason "Returning customer".',
        where: [FILE],
    })(async () => {
        const r = await mkRule();
        const closed = await mkCustomer(r.send, { account_status: 'CLOSED', email: `returning-${Date.now()}@example.com` });
        const a = await mkCustomer(r.send);
        const reg = await register(a.referral_code, {
            id: `RRET-${n()}-${Date.now()}`, first_name: 'Back', email: closed.email, phone: `+44775${String(1000000 + seq)}`, send_currency: r.send, receive_currency: r.receive, kyc_status: 'PASSED', device_id: `d-${seq}-${Date.now()}`,
        });
        expect(reg.body.data).toMatchObject({ status: 'NOT_ELIGIBLE', status_reason: 'Returning customer' });
    });

    rule('REFERRAL-17', 'Only a Growth Manager can approve a NOT_ELIGIBLE referral; approval pays through the wallet according to the snapshot', {
        why: 'Overriding the abuse checks is a money decision. It needs an accountable named approver and a written reason.',
        fix: 'POST /api/referral/referrals/:id/approve uses requireRole(GROWTH_MANAGER) (name from the token), needs a 10-250 char reason, only accepts NOT_ELIGIBLE, and issues credits via addCredit with the approver in the note.',
        where: [FILE, 'server/auth.js'],
    })(async () => {
        const w = await world({ reward_type: 'REFERRER', referee_reward: 0 }, { referrer: { phone: '+44 7700 900-222' }, referee: { phone: '+447700900222' } });
        expect(w.referral.status).toBe('NOT_ELIGIBLE');
        const url = `/api/referral/referrals/${w.referral.id}/approve`;
        expect((await request(app).post(url).send({ reason: 'Verified by phone with the customer' })).status).toBe(401);
        expect((await request(app).post(url).set('Authorization', GM).send({ reason: 'short' })).status).toBe(400);
        const ok = await request(app).post(url).set('Authorization', GM).send({ reason: 'Verified by phone with the customer' });
        expect(ok.status).toBe(200);
        expect(ok.body.data).toMatchObject({ status: 'REWARDED', approved_by: 'Grace Growth', referrer_credited: 5, referee_credited: 0 });
        expect(await available(w.referrer.id, w.rule.send)).toBe(5);
        expect(await available(w.refereeId, w.rule.send)).toBe(0); // REFERRER rule: the referee is not paid on approval either
        expect((await request(app).post(url).set('Authorization', GM).send({ reason: 'Verified by phone with the customer' })).status).toBe(400); // already rewarded
    });

    rule('REFERRAL-18', 'A customer blocked from earning bonus is not paid; the referral becomes NOT_ELIGIBLE with the reason', {
        why: 'The abuse guard (Bonus Blocks) covers referral rewards too, otherwise referrals are a way around the block.',
        fix: 'tryAward() asks ports.eligibilityGuard.isEarningBlocked (host: bonus.isEarningBlocked) for each paid party and records "Bonus blocked for ..." as NOT_ELIGIBLE; a Growth Manager can then approve.',
        where: [FILE, 'server/referralHost.js', 'server/bonus/blocks.js'],
    })(async () => {
        const w = await world();
        for (const k of [1, 2, 3]) await bonus.recordStrike({ customerId: w.refereeId, eventId: `BLK-${w.refereeId}-${k}`, kind: 'SCHEME', outcome: 'REFUNDED', amountLost: 1, currency: w.rule.send });
        const done = await qualifyAndComplete(w);
        expect(done.status).toBe('NOT_ELIGIBLE');
        expect(done.status_reason).toMatch(/Bonus blocked for .*referee/);
        expect(await available(w.refereeId, w.rule.send)).toBe(0);
        expect(await available(w.referrer.id, w.rule.send)).toBe(0);
    });
});

describe('Idempotent issue, retry and reversal', () => {
    rule('REFERRAL-19', 'If the wallet cannot issue a reward the referral stays PENDING, the daily job retries it, and nobody is paid twice', {
        why: 'A wallet outage must never lose a promised reward or double it when the retry runs (BR-51).',
        fix: 'tryAward() catches issueCredit errors, keeps status PENDING with reason "Reward could not be issued – will retry"; runJobs() calls tryAward() again for those rows. issueCredit is idempotent on referenceId, so the referrer credit issued before the failure is not repeated.',
        where: [FILE, 'server/referralHost.js'],
    })(async () => {
        const w = await world();
        const real = bonus.issueCredit;
        let failing = true;
        jest.spyOn(bonus, 'issueCredit').mockImplementation((input) => {
            if (failing && input.customerId === w.referrer.id) return Promise.reject(new Error('wallet down'));
            return real(input);
        });
        const first = await qualifyAndComplete(w);
        expect(first).toMatchObject({ status: 'PENDING', status_reason: 'Reward could not be issued – will retry' });
        failing = false;
        const jobs = await request(app).post('/api/referral/run-jobs');
        expect(jobs.body.rewards_retried).toBeGreaterThanOrEqual(1);
        expect((await tracked(`rule_id=${w.rule.id}`)).data[0].status).toBe('REWARDED');
        expect(await available(w.referrer.id, w.rule.send)).toBe(5);
        expect(await available(w.refereeId, w.rule.send)).toBe(10);
        await request(app).post('/api/referral/run-jobs').expect(200);
        expect(await available(w.referrer.id, w.rule.send)).toBe(5);
    });

    rule('REFERRAL-20', 'A refunded qualifying transfer reverses the reward: unused credit is voided, spent credit becomes debt, the referee takes the strike', {
        why: 'The reward was for a transfer that did not stand. Mito cannot recover what was spent, so it is owed against future bonus; the abuse strike goes to the customer who made the transfer, not the referrer.',
        fix: 'handleTransferEvent() -> voidReferralCredits(): rewardWallet.voidCredit for both credits, onSpentCreditReversed -> bonus.clawbackCredit for the spent part, then eligibilityGuard.recordStrike for the referee. Referral status REVERSED.',
        where: [FILE, 'server/referralHost.js'],
    })(async () => {
        const w = await world();
        const id = `RT-rev-${n()}`;
        await qualifyAndComplete(w, { transfer_id: id });
        await request(app).post(`/api/wallet/${w.referrer.id}/apply`).send({ amount: 3, currency: w.rule.send, transfer_id: `SPEND-${id}`, send_amount: 100 }).expect(200);
        const rev = await send(w, 'REFUNDED', { transfer_id: id });
        expect(rev.body.referral.status).toBe('REVERSED');
        const b = (await walletOf(w.referrer.id, w.rule.send)).balances[0];
        expect(b).toMatchObject({ available: 0, outstanding_debt: 3 });
        expect(await available(w.refereeId, w.rule.send)).toBe(0);
        const strikes = (await request(app).get(`/api/bonus-blocks/${w.refereeId}`)).body.strikes;
        expect(strikes).toHaveLength(1); // 3 of the referrer's 5 was spent, so the loss counts against the referee
        expect(strikes[0]).toMatchObject({ kind: 'REFERRAL', event_id: id });
        expect((await request(app).get(`/api/bonus-blocks/${w.referrer.id}`)).body.strikes).toHaveLength(0); // never the referrer
    });
});

describe('Tracking, offers and performance endpoints', () => {
    rule('REFERRAL-21', 'Tracking filters by status, currency, receive currency and rule, summarises, and exports CSV', {
        why: 'Growth reconciles referral cost per corridor from this endpoint; filters must be exact and the summary must match the rows.',
        fix: 'tracking() builds WHERE from status, currency, receive_currency, rule_id, status_group, from/to and q; summary counts total/pending/rewarded and bonus_issued per currency. /tracking.csv exports the same rows.',
        where: [FILE],
    })(async () => {
        const w = await world();
        const other = await world({}, {});
        await qualifyAndComplete(w);
        const rows = await tracked(`rule_id=${w.rule.id}`);
        expect(rows.total).toBe(1);
        expect(rows.data[0]).toMatchObject({ rule_name: w.rule.body.name, status: 'REWARDED', currency: w.rule.send, receive_currency: w.rule.receive });
        expect(rows.summary).toMatchObject({ total: 1, rewarded: 1, pending: 0, conversion_rate: 100 });
        expect(rows.summary.bonus_issued[w.rule.send]).toBe(15);
        expect((await tracked(`rule_id=${other.rule.id}&status=REWARDED`)).total).toBe(0);
        expect((await tracked(`rule_id=${other.rule.id}&status=REGISTERED`)).total).toBe(1);
        expect((await tracked(`rule_id=${w.rule.id}&receive_currency=${other.rule.receive === w.rule.receive ? 'XXX' : other.rule.receive}`)).total).toBe(0);
        const csv = await request(app).get(`/api/referral/tracking.csv?rule_id=${w.rule.id}`);
        expect(csv.headers['content-type']).toMatch(/text\/csv/);
        expect(csv.text.split('\n')[0]).toMatch(/^Referral ID,(Tracking ID,Channel,Attribution Method,)?Referrer ID,Referrer,Referee ID,Referee,Rule/);
        expect(csv.text.split('\n')).toHaveLength(2);
    });

    rule('REFERRAL-22', 'My referrals masks the friend\'s name and needs a referrer_id; the offer is built from the live rule and hidden when none exists', {
        why: 'Customers must not see each other\'s full names, and an offer without a live rule would promise money that is not funded.',
        fix: 'GET /api/referral/referrals returns maskName() ("Sarah S."), 400 without referrer_id; GET /api/referral/offer returns offer/offers from live rules, reason NO_ACTIVE_RULE otherwise, referral_link from the customer\'s code.',
        where: [FILE],
    })(async () => {
        const w = await world();
        await qualifyAndComplete(w);
        const mine = (await request(app).get(`/api/referral/referrals?referrer_id=${w.referrer.id}`)).body;
        expect(mine.data[0]).toMatchObject({ friend: 'Sarah S.', status: 'REWARDED', status_label: 'Earned', credited: 5 });
        expect(JSON.stringify(mine)).not.toMatch(/Smith/);
        expect(mine.summary.total_earned[w.rule.send]).toBe(5);
        expect((await request(app).get('/api/referral/referrals')).status).toBe(400);

        const offer = (await request(app).get(`/api/referral/offer?customer_id=${w.referrer.id}&currency=${w.rule.send}&receive_currency=${w.rule.receive}`)).body;
        expect(offer.offer).toMatchObject({ rule_id: w.rule.id, reward_type: 'BOTH', referrer_reward: 5, referee_reward: 10, floor: 50 });
        expect(offer.offer.referral_link).toBe(`https://rhemito.com/ref/${w.referrer.referral_code}`);
        await request(app).delete(`/api/referral-rules/${w.rule.id}`).expect(200);
        const gone = (await request(app).get(`/api/referral/offer?customer_id=${w.referrer.id}&currency=${w.rule.send}&receive_currency=${w.rule.receive}`)).body;
        expect(gone).toMatchObject({ offer: null, reason: 'NO_ACTIVE_RULE' });
    });

    rule('REFERRAL-23', 'Performance reports bonus figures from the wallet and counts registrations, rewards and conversion per rule', {
        why: 'Cost reporting must reflect what the wallet really issued (BR-53); the referral module keeps no private copy of balances.',
        fix: 'performance() calls ports.rewardWallet.creditSummary({ referralIds }) for issued/used/returned/expired/voided and counts referrals by status per rule.',
        where: [FILE, 'server/referralHost.js'],
    })(async () => {
        const w = await world();
        await qualifyAndComplete(w);
        const perf = (await request(app).get('/api/referral/performance')).body.data.find((p) => p.rule_id === w.rule.id);
        expect(perf).toMatchObject({ registrations: 1, rewarded: 1, conversion_rate: 100, bonus_issued: 15, bonus_unused: 15 });
        const csv = await request(app).get('/api/referral/performance.csv');
        expect(csv.headers['content-type']).toMatch(/text\/csv/);
    });

    rule('REFERRAL-24', 'A rule-specific link (?rule_id=<id>) locks the referral and referee welcome bonus to the rule chosen by the referrer', {
        why: 'When multiple rules exist, the referrer selects which rule to share; the referee must receive that exact rule\'s welcome bonus.',
        fix: 'GET /api/referral/codes/:code?rule_id=<id> returns only that live rule\'s offer; createReferral() binds directly to rule_id and issues the referee credit in that rule\'s currency.',
        where: [FILE],
    })(async () => {
        const [s1, r1] = corridor();
        const [s2, r2] = corridor();
        const [s3, r3] = corridor();
        const rule1 = await mkRule({ corridor: [s1, r1], referrer_reward: 5, referee_reward: 10 });
        const rule2 = await mkRule({ corridor: [s2, r2], referrer_reward: 12, referee_reward: 25 });
        const rule3 = await mkRule({ corridor: [s3, r3], referrer_reward: 8, referee_reward: 15 });
        const referrer = await mkCustomer(s1);

        const codeLookup = await request(app).get(`/api/referral/codes/${referrer.referral_code}?rule_id=${rule2.id}`);
        expect(codeLookup.status).toBe(200);
        expect(codeLookup.body.offer).toMatchObject({ rule_id: rule2.id, currency: s2, receive_currency: r2, referee_reward: 25 });
        expect(codeLookup.body.offers).toHaveLength(1);

        const refereeId = `RB-LOCK-${Date.now().toString(36)}-${n()}`;
        const reg = await request(app).post('/api/referral/referrals').send({
            code: referrer.referral_code,
            rule_id: rule2.id,
            referee: { id: refereeId, first_name: 'Amir', last_name: 'Khan', email: `${refereeId}@example.com`, phone: `+44779${String(1000000 + seq)}`, send_currency: s1, kyc_status: 'PASSED', device_id: `dev-${refereeId}` },
        });
        expect(reg.status).toBe(201);
        expect(reg.body.data).toMatchObject({ status: 'REGISTERED', rule_id: rule2.id, currency: s2, receive_currency: r2, referee_credited: 25 });
        expect(await available(refereeId, s2)).toBe(25);
        expect(rule1.id).not.toBe(rule2.id);
        expect(rule3.id).not.toBe(rule2.id);
    });

    rule('REFERRAL-25', 'Currency-suffixed referral codes ({BASE}{CUR}, 6–16 chars) resolve the referrer and lock to the suffixed currency rule', {
        why: 'Multi-currency referrers share codes with a 3-letter currency suffix (e.g. OLAYINKA1234CAD, up to 16 chars) so the referee lands on and receives that currency\'s welcome bonus without needing a query param.',
        fix: 'lookupCode() accepts /^[A-Z0-9]{6,16}$/ and falls back to parseCurrencyCodeSuffix() to strip a trailing supported currency; GET /api/referral/codes/:code and createReferral() lock to look.currency.',
        where: [FILE],
    })(async () => {
        const [s1, r1] = corridor();
        const [s2, r2] = corridor();
        await mkRule({ corridor: [s1, r1], referrer_reward: 5, referee_reward: 10 });
        const rule2 = await mkRule({ corridor: [s2, r2], referrer_reward: 15, referee_reward: 30 });
        const referrer = await mkCustomer(s1);
        const suffixedCode = `${referrer.referral_code}${s2}`;
        expect(suffixedCode.length).toBeGreaterThanOrEqual(9);
        expect(suffixedCode.length).toBeLessThanOrEqual(16);

        const codeRes = await request(app).get(`/api/referral/codes/${suffixedCode}`);
        expect(codeRes.status).toBe(200);
        expect(codeRes.body).toMatchObject({
            valid: true,
            code: suffixedCode,
            currency: s2,
            rule_id: rule2.id,
            send_currencies: [s2],
        });
        expect(codeRes.body.offer).toMatchObject({ rule_id: rule2.id, currency: s2, referee_reward: 30 });

        const refereeId = `RB-SFX-${Date.now().toString(36)}-${n()}`;
        const reg = await register(suffixedCode, {
            id: refereeId, first_name: 'Zainab', last_name: 'Musa', email: `${refereeId}@example.com`, phone: `+44778${String(1000000 + seq)}`,
            send_currency: s1, kyc_status: 'PASSED', device_id: `dev-${refereeId}`,
        });
        expect(reg.status).toBe(201);
        expect(reg.body.data).toMatchObject({ status: 'REGISTERED', rule_id: rule2.id, currency: s2, receive_currency: r2, referee_credited: 30, code: suffixedCode });
        expect(await available(refereeId, s2)).toBe(30);

        // >16 chars is rejected as INVALID_FORMAT (400)
        expect((await request(app).get('/api/referral/codes/ABCDEFGHIJKLMNOPQ')).status).toBe(400);
    });

    rule('REFERRAL-26', 'Rule audit history (/api/referral-rules/:id/audit) records creation, field updates and archiving', {
        why: 'Compliance and Growth need an immutable change log of who created, edited or archived each referral rule and which fields changed.',
        fix: 'POST /api/referral-rules inserts field="created"; PUT /api/referral-rules/:id diffs RULE_FIELDS and inserts changed fields; DELETE/archive inserts field="archived"; GET /api/referral-rules/:id/audit returns rows ordered by id DESC.',
        where: [FILE],
    })(async () => {
        const r = await mkRule({ referrer_reward: 5, referee_reward: 10, min_transaction_threshold: 50 });
        await request(app).put(`/api/referral-rules/${r.id}`).send({
            ...r.body,
            referrer_reward: 12,
            min_transaction_threshold: 75,
            admin_user: 'Grace Growth',
        }).expect(200);
        await request(app).delete(`/api/referral-rules/${r.id}`).expect(200);

        const auditRes = await request(app).get(`/api/referral-rules/${r.id}/audit`);
        expect(auditRes.status).toBe(200);
        const rows = auditRes.body.data;
        expect(rows.length).toBeGreaterThanOrEqual(4);
        expect(rows[0]).toMatchObject({ rule_id: r.id, field: 'archived', old_value: '0', new_value: '1' });
        expect(rows.find((x) => x.field === 'referrer_reward')).toMatchObject({ old_value: '5', new_value: '12', admin_user: 'Grace Growth' });
        expect(rows.find((x) => x.field === 'min_transaction_threshold')).toMatchObject({ old_value: '50', new_value: '75', admin_user: 'Grace Growth' });
        expect(rows[rows.length - 1]).toMatchObject({ rule_id: r.id, field: 'created', old_value: null, new_value: r.body.name });
    });

    rule('REFERRAL-27', 'FURP admin compat shim wraps CSV exports in Result<{ csv, filename, content_type }> on Accept: application/json or ?format=json while preserving raw text/csv by default', {
        why: 'Mito Admin downloads CSV exports through authenticated fetch/verb-tunnelling expecting Result<{ csv, filename, content_type }>, while direct browser/curl callers without JSON opt-in expect raw text/csv.',
        fix: 'furpAdminCompat.js checks acceptsJson(req) or ?format=json (stripping format from downstream query) and wraps text/csv payloads in { succeeded: true, code: 200, data: { csv, filename, content_type } }.',
        where: ['server/furpAdminCompat.js', FILE],
    })(async () => {
        const w = await world();
        await qualifyAndComplete(w);
        const engineBase = '/api/v1/Affiliate/AdminSettings/Rewards/engine';

        // Default Accept preserves raw text/csv
        const rawCsv = await request(app).get(`${engineBase}/referral/tracking.csv?rule_id=${w.rule.id}`).set('Accept', 'text/csv');
        expect(rawCsv.status).toBe(200);
        expect(rawCsv.headers['content-type']).toMatch(/text\/csv/);
        expect(rawCsv.text).toMatch(/^Referral ID,/);

        // Opt-in via Accept: application/json returns Result<{ csv, filename, content_type }>
        const jsonAccept = await request(app).get(`${engineBase}/referral/tracking.csv?rule_id=${w.rule.id}`).set('Accept', 'application/json');
        expect(jsonAccept.status).toBe(200);
        expect(jsonAccept.headers['content-type']).toMatch(/application\/json/);
        expect(jsonAccept.body).toMatchObject({
            succeeded: true,
            code: 200,
            data: {
                filename: 'referral-tracking.csv',
                content_type: expect.stringMatching(/^text\/csv/i),
            },
        });
        expect(jsonAccept.body.data.csv).toMatch(/^Referral ID,/);

        // Opt-in via ?format=json (with Accept: */*) and via POST verb tunnel
        const jsonQuery = await request(app).get(`${engineBase}/referral/performance.csv?format=json`).set('Accept', '*/*');
        expect(jsonQuery.status).toBe(200);
        expect(jsonQuery.body).toMatchObject({
            succeeded: true,
            code: 200,
            data: {
                filename: 'referral-performance.csv',
                content_type: expect.stringMatching(/^text\/csv/i),
            },
        });
        expect(jsonQuery.body.data.csv).toMatch(/^Rule,Send Currency,/);

        const tunnelCsv = await request(app).post(engineBase).set('Accept', '*/*').send({
            method: 'GET',
            path: '/api/referral/tracking.csv',
            query: { rule_id: w.rule.id, format: 'json' },
        });
        expect(tunnelCsv.status).toBe(200);
        expect(tunnelCsv.body.succeeded).toBe(true);
        expect(tunnelCsv.body.data.filename).toBe('referral-tracking.csv');
        expect(tunnelCsv.body.data.csv.split('\n')).toHaveLength(2);
    });

    rule('REFERRAL-28', 'Recycled transfer_id across customers does not poison corridor or amount qualification for a new referee', {
        why: 'If the upstream app restarts an in-memory sequence and reuses TXN-202611-00001 for a different customer on a new corridor (e.g. GBP -> USD after an earlier customer sent GBP -> NGN), referral_transfers and bonus_transfers must update customer_id, amount, currency, receive_currency and created_at so the new referee qualifies and the referrer receives their reward.',
        fix: 'referral.js handleTransferEvent(), bonus/engine.js upsertTransfer() & triggerEvent(), and promo/engine.js handleTransferEvent() overwrite transfer fields when customer_id changes and scope scheme duplicate checks by user_id; tryAward() verifies transfer.customer_id === r.referee_id.',
        where: [FILE, 'server/bonus/engine.js', 'server/promo/engine.js'],
    })(async () => {
        // Archive any pre-existing GBP rules so we can test the exact GBP -> NGN then GBP -> USD scenario
        const existingGbp = ((await request(app).get('/api/referral-rules')).body.data || []).filter((x) => x.base_currency === 'GBP');
        for (const r of existingGbp) {
            await request(app).delete(`/api/referral-rules/${r.id}`).expect(200);
        }

        const recycledTxnId = 'TXN-202611-00001';

        // Step 1: Referee 1 registers under GBP -> NGN and completes TXN-202611-00001 on GBP -> NGN
        setNow('2026-11-13T15:38:34Z');
        const ruleNgn = await mkRule({
            corridor: ['GBP', 'NGN'],
            name: `UK to Nigeria ${n()}`,
            reward_type: 'BOTH',
            referrer_reward: 5,
            referee_reward: 10,
            min_transaction_threshold: 50,
        });
        const referrer1 = await mkCustomer('GBP', { first_name: 'John', last_name: 'ReferrerOne' });
        const referee1Id = `RISHI-${Date.now().toString(36)}-${n()}`;
        const reg1 = await register(referrer1.referral_code, {
            id: referee1Id,
            first_name: 'Rishi',
            last_name: 'Sunak',
            email: `${referee1Id}@example.com`,
            phone: `+44776${String(1000000 + seq)}`,
            send_currency: 'GBP',
            receive_currency: 'NGN',
            kyc_status: 'PASSED',
            device_id: `dev-${referee1Id}`,
        });
        expect(reg1.status).toBe(201);
        await request(app).post('/api/referral/transfer-events').send({
            transfer_id: recycledTxnId,
            customer_id: referee1Id,
            amount: 500,
            currency: 'GBP',
            receive_currency: 'NGN',
            status: 'PAID',
            created_at: referral.clock.now().toISOString(),
        }).expect(200);
        const done1 = await request(app).post('/api/referral/transfer-events').send({
            transfer_id: recycledTxnId,
            customer_id: referee1Id,
            amount: 500,
            currency: 'GBP',
            receive_currency: 'NGN',
            status: 'COMPLETED',
            created_at: referral.clock.now().toISOString(),
        }).expect(200);
        expect(done1.body.referral).toMatchObject({ status: 'REWARDED', referrer_credited: 5, referee_credited: 10 });

        // Archive GBP -> NGN rule and create GBP -> USD rule (Rule 92 in live Render incident)
        await request(app).delete(`/api/referral-rules/${ruleNgn.id}`).expect(200);
        setNow('2026-11-16T06:38:45Z');
        await mkRule({
            corridor: ['GBP', 'USD'],
            name: `New Final Test Rule GBP - USD ${n()}`,
            reward_type: 'BOTH',
            referrer_reward: 5,
            referee_reward: 10,
            min_transaction_threshold: 50,
        });

        // Step 2: Referee 2 registers under GBP -> USD rule and completes recycled TXN-202611-00001 on GBP -> USD >= floor
        const referrer2 = await mkCustomer('GBP', { first_name: 'John', last_name: 'ReferrerTwo' });
        const referee2Id = `DAVID-${Date.now().toString(36)}-${n()}`;
        const reg2 = await register(referrer2.referral_code, {
            id: referee2Id,
            first_name: 'David',
            last_name: 'Tester',
            email: `${referee2Id}@example.com`,
            phone: `+44777${String(1000000 + seq)}`,
            send_currency: 'GBP',
            receive_currency: 'USD',
            kyc_status: 'PASSED',
            device_id: `dev-${referee2Id}`,
        });
        expect(reg2.status).toBe(201);
        expect(reg2.body.data).toMatchObject({
            status: 'REGISTERED',
            currency: 'GBP',
            receive_currency: 'USD',
            referrer_reward: 5,
            referee_reward: 10,
            referee_credited: 10,
            floor: 50,
        });

        const paid2 = await request(app).post('/api/referral/transfer-events').send({
            transfer_id: recycledTxnId,
            customer_id: referee2Id,
            amount: 60,
            currency: 'GBP',
            receive_currency: 'USD',
            status: 'PAID',
            created_at: referral.clock.now().toISOString(),
        }).expect(200);
        expect(paid2.body.referral).toMatchObject({
            status: 'PENDING',
            qualifying_transfer_id: recycledTxnId,
            qualifying_amount: 60,
        });

        const done2 = await request(app).post('/api/referral/transfer-events').send({
            transfer_id: recycledTxnId,
            customer_id: referee2Id,
            amount: 60,
            currency: 'GBP',
            receive_currency: 'USD',
            status: 'COMPLETED',
            created_at: referral.clock.now().toISOString(),
        }).expect(200);
        expect(done2.body.referral).toMatchObject({
            status: 'REWARDED',
            referrer_credited: 5,
            referee_credited: 10,
        });
        expect(await available(referrer2.id, 'GBP')).toBe(5);
    });

    rule('REFERRAL-29', 'Referee receives instant signup bonus and can redeem it on their first valid qualifying transaction', {
        why: 'Approved by Product Owner Mohammad (2026-11-16): the referee receives their welcome bonus as soon as they sign up (even with kyc_status PENDING) and can redeem it on their first valid transaction meeting the rule\'s floor and corridor, while the referrer earns their bonus when that transaction completes.',
        fix: 'createReferral() issues the referee credit immediately upon registration; wallet.applyBonus() enforces the rule\'s floor and receive_currency; cancellation restores the referee\'s credit and resets the referral to REGISTERED; completion credits the referrer without double-crediting the referee; refund voids the returned credit cleanly with zero residual balance or debt under both default clawback and BONUS_CLAWBACK=off.',
        where: [FILE, 'server/bonus/wallet.js'],
    })(async () => {
        const w = await world(
            { reward_type: 'BOTH', referrer_reward: 11, referee_reward: 21, min_transaction_threshold: 50 },
            { referee: { kyc_status: 'PENDING' } },
        );
        expect(w.referral).toMatchObject({ status: 'REGISTERED', referrer_reward: 11, referee_reward: 21, referee_credited: 21, floor: 50 });
        expect(await available(w.refereeId, w.rule.send)).toBe(21);

        // Below floor is rejected with BELOW_FLOOR
        const belowFloor = await request(app).post(`/api/wallet/${w.refereeId}/apply`).send({
            amount: 21,
            currency: w.rule.send,
            receive_currency: w.rule.receive,
            transfer_id: `TX-LOW-${n()}`,
            send_amount: 40,
        });
        expect(belowFloor.status).toBe(400);
        expect(belowFloor.body.error).toBe('BELOW_FLOOR');

        // Wrong corridor is rejected with WRONG_CORRIDOR
        const wrongCorridorCur = POOL.find((c) => c !== w.rule.send && c !== w.rule.receive);
        const wrongCorridor = await request(app).post(`/api/wallet/${w.refereeId}/apply`).send({
            amount: 21,
            currency: w.rule.send,
            receive_currency: wrongCorridorCur,
            transfer_id: `TX-CORR-${n()}`,
            send_amount: 50,
        });
        expect(wrongCorridor.status).toBe(400);
        expect(wrongCorridor.body.error).toBe('WRONG_CORRIDOR');

        // Applying 21 on first valid transaction (tx1) succeeds
        const tx1 = `TX-VALID-1-${n()}`;
        const app1 = await request(app).post(`/api/wallet/${w.refereeId}/apply`).send({
            amount: 21,
            currency: w.rule.send,
            receive_currency: w.rule.receive,
            transfer_id: tx1,
            send_amount: 50,
        });
        expect(app1.status).toBe(200);
        expect(app1.body).toEqual({ applied: 21, available: 0 });
        expect((await send(w, 'PAID', { transfer_id: tx1, amount: 50 })).body.referral.status).toBe('PENDING');

        // If tx1 is CANCELLED, referral returns to REGISTERED and referee's 21 available balance is restored
        await request(app).post(`/api/wallet/${w.refereeId}/release`).send({ transfer_id: tx1 }).expect(200);
        const cancelled = (await send(w, 'CANCELLED', { transfer_id: tx1, amount: 50 })).body.referral;
        expect(cancelled).toMatchObject({ status: 'REGISTERED', qualifying_transfer_id: null });
        expect(await available(w.refereeId, w.rule.send)).toBe(21);

        // Referee applies 21 on tx2 (send_amount: 50, matching corridor), and tx2 goes PAID -> COMPLETED
        const tx2 = `TX-VALID-2-${n()}`;
        const app2 = await request(app).post(`/api/wallet/${w.refereeId}/apply`).send({
            amount: 21,
            currency: w.rule.send,
            receive_currency: w.rule.receive,
            transfer_id: tx2,
            send_amount: 50,
        });
        expect(app2.status).toBe(200);
        expect(app2.body).toEqual({ applied: 21, available: 0 });

        await send(w, 'PAID', { transfer_id: tx2, amount: 50 });
        const completed = (await send(w, 'COMPLETED', { transfer_id: tx2, amount: 50 })).body.referral;
        expect(completed).toMatchObject({
            status: 'REWARDED',
            qualifying_transfer_id: tx2,
            referrer_credited: 11,
            referee_credited: 21,
        });
        expect(await available(w.referrer.id, w.rule.send)).toBe(11);
        const refBalAfterComplete = (await walletOf(w.refereeId, w.rule.send)).balances[0];
        expect(refBalAfterComplete).toMatchObject({
            earned: 21,
            used: 21,
            available: 0,
            outstanding_debt: 0,
        });

        // Refund test (1): default BONUS_CLAWBACK=on (both voidReferralCredits -> releaseBonus and releaseBonus -> voidReferralCredits)
        await send(w, 'REFUNDED', { transfer_id: tx2, amount: 50 });
        await request(app).post(`/api/wallet/${w.refereeId}/release`).send({ transfer_id: tx2 }).expect(200);
        const refBalAfterRefundOn = (await walletOf(w.refereeId, w.rule.send)).balances[0];
        expect(refBalAfterRefundOn).toMatchObject({ available: 0, outstanding_debt: 0 });
        expect(await available(w.referrer.id, w.rule.send)).toBe(0);

        // Refund test (2): BONUS_CLAWBACK=off in both event orders (releaseBonus -> voidReferralCredits AND voidReferralCredits -> releaseBonus)
        const prevClawback = process.env.BONUS_CLAWBACK;
        process.env.BONUS_CLAWBACK = 'off';
        try {
            // Order B: releaseBonus first, then REFUNDED transfer event
            const wOff1 = await world(
                { reward_type: 'BOTH', referrer_reward: 11, referee_reward: 21, min_transaction_threshold: 50 },
                { referee: { kyc_status: 'PENDING' } },
            );
            const txOff1 = `TX-OFF-1-${n()}`;
            await request(app).post(`/api/wallet/${wOff1.refereeId}/apply`).send({
                amount: 21, currency: wOff1.rule.send, receive_currency: wOff1.rule.receive, transfer_id: txOff1, send_amount: 50,
            }).expect(200);
            await qualifyAndComplete(wOff1, { transfer_id: txOff1, amount: 50 });
            await request(app).post(`/api/wallet/${wOff1.refereeId}/release`).send({ transfer_id: txOff1 }).expect(200);
            await send(wOff1, 'REFUNDED', { transfer_id: txOff1, amount: 50 });
            expect((await walletOf(wOff1.refereeId, wOff1.rule.send)).balances[0]).toMatchObject({ available: 0, outstanding_debt: 0 });

            // Order A: REFUNDED transfer event first, then releaseBonus
            const wOff2 = await world(
                { reward_type: 'BOTH', referrer_reward: 11, referee_reward: 21, min_transaction_threshold: 50 },
                { referee: { kyc_status: 'PENDING' } },
            );
            const txOff2 = `TX-OFF-2-${n()}`;
            await request(app).post(`/api/wallet/${wOff2.refereeId}/apply`).send({
                amount: 21, currency: wOff2.rule.send, receive_currency: wOff2.rule.receive, transfer_id: txOff2, send_amount: 50,
            }).expect(200);
            await qualifyAndComplete(wOff2, { transfer_id: txOff2, amount: 50 });
            await send(wOff2, 'REFUNDED', { transfer_id: txOff2, amount: 50 });
            await request(app).post(`/api/wallet/${wOff2.refereeId}/release`).send({ transfer_id: txOff2 }).expect(200);
            expect((await walletOf(wOff2.refereeId, wOff2.rule.send)).balances[0]).toMatchObject({ available: 0, outstanding_debt: 0 });
        } finally {
            if (prevClawback === undefined) delete process.env.BONUS_CLAWBACK;
            else process.env.BONUS_CLAWBACK = prevClawback;
        }
    });
});
