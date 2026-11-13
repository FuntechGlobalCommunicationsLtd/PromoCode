using System;
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
                var affiliateKey = user?.AffiliateId is Guid aid && aid != Guid.Empty
                    ? aid.ToString()
                    : "rhemito";
                var check = await _gateway.PostAsync("/api/promocodes/validate", new
                {
                    code = quote.PromoCode,
                    amount = quote.TransferAmount,
                    fee = quote.Fee,
                    currency = quote.Currency,
                    user_id = user?.UserId.ToString(),
                    source_currency = quote.Currency,
                    dest_currency = quote.ReceiveCurrency,
                    payment_method = quote.PaymentMethod,
                    affiliate_id = affiliateKey,
                    site_key = affiliateKey
                }, cancellationToken);

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
                if (basis == "VALUE" && quote.PromoApply == "INCREASE_RECEIVE")
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

            if (!string.IsNullOrWhiteSpace(quote.PromoCode))
            {
                // STAGING-SYNC: same tenant key as /validate so redeem enforces the same affiliate / site restrictions.
                var affiliateKey = user.AffiliateId is Guid aid && aid != Guid.Empty
                    ? aid.ToString()
                    : "rhemito";
                var redeemed = await _gateway.PostAsync("/api/promocodes/redeem", new
                {
                    code = quote.PromoCode,
                    amount = quote.TransferAmount,
                    fee = quote.Fee,
                    currency = quote.Currency,
                    user_id = user.UserId.ToString(),
                    source_currency = quote.Currency,
                    dest_currency = quote.ReceiveCurrency,
                    payment_method = quote.PaymentMethod,
                    transaction_id = transferId,
                    affiliate_id = affiliateKey,
                    site_key = affiliateKey
                }, cancellationToken);

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
                    quote.Ok = false;
                    quote.Message = RewardEngineGateway.UnavailableMessage;
                    return quote;
                }

                if (!applied.IsSuccess)
                {
                    var message = applied.ReadError("Your bonus balance has changed. Please review your transfer.");
                    if (message.IndexOf("already been applied", StringComparison.OrdinalIgnoreCase) < 0)
                    {
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
