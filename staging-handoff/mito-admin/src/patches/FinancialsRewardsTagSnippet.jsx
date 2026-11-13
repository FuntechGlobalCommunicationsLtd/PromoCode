// Drop-in integration helpers for mito-admin's existing Financials screens:
//   - Transactions list (/financials/transactions) -> "Rewards" column
//   - Transaction detail (/financials/transactions/:id) -> "Rewards" summary/detail field
//
// Displays "{CUR} {amount} [{PC|RF|BS}] ({RuleOrCodeName})" using either fields
// already present on the transaction row or a live lookup against
// GET /api/v1/Affiliate/AdminSettings/Rewards/engine/transactions/{ref}/rewards.

import { useEffect, useState } from 'react';
import { call } from '../api/furpRewardsAdminClient';

/**
 * Formats a transaction's applied reward (Promo Code [PC], Referral [RF], or Bonus Scheme [BS]).
 */
export function formatMitoAdminReward(tx) {
    if (!tx) return '—';
    if (tx.rewardDisplay && tx.rewardDisplay !== '—') return tx.rewardDisplay;
    const amount = Number(tx.rewardAmount ?? tx.discount ?? 0);
    const name = tx.rewardName || tx.rewardRuleName || tx.promoCode || tx.schemeName || '';
    if (amount > 0 && name) {
        const rawTag = String(tx.rewardTag || '').toUpperCase();
        const tag = ['PC', 'RF', 'BS'].includes(rawTag)
            ? rawTag
            : tx.rewardType === 'Referral'
              ? 'RF'
              : tx.rewardType === 'Bonus Scheme'
                ? 'BS'
                : 'PC';
        const cur = tx.currency || tx.sendCurrency || 'GBP';
        return `${cur} ${amount.toFixed(2)} [${tag}] (${name})`;
    }
    if (tx.discountDisplay && tx.discountDisplay !== '—') return tx.discountDisplay;
    if (amount > 0) {
        const cur = tx.currency || tx.sendCurrency || 'GBP';
        return `${cur} ${amount.toFixed(2)}`;
    }
    return '—';
}

/**
 * React hook for TransactionDetailPage in mito-admin: fetches authoritative
 * PC / RF / BS reward attribution for a transaction reference or ID via FURP.
 */
export function useTransactionRewardDisplay(transactionRefOrId, fallbackTx = null) {
    const [serverDisplay, setServerDisplay] = useState(null);

    useEffect(() => {
        setServerDisplay(null);
        const ref = String(transactionRefOrId || fallbackTx?.reference || fallbackTx?.id || '').trim();
        if (!ref) return undefined;
        let cancelled = false;
        call('GET', `transactions/${encodeURIComponent(ref)}/rewards`)
            .then((data) => {
                if (!cancelled && data && data.display && data.display !== '—') {
                    setServerDisplay(data.display);
                }
            })
            .catch(() => {});
        return () => {
            cancelled = true;
        };
    }, [transactionRefOrId, fallbackTx?.reference, fallbackTx?.id]);

    return serverDisplay || formatMitoAdminReward(fallbackTx);
}
