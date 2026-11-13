using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Furp.Application.DTOs.RateDto;
using Furp.Application.Features.Mito.Transactions.command.InitiateRetailTransaction;
using Furp.Application.Interfaces.Repositories;
using Furp.Application.Interfaces.Services;
using Furp.Domain.Entities;

namespace Furp.Application.Features.Rewards
{
    public interface IRewardCheckout
    {
        Task<RewardCheckoutQuote> PriceAsync(
            User user,
            CreateRetailTransactionCommand request,
            TransactionCalculate rate,
            CancellationToken cancellationToken);

        Task<RewardCheckoutQuote> CommitAsync(
            User user,
            string transferId,
            RewardCheckoutQuote quote,
            CancellationToken cancellationToken);

        Task<RewardCheckoutQuote> CommitSendAsync(
            User user,
            string transferId,
            decimal payable,
            decimal fee,
            string currency,
            string receiveCurrency,
            string paymentMethod,
            string promoCode,
            decimal bonusAmount,
            string promoApply,
            CancellationToken cancellationToken);

        /// <summary>True when this transfer already has a redeemed promo, so a payment retry must not discount twice.</summary>
        Task<bool> HasRedeemedPromoAsync(string transferId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// STAGING-SYNC: tenant keys sent to the promo engine on validate AND redeem (restrictions.affiliates).
    /// affiliate_id = the user's affiliate GUID (lower-case "D" format) when there is one, otherwise it is omitted;
    /// site_key is always "rhemito". Written with Convert.ToString + Guid.TryParse so it compiles whether
    /// User.AffiliateId is Guid, Guid?, string or int.
    /// </summary>
    public static class RewardAffiliateKey
    {
        public const string SiteKey = "rhemito";

        public static string AffiliateId(User user)
        {
            var raw = Convert.ToString(user?.AffiliateId, CultureInfo.InvariantCulture);
            return Guid.TryParse(raw, out var id) && id != Guid.Empty
                ? id.ToString("D").ToLowerInvariant()
                : null;
        }
    }

    public class RewardCheckoutQuote
    {
        public bool Ok { get; set; } = true;
        public string Message { get; set; }
        public decimal SendAmount { get; set; }
        public decimal Commission { get; set; }
        public decimal PromoDiscount { get; set; }
        public string PromoCode { get; set; }

        /// <summary>REDUCE_SEND or INCREASE_RECEIVE.</summary>
        public string PromoApply { get; set; } = "REDUCE_SEND";

        /// <summary>Send-currency amount to add to the payout when PromoApply is INCREASE_RECEIVE.</summary>
        public decimal ReceiveBoost { get; set; }
        public decimal BonusRequested { get; set; }
        public string Currency { get; set; }
        public string ReceiveCurrency { get; set; }
        public string PaymentMethod { get; set; }
        public decimal TransferAmount { get; set; }
        public decimal Fee { get; set; }
    }

    public class RewardCheckout : IRewardCheckout
    {
        private readonly RewardEngineGateway _gateway;
        private readonly IDapperRepository _dapper;

        // STAGING-SYNC: smallest amount the customer must still pay after promo + bonus are taken off.
        // Defaults to 0m (no floor). Optionally seeded from env REWARD_MIN_PAYABLE_AMOUNT; can also be set in code.
        public decimal MinPayableAmount { get; set; } = ReadMinPayableDefault();

        public RewardCheckout(RewardEngineGateway gateway, IDapperRepository dapper)
        {
            _gateway = gateway;
            _dapper = dapper;
        }

        // STAGING-SYNC: safe default reader for MinPayableAmount (never throws; negatives clamp to 0m).
        private static decimal ReadMinPayableDefault()
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable("REWARD_MIN_PAYABLE_AMOUNT");
                if (!string.IsNullOrWhiteSpace(raw)
                    && decimal.TryParse(raw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                    && value > 0m)
                {
                    return value;
                }
            }
            catch (Exception)
            {
                // fall through to default
            }

            return 0m;
        }

        public async Task<bool> HasRedeemedPromoAsync(string transferId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(transferId)) return false;
            try
            {
                var count = await _dapper.ExecuteScalarAsync<int>(@"
SELECT COUNT(1)
FROM dbo.RewardPromoRedemption
WHERE Status = N'REDEEMED'
  AND TransferId = @transferId;",
                    new { transferId = transferId.Trim() },
                    cancellationToken: cancellationToken);
                return count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<RewardCheckoutQuote> PriceAsync(
            User user,
            CreateRetailTransactionCommand request,
            TransactionCalculate rate,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                return new RewardCheckoutQuote
                {
                    Ok = false,
                    Message = RewardEngineGateway.UnavailableMessage
                };
            }

            var quote = new RewardCheckoutQuote
            {
                SendAmount = request.SendAmount,
                Commission = rate?.Commission ?? 0m,
                TransferAmount = request.SendAmount,
                Fee = rate?.Commission ?? 0m,
                Currency = (request.SendCurrency ?? "GBP").Trim().ToUpperInvariant(),
                ReceiveCurrency = request.ReceiveCurrency,
                PaymentMethod = request.PreferredPaymentMethod,
                PromoCode = (request.PromoCode ?? string.Empty).Trim().ToUpperInvariant(),
                PromoApply = string.Equals(request.PromoApply, "INCREASE_RECEIVE", StringComparison.OrdinalIgnoreCase)
                    ? "INCREASE_RECEIVE"
                    : "REDUCE_SEND",
                BonusRequested = request.BonusAmount.GetValueOrDefault()
            };

            if (string.IsNullOrWhiteSpace(quote.PromoCode) && quote.BonusRequested <= 0)
            {
                return quote;
            }

            if (!_gateway.IsConfigured)
            {
                quote.Ok = false;
                quote.Message = string.IsNullOrWhiteSpace(quote.PromoCode)
                    ? RewardEngineGateway.UnavailableMessage
                    : RewardEngineGateway.PromoUnavailableMessage;
                return quote;
            }

            if (!string.IsNullOrWhiteSpace(quote.PromoCode))
            {
                // Pass corridor + tenant so the engine can enforce promo restrictions
                // (allowed corridors / affiliates / payment methods) saved in MITO Admin.
                // STAGING-SYNC: affiliate_id is omitted when the user has none; site_key is always "rhemito".
                var validateBody = new Dictionary<string, object>
                {
                    ["code"] = quote.PromoCode,
                    ["amount"] = quote.TransferAmount,
                    ["fee"] = quote.Fee,
                    ["currency"] = quote.Currency,
                    ["user_id"] = user?.UserId.ToString(),
                    ["source_currency"] = quote.Currency,
                    ["dest_currency"] = quote.ReceiveCurrency,
                    ["payment_method"] = quote.PaymentMethod
                };
                var validateAffiliate = RewardAffiliateKey.AffiliateId(user);
                if (validateAffiliate != null) validateBody["affiliate_id"] = validateAffiliate;
                validateBody["site_key"] = RewardAffiliateKey.SiteKey;
                var check = await _gateway.PostAsync("/api/promocodes/validate", validateBody, cancellationToken);

                if (!check.Reachable)
                {
                    quote.Ok = false;
                    quote.Message = RewardEngineGateway.PromoUnavailableMessage;
                    return quote;
                }

                if (!check.IsSuccess || (check.Json?["valid"] != null && !RewardEngineGateway.Flag(check.Json["valid"])))
                {
                    quote.Ok = false;
                    quote.Message = check.ReadError("Invalid promo code");
                    return quote;
                }

                var discount = RewardEngineGateway.Money(check.Json?["appliedDiscount"] ?? check.Json?["applied_discount"]);
                if (discount < 0) discount = 0;
                if (discount > quote.SendAmount) discount = quote.SendAmount;
                var basis = (RewardEngineGateway.Text(check.Json?["discount_basis"] ?? check.Json?["discountBasis"]) ?? string.Empty)
                    .Trim()
                    .ToUpperInvariant();
                quote.PromoDiscount = discount;
                // Percentage of value can add to the payout instead of reducing what they pay.
                // Every other type keeps the existing payable reduction.
                // STAGING-SYNC: FixedAmount codes discount the transfer AMOUNT (not the fee): the fee stays whole.
                var appliesTo = (RewardEngineGateway.Text(check.Json?["applies_to"] ?? check.Json?["appliesTo"]) ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();
                if (appliesTo == "amount")
                {
                    quote.PromoApply = "REDUCE_SEND";
                    quote.SendAmount -= discount;
                }
                else if (basis == "VALUE" && quote.PromoApply == "INCREASE_RECEIVE")
                {
                    quote.ReceiveBoost = discount;
                }
                else
                {
                    quote.PromoApply = "REDUCE_SEND";
                    quote.SendAmount -= discount;
                    quote.Commission = Math.Max(0m, quote.Commission - discount);
                }
            }

            if (quote.BonusRequested > 0 && user != null)
            {
                var wallet = await _gateway.GetAsync(
                    "/api/wallet/" + Uri.EscapeDataString(user.UserId.ToString()) + "?currency=" + Uri.EscapeDataString(quote.Currency),
                    cancellationToken);
                if (!wallet.Reachable)
                {
                    quote.Ok = false;
                    quote.Message = RewardEngineGateway.UnavailableMessage;
                    return quote;
                }

                if (RewardEngineGateway.Flag(wallet.Json?["bonus_blocked"]))
                {
                    quote.Ok = false;
                    quote.Message = RewardEngineGateway.NotQualifiedMessage;
                    return quote;
                }

                // One balance (v1.1): whole wallet; never more than send amount (D2 / B-22).
                var available = Available(wallet.Json, quote.Currency);
                // STAGING-SYNC: stacking fix. Cap the bonus by what is STILL payable after the promo
                // (quote.SendAmount is already net of a REDUCE_SEND promo), not by the gross
                // TransferAmount, otherwise promo + bonus together can debit more than the customer owes.
                var maxBonus = Math.Min(available, Math.Max(0m, quote.SendAmount - MinPayableAmount));
                if (quote.BonusRequested > maxBonus) quote.BonusRequested = maxBonus;
                if (quote.BonusRequested <= 0)
                {
                    quote.Ok = false;
                    quote.Message = "You do not have bonus credit to use on this transfer.";
                }
            }

            return quote;
        }

        public async Task<RewardCheckoutQuote> CommitSendAsync(
            User user,
            string transferId,
            decimal payable,
            decimal fee,
            string currency,
            string receiveCurrency,
            string paymentMethod,
            string promoCode,
            decimal bonusAmount,
            string promoApply,
            CancellationToken cancellationToken)
        {
            var request = new CreateRetailTransactionCommand
            {
                SendAmount = payable,
                SendCurrency = currency,
                ReceiveCurrency = receiveCurrency,
                PreferredPaymentMethod = paymentMethod,
                PromoCode = promoCode,
                PromoApply = promoApply,
                BonusAmount = bonusAmount
            };
            var rate = new TransactionCalculate
            {
                Commission = fee,
                SendAmount = payable
            };
            var quote = await PriceAsync(user, request, rate, cancellationToken);
            if (!quote.Ok) return quote;
            return await CommitAsync(user, transferId, quote, cancellationToken);
        }

        public async Task<RewardCheckoutQuote> CommitAsync(
            User user,
            string transferId,
            RewardCheckoutQuote quote,
            CancellationToken cancellationToken)
        {
            if (quote == null || !quote.Ok) return quote;
            if (string.IsNullOrWhiteSpace(quote.PromoCode) && quote.BonusRequested <= 0) return quote;
            if (user == null || string.IsNullOrWhiteSpace(transferId))
            {
                quote.Ok = false;
                quote.Message = RewardEngineGateway.UnavailableMessage;
                return quote;
            }

            // STAGING-SYNC: true once /api/promocodes/redeem succeeded, so a later bonus-apply failure can release it.
            var promoRedeemed = false;

            if (!string.IsNullOrWhiteSpace(quote.PromoCode))
            {
                // STAGING-SYNC: same tenant keys as /validate so redeem enforces the same affiliate / site restrictions.
                var redeemBody = new Dictionary<string, object>
                {
                    ["code"] = quote.PromoCode,
                    ["amount"] = quote.TransferAmount,
                    ["fee"] = quote.Fee,
                    ["currency"] = quote.Currency,
                    ["user_id"] = user.UserId.ToString(),
                    ["source_currency"] = quote.Currency,
                    ["dest_currency"] = quote.ReceiveCurrency,
                    ["payment_method"] = quote.PaymentMethod,
                    ["transaction_id"] = transferId
                };
                var redeemAffiliate = RewardAffiliateKey.AffiliateId(user);
                if (redeemAffiliate != null) redeemBody["affiliate_id"] = redeemAffiliate;
                redeemBody["site_key"] = RewardAffiliateKey.SiteKey;
                var redeemed = await _gateway.PostAsync("/api/promocodes/redeem", redeemBody, cancellationToken);

                if (!redeemed.Reachable)
                {
                    quote.Ok = false;
                    quote.Message = RewardEngineGateway.PromoUnavailableMessage;
                    return quote;
                }

                if (!redeemed.IsSuccess)
                {
                    quote.Ok = false;
                    quote.Message = redeemed.ReadError("Invalid promo code") + " Go back and remove the promo code to continue.";
                    return quote;
                }

                var discount = RewardEngineGateway.Money(redeemed.Json?["discount"]);
                if (discount > quote.PromoDiscount + 0.009m)
                {
                    await _gateway.PostAsync("/api/promocodes/release", new
                    {
                        transaction_id = transferId,
                        code = quote.PromoCode
                    }, cancellationToken);
                    quote.Ok = false;
                    quote.Message = RewardEngineGateway.PromoChangedMessage;
                    return quote;
                }

                promoRedeemed = true;
            }

            if (quote.BonusRequested > 0)
            {
                // STAGING-SYNC: send_amount is what is still payable after the promo (bonus can never exceed it);
                // transfer_amount is the gross transfer amount used by the wallet to test credit Floor;
                // receive_currency lets the wallet enforce credit corridor (ReceiveCurrency).
                var applied = await _gateway.PostAsync(
                    "/api/wallet/" + Uri.EscapeDataString(user.UserId.ToString()) + "/apply",
                    new
                    {
                        transfer_id = transferId,
                        amount = quote.BonusRequested,
                        currency = quote.Currency,
                        send_amount = quote.SendAmount,
                        transfer_amount = quote.TransferAmount,
                        receive_currency = quote.ReceiveCurrency
                    },
                    cancellationToken);

                if (!applied.Reachable)
                {
                    await ReleasePromoAsync(promoRedeemed, transferId, quote.PromoCode, cancellationToken);
                    quote.Ok = false;
                    quote.Message = RewardEngineGateway.UnavailableMessage;
                    return quote;
                }

                if (!applied.IsSuccess)
                {
                    var message = applied.ReadError("Your bonus balance has changed. Please review your transfer.");
                    if (message.IndexOf("already been applied", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        await ReleasePromoAsync(promoRedeemed, transferId, quote.PromoCode, cancellationToken);
                        quote.Ok = false;
                        quote.Message = message;
                        return quote;
                    }
                }
                else
                {
                    var taken = RewardEngineGateway.Money(applied.Json?["applied"]);
                    if (taken < 0) taken = 0;
                    if (taken > quote.SendAmount) taken = quote.SendAmount;
                    quote.SendAmount -= taken;
                }
            }

            return quote;
        }

        // STAGING-SYNC: best-effort release of an already-redeemed promo when the bonus wallet apply fails, so the
        // customer does not lose the promo use. Release errors are ignored (the original failure message is returned).
        private async Task ReleasePromoAsync(bool promoRedeemed, string transferId, string promoCode, CancellationToken cancellationToken)
        {
            if (!promoRedeemed || string.IsNullOrWhiteSpace(promoCode)) return;
            try
            {
                await _gateway.PostAsync("/api/promocodes/release", new
                {
                    transaction_id = transferId,
                    code = promoCode,
                    reason = "BONUS_APPLY_FAILED"
                }, CancellationToken.None);
            }
            catch (Exception)
            {
                // ignore: best effort
            }
        }

        private static decimal Available(Newtonsoft.Json.Linq.JObject wallet, string currency)
        {
            var balances = wallet?["balances"] as Newtonsoft.Json.Linq.JArray;
            if (balances == null) return 0m;
            foreach (var row in balances)
            {
                var code = RewardEngineGateway.Text(row["currency"]);
                if (string.Equals(code, currency, StringComparison.OrdinalIgnoreCase))
                {
                    return RewardEngineGateway.Money(row["available"]);
                }
            }

            return balances.Count > 0 ? RewardEngineGateway.Money(balances[0]["available"]) : 0m;
        }
    }
}
