using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Furp.Application.Interfaces.Repositories;
using Newtonsoft.Json.Linq;

namespace Furp.Application.Features.Rewards.Engine
{
    public sealed class RewardReferralService
    {
        private readonly IDapperRepository _dapper;
        private readonly RewardWalletService _wallet;
        private readonly RewardSchemeAwards _schemes;
        private readonly RewardDebtBlocks _debt;

        // STAGING-SYNC: additive schema (dbo.RewardReferralVisit + dbo.RewardCreditLedger.Floor/ReceiveCurrency).
        // Idempotent. Applied lazily by EnsureAsync() after RewardSchema.EnsureAsync, so this file works even
        // before RewardSchema.DdlSql is updated. Merge this into RewardSchema.DdlSql / the ops migration script
        // and this latch becomes a harmless no-op.
        public const string StagingDdlSql = @"
IF OBJECT_ID(N'dbo.RewardReferralVisit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RewardReferralVisit (
        Id NVARCHAR(64) NOT NULL CONSTRAINT PK_RewardReferralVisit PRIMARY KEY,
        TrackingId NVARCHAR(64) NULL,
        Code NVARCHAR(32) NULL,
        ReferrerId NVARCHAR(64) NULL,
        RuleId NVARCHAR(64) NULL,
        Currency CHAR(3) NULL,
        ReceiveCurrency CHAR(3) NULL,
        VisitorId NVARCHAR(64) NULL,
        DeviceType NVARCHAR(32) NULL,
        OperatingSystem NVARCHAR(32) NULL,
        SourceChannel NVARCHAR(32) NOT NULL CONSTRAINT DF_RewardReferralVisit_SourceChannel DEFAULT (N'LINK'),
        AttributionStatus NVARCHAR(32) NOT NULL CONSTRAINT DF_RewardReferralVisit_AttributionStatus DEFAULT (N'CLICKED'),
        StoreTarget NVARCHAR(32) NULL,
        ResolvedAt DATETIME2 NULL,
        LinkedReferralId NVARCHAR(64) NULL,
        LinkedRefereeId NVARCHAR(64) NULL,
        ExpiresAt DATETIME2 NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_RewardReferralVisit_CreatedAt DEFAULT (SYSUTCDATETIME())
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RewardReferralVisit_TrackingId' AND object_id = OBJECT_ID(N'dbo.RewardReferralVisit'))
    EXEC (N'CREATE UNIQUE INDEX UX_RewardReferralVisit_TrackingId
                ON dbo.RewardReferralVisit (TrackingId) WHERE TrackingId IS NOT NULL;');
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardReferralVisit_CodeVisitor' AND object_id = OBJECT_ID(N'dbo.RewardReferralVisit'))
    CREATE INDEX IX_RewardReferralVisit_CodeVisitor
        ON dbo.RewardReferralVisit (Code, VisitorId, CreatedAt DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardReferralVisit_Referrer' AND object_id = OBJECT_ID(N'dbo.RewardReferralVisit'))
    CREATE INDEX IX_RewardReferralVisit_Referrer
        ON dbo.RewardReferralVisit (ReferrerId, CreatedAt DESC);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardReferralVisit_LinkedReferee' AND object_id = OBJECT_ID(N'dbo.RewardReferralVisit'))
    EXEC (N'CREATE INDEX IX_RewardReferralVisit_LinkedReferee
                ON dbo.RewardReferralVisit (LinkedRefereeId) WHERE LinkedRefereeId IS NOT NULL;');

IF COL_LENGTH(N'dbo.RewardCreditLedger', N'Floor') IS NULL
    ALTER TABLE dbo.RewardCreditLedger ADD [Floor] DECIMAL(18,2) NULL;
IF COL_LENGTH(N'dbo.RewardCreditLedger', N'ReceiveCurrency') IS NULL
    ALTER TABLE dbo.RewardCreditLedger ADD ReceiveCurrency CHAR(3) NULL;
IF COL_LENGTH(N'dbo.RewardTransferFact', N'PaymentMethod') IS NULL
    ALTER TABLE dbo.RewardTransferFact ADD PaymentMethod NVARCHAR(50) NULL;

-- Unique index only when live data has no duplicates, so bootstrap can never fail on dirty data.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RewardPromoRedemption_Code_Transfer_Redeemed' AND object_id = OBJECT_ID(N'dbo.RewardPromoRedemption'))
   AND NOT EXISTS (SELECT 1 FROM dbo.RewardPromoRedemption WHERE Status = N'REDEEMED' AND TransferId IS NOT NULL
                   GROUP BY PromoCode, TransferId HAVING COUNT(*) > 1)
    EXEC (N'CREATE UNIQUE INDEX UX_RewardPromoRedemption_Code_Transfer_Redeemed
                ON dbo.RewardPromoRedemption (PromoCode, TransferId)
                WHERE Status = N''REDEEMED'' AND TransferId IS NOT NULL;');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardPromoRedemption_CodeStatusUser' AND object_id = OBJECT_ID(N'dbo.RewardPromoRedemption'))
    CREATE INDEX IX_RewardPromoRedemption_CodeStatusUser
        ON dbo.RewardPromoRedemption (PromoCode, Status, UserId)
        INCLUDE (Discount, Currency, TransferId);

IF OBJECT_ID(N'dbo.RewardReferralRuleAudit', N'U') IS NULL
    CREATE TABLE dbo.RewardReferralRuleAudit (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RewardReferralRuleAudit PRIMARY KEY,
        RuleId NVARCHAR(64) NOT NULL,
        Field NVARCHAR(64) NOT NULL,
        OldValue NVARCHAR(MAX) NULL,
        NewValue NVARCHAR(MAX) NULL,
        AdminUser NVARCHAR(128) NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_RewardReferralRuleAudit_CreatedAt DEFAULT (SYSUTCDATETIME()));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardReferralRuleAudit_Rule' AND object_id = OBJECT_ID(N'dbo.RewardReferralRuleAudit'))
    CREATE INDEX IX_RewardReferralRuleAudit_Rule ON dbo.RewardReferralRuleAudit (RuleId, Id DESC);

IF COL_LENGTH(N'dbo.RewardPromoRedemption', N'Flow') IS NULL
    ALTER TABLE dbo.RewardPromoRedemption ADD Flow NVARCHAR(16) NULL;";

        /// <summary>STAGING-SYNC: content type for the CSV exports below.</summary>
        public const string CsvContentType = "text/csv; charset=utf-8";

        private static int _stagingReady;

        // STAGING-SYNC: UK calendar day (Europe/London) is what the SQLite reference uses for rule dates / deadlines.
        private static readonly TimeZoneInfo UkTz = ResolveUkZone();

        public RewardReferralService(
            IDapperRepository dapper,
            RewardWalletService wallet,
            RewardSchemeAwards schemes,
            RewardDebtBlocks debt)
        {
            _dapper = dapper;
            _wallet = wallet;
            _schemes = schemes;
            _debt = debt;
        }

        // STAGING-SYNC: RewardSchema.EnsureAsync + the additive staging DDL.
        private async Task EnsureAsync(CancellationToken cancellationToken)
        {
            await RewardSchema.EnsureAsync(_dapper, cancellationToken);
            if (Interlocked.CompareExchange(ref _stagingReady, 1, 1) == 1) return;
            // STAGING-SYNC: two nodes can race on the very first DDL run; retry once (same approach as RewardWalletService).
            try
            {
                await _dapper.ExecuteAsync(StagingDdlSql, cancellationToken: cancellationToken);
            }
            catch
            {
                await _dapper.ExecuteAsync(StagingDdlSql, cancellationToken: cancellationToken);
            }

            Interlocked.Exchange(ref _stagingReady, 1);
        }

        /// <summary>Test helper — clears the staging DDL latch (pairs with RewardSchema.ResetForTests).</summary>
        public static void ResetStagingForTests() => Interlocked.Exchange(ref _stagingReady, 0);

        public async Task<JObject> UpsertCustomerAsync(JObject body, CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var id = Text(body?["id"]);
            if (string.IsNullOrWhiteSpace(id))
                return new JObject { ["error"] = "id is required", ["_httpStatus"] = 400 };

            var existing = await _dapper.QueryFirstOrDefaultAsync<CustomerRow>(@"
SELECT Id, FirstName, LastName, Email, SendCurrency, AccountStatus, KycStatus, ReferralCode, CreatedAt, UpdatedAt
FROM dbo.RewardCustomer WHERE Id = @id;",
                new { id },
                cancellationToken: cancellationToken);

            var code = existing?.ReferralCode;
            if (string.IsNullOrWhiteSpace(code))
            {
                code = await UniqueCodeAsync(cancellationToken);
            }

            await _dapper.ExecuteAsync(@"
MERGE dbo.RewardCustomer AS t
USING (SELECT @Id AS Id) AS s ON t.Id = s.Id
WHEN MATCHED THEN UPDATE SET
    FirstName=@FirstName, LastName=@LastName, Email=@Email, SendCurrency=@SendCurrency,
    AccountStatus=@AccountStatus, KycStatus=@KycStatus, ReferralCode=@ReferralCode, UpdatedAt=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
    (Id, FirstName, LastName, Email, SendCurrency, AccountStatus, KycStatus, ReferralCode)
    VALUES (@Id, @FirstName, @LastName, @Email, @SendCurrency, @AccountStatus, @KycStatus, @ReferralCode);",
                new
                {
                    Id = id,
                    FirstName = Text(body["first_name"] ?? body["firstName"]),
                    LastName = Text(body["last_name"] ?? body["lastName"]),
                    Email = Text(body["email"]),
                    SendCurrency = (Text(body["send_currency"] ?? body["sendCurrency"]) ?? "GBP").ToUpperInvariant(),
                    AccountStatus = Text(body["account_status"] ?? body["accountStatus"]) ?? "ACTIVE",
                    KycStatus = Text(body["kyc_status"] ?? body["kycStatus"]) ?? "PENDING",
                    ReferralCode = code
                },
                cancellationToken: cancellationToken);

            return new JObject
            {
                ["success"] = true,
                ["data"] = new JObject { ["referral_code"] = code, ["id"] = id }
            };
        }

        public Task<JObject> GetOfferAsync(string customerId, string currency, CancellationToken cancellationToken) =>
            GetOfferAsync(customerId, currency, null, cancellationToken);

        // STAGING-SYNC: corridor-aware overload. Adds currency_offers / offers, cap_reached (COUNT, AMOUNT and
        // scheme budget caps) and the ACCOUNT_NOT_ACTIVE guard. Original keys of "offer" are unchanged.
        public async Task<JObject> GetOfferAsync(
            string customerId,
            string currency,
            string receiveCurrency,
            CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var customer = await GetCustomerAsync(customerId, cancellationToken);
            if (customer != null && !IsActive(customer))
            {
                return new JObject
                {
                    ["offer"] = null,
                    ["offers"] = new JArray(),
                    ["currency_offers"] = new JArray(),
                    ["reason"] = "ACCOUNT_NOT_ACTIVE",
                    ["referral_code"] = customer.ReferralCode
                };
            }

            var receive = NormaliseCurrency(receiveCurrency);
            var live = await LoadLiveRulesAsync(cancellationToken);
            var rule = PickRule(live, currency, receive);
            if (rule == null)
            {
                return new JObject
                {
                    ["offer"] = null,
                    ["offers"] = new JArray(),
                    ["currency_offers"] = new JArray(),
                    ["reason"] = "NO_RULE",
                    ["referral_code"] = customer?.ReferralCode
                };
            }

            var offer = await BuildOfferAsync(rule, customer, cancellationToken);

            var primary = NormaliseCurrency(currency) ?? "GBP";
            var sendCurrencies = SendCurrenciesFor(primary);
            var offers = new JArray();
            var currencyOffers = new JArray();
            foreach (var cur in sendCurrencies)
            {
                foreach (var r in OfferRules(live, cur, receive))
                {
                    var built = r.Id == rule.Id
                        ? (JObject)offer.DeepClone()
                        : await BuildOfferAsync(r, customer, cancellationToken);
                    currencyOffers.Add(built);
                    if (cur == primary) offers.Add(built.DeepClone());
                }
            }

            return new JObject
            {
                ["offer"] = offer,
                ["offers"] = offers,
                ["send_currencies"] = new JArray(sendCurrencies),
                ["currency_offers"] = currencyOffers,
                ["reason"] = null
            };
        }

        public Task<JObject> GetCodeAsync(string code, string currency, CancellationToken cancellationToken) =>
            GetCodeAsync(code, currency, null, cancellationToken);

        public Task<JObject> GetCodeAsync(
            string code,
            string currency,
            string receiveCurrency,
            CancellationToken cancellationToken) =>
            GetCodeAsync(code, currency, receiveCurrency, null, cancellationToken);

        // STAGING-SYNC: referral code lookup with
        //   * currency-suffixed share codes ({BASE}{CUR}, e.g. A1B2C3GBP) resolved to the referrer's base code
        //   * currency precedence  explicit ?? codeCurrency ?? referrer.SendCurrency ?? "GBP"
        //   * optional rule lock (?rule_id=): ENDED rule => LINK_EXPIRED, live rule => single locked offer
        // (port of referral.js GET /api/referral/codes/:code). Old overloads delegate here with ruleId = null.
        public async Task<JObject> GetCodeAsync(
            string code,
            string currency,
            string receiveCurrency,
            string ruleId,
            CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var (referrer, codeCurrency) = await ResolveCodeAsync(code, cancellationToken);
            code = CanonicalCode(code);
            if (referrer == null)
            {
                return new JObject { ["valid"] = false, ["error"] = "Invalid referral code", ["_httpStatus"] = 404 };
            }

            // STAGING-SYNC: referrer must be ACTIVE.
            if (!IsActive(referrer)) return InactiveReferrerError(404, true);

            var receive = NormaliseCurrency(receiveCurrency);
            var requestedRuleId = string.IsNullOrWhiteSpace(ruleId) ? null : ruleId.Trim();
            if (requestedRuleId != null)
            {
                var chosen = await _dapper.QueryFirstOrDefaultAsync<RuleRow>(@"
SELECT TOP 1 Id, Name, Status, BaseCurrency, ReceiveCurrency, ConfigJson
FROM dbo.RewardReferralRule WHERE Id = @id;",
                    new { id = requestedRuleId },
                    cancellationToken: cancellationToken);
                if (chosen != null && !IsArchived(chosen))
                {
                    var chosenStatus = RuleStatus(chosen, UkToday());
                    if (chosenStatus == "ENDED") return LinkExpired();
                    if (chosenStatus == "ACTIVE")
                    {
                        var lockedOffer = await BuildOfferAsync(chosen, referrer, cancellationToken);
                        return new JObject
                        {
                            ["valid"] = true,
                            ["code"] = code,
                            ["referrer_first_name"] = referrer.FirstName,
                            ["rule_id"] = chosen.Id,
                            ["offer"] = lockedOffer,
                            ["offers"] = new JArray(lockedOffer.DeepClone()),
                            ["send_currencies"] = new JArray(NormaliseCurrency(chosen.BaseCurrency) ?? chosen.BaseCurrency),
                            ["currency_offers"] = new JArray(lockedOffer.DeepClone())
                        };
                    }
                }
            }

            var effectiveCurrency = NormaliseCurrency(currency)
                                    ?? codeCurrency
                                    ?? NormaliseCurrency(referrer.SendCurrency)
                                    ?? "GBP";

            var live = await LoadLiveRulesAsync(cancellationToken);
            var corridorRules = OfferRules(live, effectiveCurrency, receive);
            if (corridorRules.Count == 0 && await HasEndedRuleForAsync(effectiveCurrency, receive, cancellationToken))
            {
                return LinkExpired();
            }

            if (codeCurrency != null && corridorRules.Count > 0)
            {
                var lockedOffers = new JArray();
                foreach (var r in corridorRules) lockedOffers.Add(await BuildOfferAsync(r, referrer, cancellationToken));
                var locked = new JObject
                {
                    ["valid"] = true,
                    ["code"] = code,
                    ["currency"] = effectiveCurrency
                };
                if (corridorRules.Count == 1) locked["rule_id"] = corridorRules[0].Id;
                locked["referrer_first_name"] = referrer.FirstName;
                locked["offer"] = lockedOffers[0].DeepClone();
                locked["offers"] = lockedOffers;
                locked["send_currencies"] = new JArray(effectiveCurrency);
                locked["currency_offers"] = lockedOffers.DeepClone();
                return locked;
            }

            var offer = await GetOfferAsync(referrer.Id, effectiveCurrency, receiveCurrency, cancellationToken);
            return new JObject
            {
                ["valid"] = true,
                ["code"] = code,
                ["referrer_first_name"] = referrer.FirstName,
                ["offer"] = offer["offer"],
                // STAGING-SYNC: multi-currency offers (additive keys).
                ["offers"] = offer["offers"] ?? new JArray(),
                ["send_currencies"] = offer["send_currencies"] ?? new JArray(),
                ["currency_offers"] = offer["currency_offers"] ?? new JArray()
            };
        }

        private static JObject LinkExpired() =>
            new JObject
            {
                ["valid"] = false,
                ["error"] = "LINK_EXPIRED",
                ["message"] = "Link Expired",
                ["_httpStatus"] = 400
            };

        // True when a non-archived rule for this corridor exists but its end_date has passed.
        private async Task<bool> HasEndedRuleForAsync(string currency, string receive, CancellationToken cancellationToken)
        {
            var rows = await _dapper.QueryAsync<RuleRow>(@"
SELECT Id, Name, Status, BaseCurrency, ReceiveCurrency, ConfigJson
FROM dbo.RewardReferralRule;",
                new { },
                cancellationToken: cancellationToken);
            var cur = NormaliseCurrency(currency) ?? "GBP";
            var today = UkToday();
            return (rows ?? Array.Empty<RuleRow>()).Any(r =>
                string.Equals(r.BaseCurrency?.Trim(), cur, StringComparison.OrdinalIgnoreCase)
                && RuleStatus(r, today) == "ENDED"
                && (receive == null || RuleReceive(r) == null
                    || string.Equals(RuleReceive(r), receive, StringComparison.OrdinalIgnoreCase)));
        }
        public async Task<JObject> CreateReferralAsync(JObject body, CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var code = CanonicalCode(Text(body?["code"] ?? body?["referral_code"]));
            var referee = body?["referee"] as JObject;
            var refereeId = Text(
                body?["referee_id"]
                ?? body?["refereeId"]
                ?? body?["customer_id"]
                ?? referee?["id"]
                ?? referee?["customer_id"]);
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(refereeId))
            {
                return new JObject { ["error"] = "code and referee are required", ["_httpStatus"] = 400 };
            }

            // STAGING-SYNC: exact code first, then currency-suffixed share code ({BASE}{CUR}); the full code is kept below.
            var (referrer, _) = await ResolveCodeAsync(code, cancellationToken);
            if (referrer == null)
                return new JObject { ["error"] = "Invalid referral code", ["_httpStatus"] = 400 };
            // STAGING-SYNC: referrer must be ACTIVE, otherwise REFERRER_INACTIVE.
            if (!IsActive(referrer))
                return InactiveReferrerError(400, false);
            if (string.Equals(referrer.Id, refereeId, StringComparison.OrdinalIgnoreCase))
                return new JObject { ["error"] = "You cannot use your own referral code", ["_httpStatus"] = 400 };

            var existing = await _dapper.QueryFirstOrDefaultAsync<ReferralRow>(@"
SELECT TOP 1 Id, Code, ReferrerId, RefereeId, RuleId, Status, StatusReason, Currency, ReceiveCurrency,
       ReferrerReward, RefereeReward, ReferrerCredited, RefereeCredited, QualifyingTransferId,
       QualifyingAmount, RegisteredAt, RewardedAt, ConfigJson
FROM dbo.RewardReferral WHERE RefereeId = @refereeId;",
                new { refereeId },
                cancellationToken: cancellationToken);
            if (existing != null)
            {
                await CreditRefereeWelcomeAsync(existing, cancellationToken);
                await AwardExistingTransferAsync(existing, cancellationToken);
                return new JObject
                {
                    ["data"] = new JObject { ["id"] = existing.Id, ["status"] = existing.Status }
                };
            }

            // STAGING-SYNC: click attribution + corridor + rule priority (start/end validity, corridor beats generic).
            var click = await FindClickAsync(body, referee, code, cancellationToken);
            var receive = NormaliseCurrency(Text(
                referee?["receive_currency"] ?? referee?["receiveCurrency"]
                ?? body?["receive_currency"] ?? body?["receiveCurrency"])) ?? NormaliseCurrency(click?.ReceiveCurrency);
            var live = await LoadLiveRulesAsync(cancellationToken);
            var requestedRuleId = Text(body?["rule_id"] ?? body?["ruleId"] ?? referee?["rule_id"]);
            RuleRow rule = null;
            if (!string.IsNullOrWhiteSpace(requestedRuleId))
            {
                rule = live.FirstOrDefault(r => string.Equals(r.Id, requestedRuleId.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            rule ??= PickRule(live, referrer.SendCurrency ?? "GBP", receive);
            var cfg = rule != null ? Parse(rule.ConfigJson) : new JObject();
            var referrerReward = Money(cfg["referrer_reward"] ?? cfg["referrerReward"] ?? cfg["referrer_bonus"]);
            var refereeReward = Money(cfg["referee_reward"] ?? cfg["refereeReward"] ?? cfg["referee_bonus"]);
            var ruleReceive = NormaliseCurrency(rule?.ReceiveCurrency ?? Text(cfg["receive_currency"])) ?? receive;

            // STAGING-SYNC: duplicate check — same identity as the referrer, or a returning customer that
            // already owns a CLOSED account (matched on e-mail; RewardCustomer has no phone column).
            var refereeRow = await GetCustomerAsync(refereeId, cancellationToken);
            var refereeEmail = Text(referee?["email"] ?? body?["email"]);
            if (string.IsNullOrWhiteSpace(refereeEmail)) refereeEmail = refereeRow?.Email;
            var status = "REGISTERED";
            string statusReason = null;
            if (!string.IsNullOrWhiteSpace(refereeEmail) && !string.IsNullOrWhiteSpace(referrer.Email)
                && string.Equals(refereeEmail.Trim(), referrer.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                status = "NOT_ELIGIBLE";
                statusReason = "Self-referral: same email";
            }
            else if (!string.IsNullOrWhiteSpace(refereeEmail))
            {
                var closedId = await _dapper.QueryFirstOrDefaultAsync<string>(@"
SELECT TOP 1 Id FROM dbo.RewardCustomer
WHERE UPPER(ISNULL(AccountStatus, N'')) = N'CLOSED' AND Id <> @refereeId
  AND LOWER(Email) = LOWER(@email);",
                    new { refereeId, email = refereeEmail.Trim() },
                    cancellationToken: cancellationToken);
                if (!string.IsNullOrWhiteSpace(closedId))
                {
                    status = "NOT_ELIGIBLE";
                    statusReason = "Returning customer";
                }
            }

            // STAGING-SYNC: referrer cap (COUNT or AMOUNT) enforced at registration. The referral is still
            // recorded (the friend keeps their welcome bonus); the referrer reward is withheld at credit time.
            if (status == "REGISTERED" && rule != null && referrerReward > 0 && RewardType(cfg) != "REFEREE"
                && await IsReferrerCapReachedAsync(referrer.Id, rule.Id, cfg, referrerReward, cancellationToken))
            {
                statusReason = "Referrer cap reached";
            }

            var id = "REF-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            await _dapper.ExecuteAsync(@"
INSERT INTO dbo.RewardReferral
(Id, Code, ReferrerId, RefereeId, RuleId, Status, StatusReason, Currency, ReceiveCurrency,
 ReferrerReward, RefereeReward, RegisteredAt, ConfigJson)
VALUES
(@Id, @Code, @ReferrerId, @RefereeId, @RuleId, @Status, @StatusReason, @Currency, @ReceiveCurrency,
 @ReferrerReward, @RefereeReward, SYSUTCDATETIME(), @ConfigJson);",
                new
                {
                    Id = id,
                    Code = code,
                    ReferrerId = referrer.Id,
                    RefereeId = refereeId,
                    RuleId = rule?.Id,
                    Status = status,
                    StatusReason = statusReason,
                    Currency = rule?.BaseCurrency ?? referrer.SendCurrency ?? "GBP",
                    ReceiveCurrency = ruleReceive,
                    ReferrerReward = referrerReward,
                    RefereeReward = refereeReward,
                    ConfigJson = cfg.ToString(Newtonsoft.Json.Formatting.None)
                },
                cancellationToken: cancellationToken);

            // STAGING-SYNC: mark the click as LINKED to this referral (best effort — never blocks registration).
            if (click != null)
            {
                try
                {
                    await _dapper.ExecuteAsync(@"
UPDATE dbo.RewardReferralVisit
SET AttributionStatus = N'LINKED', LinkedReferralId = @referralId, LinkedRefereeId = @refereeId,
    ResolvedAt = COALESCE(ResolvedAt, SYSUTCDATETIME())
WHERE Id = @visitId AND LinkedReferralId IS NULL;",
                        new { referralId = id, refereeId, visitId = click.Id },
                        cancellationToken: cancellationToken);
                }
                catch
                {
                    // attribution is informational only
                }
            }

            var created = await _dapper.QueryFirstOrDefaultAsync<ReferralRow>(@"
SELECT TOP 1 Id, Code, ReferrerId, RefereeId, RuleId, Status, StatusReason, Currency, ReceiveCurrency,
       ReferrerReward, RefereeReward, ReferrerCredited, RefereeCredited, QualifyingTransferId,
       QualifyingAmount, RegisteredAt, RewardedAt, ConfigJson
FROM dbo.RewardReferral WHERE Id = @id;",
                new { id },
                cancellationToken: cancellationToken);
            await CreditRefereeWelcomeAsync(created, cancellationToken);
            await AwardExistingTransferAsync(created, cancellationToken);

            var data = new JObject { ["id"] = id, ["status"] = status };
            if (statusReason != null) data["status_reason"] = statusReason;
            if (click?.TrackingId != null) data["tracking_id"] = click.TrackingId;
            return new JObject { ["data"] = data };
        }

        public async Task<JObject> ListReferralsAsync(string referrerId, CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var rows = await _dapper.QueryAsync<ReferralRow>(@"
SELECT Id, Code, ReferrerId, RefereeId, RuleId, Status, StatusReason, Currency, ReceiveCurrency,
       ReferrerReward, RefereeReward, ReferrerCredited, RefereeCredited, QualifyingTransferId,
       QualifyingAmount, RegisteredAt, RewardedAt, ConfigJson
FROM dbo.RewardReferral
WHERE (@referrerId IS NULL OR ReferrerId = @referrerId)
ORDER BY RegisteredAt DESC;",
                new { referrerId = string.IsNullOrWhiteSpace(referrerId) ? null : referrerId },
                cancellationToken: cancellationToken);

            var list = (rows ?? Array.Empty<ReferralRow>()).ToList();
            if (!string.IsNullOrWhiteSpace(referrerId))
            {
                var waiting = list.Where(r =>
                {
                    var status = (r.Status ?? string.Empty).ToUpperInvariant();
                    return status is "REGISTERED" or "PENDING";
                }).ToList();
                foreach (var row in waiting)
                {
                    await AwardExistingTransferAsync(row, cancellationToken);
                }

                if (waiting.Count > 0)
                {
                    rows = await _dapper.QueryAsync<ReferralRow>(@"
SELECT Id, Code, ReferrerId, RefereeId, RuleId, Status, StatusReason, Currency, ReceiveCurrency,
       ReferrerReward, RefereeReward, ReferrerCredited, RefereeCredited, QualifyingTransferId,
       QualifyingAmount, RegisteredAt, RewardedAt, ConfigJson
FROM dbo.RewardReferral
WHERE ReferrerId = @referrerId
ORDER BY RegisteredAt DESC;",
                        new { referrerId },
                        cancellationToken: cancellationToken);
                    list = (rows ?? Array.Empty<ReferralRow>()).ToList();
                }
            }

            var data = new JArray(list.Select(r => new JObject
            {
                ["id"] = r.Id,
                ["referral_id"] = r.Id,
                ["code"] = r.Code,
                ["referrer_id"] = r.ReferrerId,
                ["referee_id"] = r.RefereeId,
                ["rule_id"] = r.RuleId,
                ["status"] = r.Status,
                ["status_reason"] = r.StatusReason,
                ["currency"] = r.Currency,
                ["referrer_reward"] = r.ReferrerReward,
                ["referee_reward"] = r.RefereeReward,
                ["referrer_credited"] = r.ReferrerCredited,
                ["referee_credited"] = r.RefereeCredited,
                ["qualifying_transfer_id"] = r.QualifyingTransferId,
                ["qualifying_amount"] = r.QualifyingAmount,
                ["registered_at"] = r.RegisteredAt,
                ["rewarded_at"] = r.RewardedAt
            }));

            return new JObject
            {
                ["data"] = data,
                ["summary"] = new JObject
                {
                    ["joined"] = list.Count,
                    ["earned_count"] = list.Count(r =>
                        string.Equals(r.Status, "REWARDED", StringComparison.OrdinalIgnoreCase)),
                    ["total_earned"] = list.Sum(r => r.ReferrerCredited ?? 0)
                }
            };
        }

        public async Task<JObject> TrackingAsync(CancellationToken cancellationToken) =>
            await ListReferralsAsync(null, cancellationToken);

        public async Task<JObject> PerformanceAsync(CancellationToken cancellationToken)
        {
            var tracking = await TrackingAsync(cancellationToken);
            return new JObject
            {
                ["data"] = tracking["data"],
                ["summary"] = tracking["summary"]
            };
        }

        public async Task<JObject> TransferEventAsync(JObject body, CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var customerId = Text(body?["customer_id"] ?? body?["customerId"]);
            var transferId = Text(body?["transfer_id"] ?? body?["transferId"]);
            var status = (Text(body?["status"]) ?? string.Empty).ToUpperInvariant();
            var amount = Money(body?["amount"]);
            var currency = (Text(body?["currency"]) ?? "GBP").ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(customerId) || string.IsNullOrWhiteSpace(transferId))
            {
                return new JObject { ["bonuses"] = new JArray(), ["awards"] = new JArray() };
            }

            await _schemes.RecordTransferAsync(
                transferId, customerId, amount, currency,
                Text(body?["receive_currency"] ?? body?["receiveCurrency"]),
                status, cancellationToken,
                // STAGING-SYNC: needs the optional `paymentMethod` parameter from PATCHES-AND-WIRING.md section 6.1
                paymentMethod: Text(body?["payment_method"] ?? body?["paymentMethod"]));

            // Failed / cancelled — give the wallet hold back. Refund, recall and chargeback
            // also reverse scheme and referral credit. A plain failure does not add a strike.
            if (status is "FAILED" or "REFUNDED" or "CANCELLED" or "ABORTED" or "RECALLED" or "CHARGEBACK")
            {
                await _wallet.ReleaseAsync(customerId, new JObject { ["transfer_id"] = transferId }, cancellationToken);
                var reversesCredit = status is "REFUNDED" or "RECALLED" or "CHARGEBACK";
                if (reversesCredit)
                {
                    await _debt.ReverseSchemeCreditAsync(customerId, transferId, status, true, cancellationToken);
                    await ReverseReferralAsync(transferId, cancellationToken);
                }

                return new JObject { ["bonuses"] = new JArray(), ["returned"] = true };
            }

            // A payment being taken is not a completed transfer.
            if (status is not ("SUCCESS" or "COMPLETED" or "SETTLED" or "PROCESSED"))
            {
                return new JObject { ["bonuses"] = new JArray() };
            }

            var awards = await _schemes.AwardForTransferAsync(
                transferId, customerId, amount, currency, cancellationToken);

            var referral = await _dapper.QueryFirstOrDefaultAsync<ReferralRow>(@"
SELECT TOP 1 Id, Code, ReferrerId, RefereeId, RuleId, Status, StatusReason, Currency, ReceiveCurrency,
       ReferrerReward, RefereeReward, ReferrerCredited, RefereeCredited, QualifyingTransferId,
       QualifyingAmount, RegisteredAt, RewardedAt, ConfigJson
FROM dbo.RewardReferral
WHERE RefereeId = @customerId AND Status IN (N'REGISTERED', N'PENDING')
ORDER BY RegisteredAt ASC;",
                new { customerId },
                cancellationToken: cancellationToken);

            if (referral != null)
            {
                // STAGING-SYNC: qualification_window_days — a transfer that arrives after the deadline
                // no longer qualifies; the referral is closed as EXPIRED (the friend's welcome bonus stays).
                if (IsPastQualificationWindow(referral, EventTimeUtc(body)))
                {
                    await ExpireReferralAsync(referral.Id, "Qualification window ended", cancellationToken);
                    return new JObject { ["bonuses"] = awards, ["awards"] = awards };
                }

                var cfg = Parse(referral.ConfigJson);
                var minAmount = Money(cfg["min_transaction_threshold"] ?? cfg["min_amount"] ?? cfg["minimum"]);
                // The friend may have paid less because their welcome bonus came off the bill.
                // The minimum is the amount they sent, before that bonus.
                var qualifying = await QualifyingSendAsync(customerId, transferId, amount, cancellationToken);
                if (minAmount <= 0 || qualifying >= minAmount)
                {
                    await CreditReferralAsync(referral, transferId, qualifying, currency, awards, cancellationToken);
                }
            }

            return new JObject { ["bonuses"] = awards, ["awards"] = awards };
        }

        public async Task<JObject> ApproveAsync(
            string referralId,
            JObject body,
            CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var reason = Text(body?["reason"]);
            if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            {
                return new JObject
                {
                    ["error"] = "Approval reason must be at least 10 characters.",
                    ["_httpStatus"] = 400
                };
            }

            var referral = await _dapper.QueryFirstOrDefaultAsync<ReferralRow>(@"
SELECT TOP 1 Id, Code, ReferrerId, RefereeId, RuleId, Status, StatusReason, Currency, ReceiveCurrency,
       ReferrerReward, RefereeReward, ReferrerCredited, RefereeCredited, QualifyingTransferId,
       QualifyingAmount, RegisteredAt, RewardedAt, ConfigJson
FROM dbo.RewardReferral WHERE Id = @id;",
                new { id = referralId },
                cancellationToken: cancellationToken);
            if (referral == null)
                return new JObject { ["error"] = "Referral not found", ["_httpStatus"] = 404 };

            if (string.Equals(referral.Status, "REWARDED", StringComparison.OrdinalIgnoreCase))
            {
                return new JObject { ["success"] = true, ["id"] = referral.Id, ["status"] = "REWARDED" };
            }

            var awards = new JArray();
            await CreditReferralAsync(
                referral,
                referral.QualifyingTransferId ?? "manual-approve",
                referral.QualifyingAmount ?? 0,
                referral.Currency ?? "GBP",
                awards,
                cancellationToken);

            await _dapper.ExecuteAsync(@"
UPDATE dbo.RewardReferral SET StatusReason=@reason WHERE Id=@id;",
                new { id = referral.Id, reason = "Approved: " + reason.Trim() },
                cancellationToken: cancellationToken);

            return new JObject
            {
                ["success"] = true,
                ["id"] = referral.Id,
                ["status"] = "REWARDED",
                ["bonuses"] = awards
            };
        }

        private async Task CreditReferralAsync(
            ReferralRow referral,
            string transferId,
            decimal amount,
            string currency,
            JArray awards,
            CancellationToken cancellationToken)
        {
            decimal referrerCredited = referral.ReferrerCredited ?? 0;
            decimal refereeCredited = referral.RefereeCredited ?? 0;
            var validity = (int)Money(Parse(referral.ConfigJson)["bonus_validity_days"] ?? Parse(referral.ConfigJson)["validity_days"]);
            if (validity <= 0) validity = 90;
            var notes = new List<string>();
            if (referrerCredited <= 0 && (referral.ReferrerReward ?? 0) > 0)
            {
                // STAGING-SYNC: referrer account / scheme budget / referrer cap checked when the reward is issued.
                var blocked = await ReferrerBlockReasonAsync(referral, cancellationToken);
                if (blocked != null)
                {
                    notes.Add(blocked);
                }
                else
                {
                    await _wallet.IssueCreditAsync(
                        referral.ReferrerId,
                        referral.ReferrerReward.Value,
                        referral.Currency ?? currency,
                        validity,
                        "REFERRAL",
                        "REFERRER",
                        referral.Id,
                        referral.RuleId,
                        null,
                        referral.Id + ":referrer",
                        "Referral bonus",
                        "System",
                        cancellationToken);
                    referrerCredited = referral.ReferrerReward.Value;
                    awards.Add(new JObject
                    {
                        ["status"] = "AWARDED",
                        ["customer_id"] = referral.ReferrerId,
                        ["amount"] = referrerCredited,
                        ["currency"] = referral.Currency ?? currency,
                        ["credit_source"] = "REFERRAL"
                    });
                }
            }

            if (refereeCredited <= 0 && (referral.RefereeReward ?? 0) > 0)
            {
                await _wallet.IssueCreditAsync(
                    referral.RefereeId,
                    referral.RefereeReward.Value,
                    referral.Currency ?? currency,
                    validity,
                    "REFERRAL",
                    "REFEREE",
                    referral.Id,
                    referral.RuleId,
                    null,
                    referral.Id + ":referee",
                    "Welcome bonus",
                    "System",
                    cancellationToken);
                await StampCreditFloorAsync(referral, cancellationToken);
                refereeCredited = referral.RefereeReward.Value;
                awards.Add(new JObject
                {
                    ["status"] = "AWARDED",
                    ["customer_id"] = referral.RefereeId,
                    ["amount"] = refereeCredited,
                    ["currency"] = referral.Currency ?? currency,
                    ["credit_source"] = "REFERRAL"
                });
            }

            await _dapper.ExecuteAsync(@"
UPDATE dbo.RewardReferral
SET Status=N'REWARDED', QualifyingTransferId=@transferId, QualifyingAmount=@amount,
    ReferrerCredited=@referrerCredited, RefereeCredited=@refereeCredited, RewardedAt=SYSUTCDATETIME(),
    StatusReason=@note
WHERE Id=@id;",
                new
                {
                    id = referral.Id,
                    transferId,
                    amount,
                    referrerCredited,
                    refereeCredited,
                    note = notes.Count > 0 ? string.Join("; ", notes) : null
                },
                cancellationToken: cancellationToken);
        }

        private async Task ReverseReferralAsync(string transferId, CancellationToken cancellationToken)
        {
            var referral = await _dapper.QueryFirstOrDefaultAsync<ReferralRow>(@"
SELECT TOP 1 Id, Code, ReferrerId, RefereeId, RuleId, Status, StatusReason, Currency, ReceiveCurrency,
       ReferrerReward, RefereeReward, ReferrerCredited, RefereeCredited, QualifyingTransferId,
       QualifyingAmount, RegisteredAt, RewardedAt, ConfigJson
FROM dbo.RewardReferral
WHERE QualifyingTransferId = @transferId AND Status = N'REWARDED';",
                new { transferId },
                cancellationToken: cancellationToken);
            if (referral == null) return;

            await VoidReferralCreditAsync(referral.ReferrerId, referral.Id + ":referrer", cancellationToken);
            await VoidReferralCreditAsync(referral.RefereeId, referral.Id + ":referee", cancellationToken);
            await _dapper.ExecuteAsync(@"
UPDATE dbo.RewardReferral
SET Status = N'REVERSED', StatusReason = N'Qualifying transfer was refunded'
WHERE Id = @id AND Status = N'REWARDED';",
                new { id = referral.Id },
                cancellationToken: cancellationToken);
        }

        private async Task VoidReferralCreditAsync(string userId, string referenceId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(referenceId)) return;
            var credit = await _dapper.QueryFirstOrDefaultAsync<RewardWalletService.LedgerRow>(@"
SELECT TOP 1 Id, UserId, Amount, Type, Currency, SchemeId, ReferenceId, ReasonCode, Notes,
       AdminUser, ExpiresAt, CreatedAt, ReferralId, ReferralRuleId, SourceCreditId,
       TransferId, CreditSource, CreditSourceDetail, ReturnedFromCreditId, SourceType
FROM dbo.RewardCreditLedger
WHERE ReferenceId = @referenceId AND Type = N'EARNED';",
                new { referenceId },
                cancellationToken: cancellationToken);
            if (credit == null) return;
            var voided = await _dapper.ExecuteScalarAsync<int>(@"
SELECT COUNT(1) FROM dbo.RewardCreditLedger
WHERE SourceCreditId = @id AND Type = N'VOIDED';",
                new { id = credit.Id },
                cancellationToken: cancellationToken);
            if (voided > 0) return;
            var spent = await _dapper.ExecuteScalarAsync<decimal>(@"
SELECT ISNULL(SUM(ABS(Amount)),0) FROM dbo.RewardCreditLedger
WHERE SourceCreditId = @id AND Type = N'APPLIED';",
                new { id = credit.Id },
                cancellationToken: cancellationToken);
            var remaining = Math.Max(0, credit.Amount - spent);
            if (remaining <= 0) return;
            await _dapper.ExecuteAsync(@"
INSERT INTO dbo.RewardCreditLedger
(Id, UserId, Amount, Type, Currency, SourceCreditId, CreditSource, CreditSourceDetail,
 SourceType, ReferenceId, ReasonCode, Notes, CreatedAt)
VALUES
(@Id, @UserId, @Amount, N'VOIDED', @Currency, @SourceCreditId, N'REFERRAL', @Detail,
 N'BONUS', @ReferenceId, N'REFERRAL_REVERSAL', @Notes, SYSUTCDATETIME());",
                new
                {
                    Id = "crd_" + Guid.NewGuid().ToString("N"),
                    UserId = userId,
                    Amount = -remaining,
                    Currency = credit.Currency,
                    SourceCreditId = credit.Id,
                    Detail = credit.CreditSourceDetail,
                    ReferenceId = "void:" + credit.Id,
                    Notes = "Referral bonus removed because the qualifying transfer was refunded"
                },
                cancellationToken: cancellationToken);
        }

        // ------------------------------------------------------------------------------------------------
        // STAGING-SYNC: link visits / clicks / install-attribution resolve / store handoff
        // (port of referral.js recordClick / resolveTrackingId / updateClickHandoff)
        // ------------------------------------------------------------------------------------------------

        public Task<JObject> VisitAsync(JObject body, CancellationToken cancellationToken) =>
            VisitAsync(body, Text(body?["user_agent"] ?? body?["userAgent"]), cancellationToken);

        /// <summary>
        /// Records a link visit. Repeat visits by the same visitor + code inside 24h count once
        /// (same trackingId returned, counted=false). Tracking ids look like TRK-XXXXXXXX.
        /// </summary>
        public async Task<JObject> VisitAsync(JObject body, string userAgent, CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var code = CanonicalCode(Text(body?["code"] ?? body?["referralCode"] ?? body?["referral_code"]));
            if (!Regex.IsMatch(code, "^[A-Z0-9]{6,16}$"))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "INVALID_FORMAT",
                    ["message"] = "Referral codes are 6–16 letters and numbers.",
                    ["_httpStatus"] = 400
                };
            }

            // STAGING-SYNC: currency-suffixed share codes resolve to the base referrer; the visit row stores the FULL code.
            var (referrer, _) = await ResolveCodeAsync(code, cancellationToken);
            if (referrer == null)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "NOT_FOUND",
                    ["message"] = "We couldn't find this referral code. Check it or leave the field blank.",
                    ["_httpStatus"] = 404
                };
            }

            if (!IsActive(referrer)) return InactiveReferrerError(404, false);

            var visitor = (Text(body?["visitor_id"] ?? body?["visitorId"]) ?? "anonymous").Trim();
            if (visitor.Length == 0) visitor = "anonymous";
            if (visitor.Length > 64) visitor = visitor.Substring(0, 64);

            var (deviceType, operatingSystem) = DetectDevice(
                userAgent,
                Text(body?["device_type"] ?? body?["deviceType"]),
                Text(body?["operating_system"] ?? body?["operatingSystem"]));
            var sourceChannel = Limit((Text(body?["source_channel"] ?? body?["sourceChannel"]) ?? "LINK").Trim().ToUpperInvariant(), 32);
            if (sourceChannel.Length == 0) sourceChannel = "LINK";
            var storeRaw = Text(body?["store_target"] ?? body?["storeTarget"]);
            var storeTarget = string.IsNullOrWhiteSpace(storeRaw) ? null : Limit(storeRaw.Trim().ToUpperInvariant(), 32);
            var initialStatus = (storeTarget is "GOOGLE_PLAY" or "APPLE_APP_STORE") ? "STORE_REDIRECT" : "CLICKED";

            var visitCurrency = NormaliseCurrency(Text(body?["currency"])) ?? NormaliseCurrency(referrer.SendCurrency) ?? "GBP";
            var visitReceive = NormaliseCurrency(Text(body?["receive_currency"] ?? body?["receiveCurrency"]));
            var requestedRuleId = Text(body?["rule_id"] ?? body?["ruleId"]);
            if (!string.IsNullOrWhiteSpace(requestedRuleId))
            {
                requestedRuleId = requestedRuleId.Trim();
                var chosen = await _dapper.QueryFirstOrDefaultAsync<RuleRow>(@"
SELECT TOP 1 Id, Name, Status, BaseCurrency, ReceiveCurrency, ConfigJson
FROM dbo.RewardReferralRule WHERE Id = @id;",
                    new { id = requestedRuleId },
                    cancellationToken: cancellationToken);
                if (chosen != null)
                {
                    visitCurrency = NormaliseCurrency(chosen.BaseCurrency) ?? visitCurrency;
                    visitReceive = NormaliseCurrency(chosen.ReceiveCurrency) ?? visitReceive;
                }
                else
                {
                    requestedRuleId = null;
                }
            }
            else
            {
                requestedRuleId = null;
            }

            var now = DateTime.UtcNow;
            VisitRow dup = null;
            if (!string.Equals(visitor, "anonymous", StringComparison.Ordinal))
            {
                dup = await _dapper.QueryFirstOrDefaultAsync<VisitRow>(VisitSelect + @"
WHERE Code = @code AND VisitorId = @visitor AND CreatedAt >= @since
ORDER BY CreatedAt DESC;",
                    new { code, visitor, since = now.AddHours(-24) },
                    cancellationToken: cancellationToken);
            }

            var expiresAt = now.AddDays(90);
            string trackingId;
            DateTime clickedAt;
            if (dup != null)
            {
                trackingId = dup.TrackingId ?? await NewTrackingIdAsync(cancellationToken);
                clickedAt = dup.CreatedAt;
                expiresAt = dup.ExpiresAt ?? expiresAt;
                await _dapper.ExecuteAsync(@"
UPDATE dbo.RewardReferralVisit
SET TrackingId = COALESCE(TrackingId, @trackingId),
    DeviceType = COALESCE(@deviceType, DeviceType),
    OperatingSystem = COALESCE(@operatingSystem, OperatingSystem),
    StoreTarget = COALESCE(@storeTarget, StoreTarget),
    AttributionStatus = CASE WHEN AttributionStatus IN (N'LINKED', N'RESOLVED') THEN AttributionStatus ELSE @status END
WHERE Id = @id;",
                    new
                    {
                        id = dup.Id,
                        trackingId,
                        deviceType,
                        operatingSystem,
                        storeTarget,
                        status = initialStatus
                    },
                    cancellationToken: cancellationToken);
            }
            else
            {
                trackingId = await NewTrackingIdAsync(cancellationToken);
                clickedAt = now;
                await _dapper.ExecuteAsync(@"
INSERT INTO dbo.RewardReferralVisit
(Id, TrackingId, Code, ReferrerId, RuleId, Currency, ReceiveCurrency, VisitorId, DeviceType, OperatingSystem,
 SourceChannel, AttributionStatus, StoreTarget, ExpiresAt, CreatedAt)
VALUES
(@Id, @TrackingId, @Code, @ReferrerId, @RuleId, @Currency, @ReceiveCurrency, @VisitorId, @DeviceType, @OperatingSystem,
 @SourceChannel, @AttributionStatus, @StoreTarget, @ExpiresAt, @CreatedAt);",
                    new
                    {
                        Id = "vis_" + Guid.NewGuid().ToString("N"),
                        TrackingId = trackingId,
                        Code = code,
                        ReferrerId = referrer.Id,
                        RuleId = requestedRuleId,
                        Currency = visitCurrency,
                        ReceiveCurrency = visitReceive,
                        VisitorId = visitor,
                        DeviceType = deviceType,
                        OperatingSystem = operatingSystem,
                        SourceChannel = sourceChannel,
                        AttributionStatus = initialStatus,
                        StoreTarget = storeTarget,
                        ExpiresAt = expiresAt,
                        CreatedAt = clickedAt
                    },
                    cancellationToken: cancellationToken);
            }

            var playUrl = PlayStoreUrlFor(trackingId, code);
            return new JObject
            {
                ["success"] = true,
                ["counted"] = dup == null,
                ["referralCode"] = code,
                ["referrerId"] = referrer.Id,
                ["referrerFirstName"] = referrer.FirstName,
                ["trackingId"] = trackingId,
                ["tracking_id"] = trackingId,
                ["deviceType"] = deviceType,
                ["device_type"] = deviceType,
                ["operatingSystem"] = operatingSystem,
                ["operating_system"] = operatingSystem,
                ["sourceChannel"] = sourceChannel,
                ["storeTarget"] = storeTarget,
                ["clickedAt"] = clickedAt,
                ["expiresAt"] = expiresAt,
                ["status"] = initialStatus,
                ["playStoreUrl"] = playUrl,
                ["play_store_url"] = playUrl,
                ["appStoreUrl"] = IosAppStoreUrl(),
                ["app_store_url"] = IosAppStoreUrl()
            };
        }

        /// <summary>
        /// Resolves an Android Play Install Referrer tracking id (or universal-link context) to its referral
        /// code, referrer and offers. Errors carry error=INVALID_TRACKING_ID / TRACKING_NOT_FOUND /
        /// TRACKING_EXPIRED and _httpStatus=404.
        /// </summary>
        public async Task<JObject> ResolveTrackingAsync(JObject body, CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var parsed = ParseReferrerToken(Text(body?["trackingId"] ?? body?["tracking_id"] ?? body?["referrer"]));
            var channel = (Text(body?["channel"]) ?? "ANDROID").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(parsed.TrackingId))
            {
                return TrackingError("INVALID_TRACKING_ID", "Tracking ID is required to resolve install attribution.");
            }

            var click = await FindVisitByTrackingAsync(parsed.TrackingId, cancellationToken);
            if (click == null)
            {
                return TrackingError("TRACKING_NOT_FOUND",
                    "Install referral tracking ID was not found. You can enter a referral code manually.");
            }

            if (click.ExpiresAt.HasValue && click.ExpiresAt.Value < DateTime.UtcNow)
            {
                await _dapper.ExecuteAsync(@"
UPDATE dbo.RewardReferralVisit SET AttributionStatus = N'EXPIRED' WHERE Id = @id;",
                    new { id = click.Id },
                    cancellationToken: cancellationToken);
                return TrackingError("TRACKING_EXPIRED",
                    "This referral install link has expired. You can still enter a referral code manually.");
            }

            var (referrer, _) = await ResolveCodeAsync(click.Code, cancellationToken);
            if (referrer == null)
            {
                return TrackingError("NOT_FOUND",
                    "We couldn't find this referral code. Check it or leave the field blank.");
            }

            if (!IsActive(referrer))
            {
                return TrackingError("REFERRER_INACTIVE", InactiveMessage);
            }

            var nextStatus = string.Equals(click.AttributionStatus, "LINKED", StringComparison.OrdinalIgnoreCase)
                ? "LINKED"
                : "RESOLVED";
            await _dapper.ExecuteAsync(@"
UPDATE dbo.RewardReferralVisit
SET AttributionStatus = @status,
    ResolvedAt = COALESCE(ResolvedAt, SYSUTCDATETIME()),
    OperatingSystem = CASE WHEN @channel = N'ANDROID' THEN N'Android' WHEN @channel = N'IOS' THEN N'iOS' ELSE OperatingSystem END
WHERE Id = @id;",
                new { id = click.Id, status = nextStatus, channel },
                cancellationToken: cancellationToken);

            var currency = NormaliseCurrency(click.Currency) ?? NormaliseCurrency(referrer.SendCurrency) ?? "GBP";
            var receive = NormaliseCurrency(click.ReceiveCurrency);
            var live = await LoadLiveRulesAsync(cancellationToken);
            var rules = OfferRules(live, currency, receive);
            var sendCurrencies = SendCurrenciesFor(currency);
            var currencyOffers = new JArray();
            foreach (var cur in sendCurrencies)
            {
                foreach (var r in OfferRules(live, cur, receive))
                {
                    currencyOffers.Add(await BuildOfferAsync(r, null, cancellationToken));
                }
            }

            var offers = new JArray();
            foreach (var r in rules) offers.Add(await BuildOfferAsync(r, null, cancellationToken));

            return new JObject
            {
                ["valid"] = true,
                ["trackingId"] = click.TrackingId,
                ["tracking_id"] = click.TrackingId,
                ["referralCode"] = click.Code,
                ["code"] = click.Code,
                ["referrerId"] = referrer.Id,
                ["referrerFirstName"] = referrer.FirstName,
                ["referrer_first_name"] = referrer.FirstName,
                ["referralContextId"] = "CTX-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5)),
                ["channel"] = channel,
                ["ruleId"] = click.RuleId,
                ["offer"] = offers.Count == 1 ? offers[0].DeepClone() : (currencyOffers.Count > 0 ? currencyOffers[0].DeepClone() : null),
                ["offers"] = offers,
                ["send_currencies"] = new JArray(sendCurrencies),
                ["currency_offers"] = currencyOffers
            };
        }

        /// <summary>Records the store hand-off (Google Play / App Store) for a tracking id. Result: { data: {...} }.</summary>
        public async Task<JObject> HandoffAsync(string trackingId, JObject body, CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var cleanId = (trackingId ?? string.Empty).Trim().ToUpperInvariant();
            if (cleanId.Length == 0)
                return new JObject { ["error"] = "trackingId is required", ["code"] = "VALIDATION", ["_httpStatus"] = 400 };

            var row = await FindVisitByTrackingAsync(cleanId, cancellationToken);
            if (row == null)
                return new JObject { ["error"] = "Tracking ID not found.", ["code"] = "NOT_FOUND", ["_httpStatus"] = 404 };

            var storeRaw = Text(body?["store_target"] ?? body?["storeTarget"]);
            var storeTarget = !string.IsNullOrWhiteSpace(storeRaw) ? Limit(storeRaw.Trim().ToUpperInvariant(), 32) : row.StoreTarget;
            string nextStatus;
            if (string.Equals(row.AttributionStatus, "LINKED", StringComparison.OrdinalIgnoreCase))
            {
                nextStatus = "LINKED";
            }
            else
            {
                var requested = Text(body?["attribution_status"] ?? body?["status"]);
                nextStatus = !string.IsNullOrWhiteSpace(requested)
                    ? Limit(requested.Trim().ToUpperInvariant(), 32)
                    : ((storeTarget is "GOOGLE_PLAY" or "APPLE_APP_STORE") ? "STORE_REDIRECT" : (row.AttributionStatus ?? "CLICKED"));
            }

            var os = Text(body?["operating_system"] ?? body?["operatingSystem"]);
            await _dapper.ExecuteAsync(@"
UPDATE dbo.RewardReferralVisit
SET StoreTarget = @storeTarget, AttributionStatus = @status, OperatingSystem = COALESCE(@os, OperatingSystem)
WHERE Id = @id;",
                new { id = row.Id, storeTarget, status = nextStatus, os = string.IsNullOrWhiteSpace(os) ? null : Limit(os.Trim(), 32) },
                cancellationToken: cancellationToken);

            var updated = await FindVisitByTrackingAsync(cleanId, cancellationToken) ?? row;
            return new JObject
            {
                ["data"] = new JObject
                {
                    ["id"] = updated.Id,
                    ["trackingId"] = updated.TrackingId,
                    ["tracking_id"] = updated.TrackingId,
                    ["referralCode"] = updated.Code,
                    ["code"] = updated.Code,
                    ["referrer_id"] = updated.ReferrerId,
                    ["store_target"] = updated.StoreTarget,
                    ["attribution_status"] = updated.AttributionStatus,
                    ["operating_system"] = updated.OperatingSystem,
                    ["playStoreUrl"] = PlayStoreUrlFor(updated.TrackingId, updated.Code),
                    ["appStoreUrl"] = IosAppStoreUrl()
                }
            };
        }

        // ------------------------------------------------------------------------------------------------
        // STAGING-SYNC: CSV exports (RFC 4180 quoting, header row, CRLF line ends; UTF-8 via CsvBytes)
        // ------------------------------------------------------------------------------------------------

        public Task<string> TrackingCsvAsync(CancellationToken cancellationToken) =>
            TrackingCsvAsync(null, cancellationToken);

        /// <summary>
        /// Tracking export. Optional filters (all parameterised): status, status_group (pending|closed),
        /// currency, receive_currency, rule_id, from, to (yyyy-MM-dd, inclusive), q (free-text), limit (max 5000).
        /// </summary>
        public async Task<string> TrackingCsvAsync(JObject filters, CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var status = Opt(filters?["status"])?.ToUpperInvariant();
            var group = Opt(filters?["status_group"])?.ToLowerInvariant();
            var currency = NormaliseCurrency(Opt(filters?["currency"]));
            var receive = NormaliseCurrency(Opt(filters?["receive_currency"]));
            var ruleId = Opt(filters?["rule_id"]);
            var fromDate = ParseDay(Opt(filters?["from"]));
            var toDate = ParseDay(Opt(filters?["to"]));
            var q = Opt(filters?["q"]);
            var like = q == null ? null : "%" + EscapeLike(q) + "%";
            var limit = (int)Math.Min(Math.Max(Money(filters?["limit"]), 0m), 5000m);
            if (limit <= 0) limit = 5000;

            var rows = await _dapper.QueryAsync<TrackingCsvRow>(@"
SELECT TOP (@limit)
       r.Id, r.Code, r.ReferrerId, r.RefereeId, r.RuleId, r.Status, r.StatusReason, r.Currency, r.ReceiveCurrency,
       r.ReferrerCredited, r.RefereeCredited, r.QualifyingTransferId, r.RegisteredAt, r.RewardedAt, r.ConfigJson,
       rr.Name AS RuleName,
       a.FirstName AS ReferrerFirstName, a.LastName AS ReferrerLastName,
       b.FirstName AS RefereeFirstName, b.LastName AS RefereeLastName,
       (SELECT TOP 1 v.TrackingId FROM dbo.RewardReferralVisit v
         WHERE v.LinkedReferralId = r.Id ORDER BY v.CreatedAt DESC) AS TrackingId,
       (SELECT TOP 1 v.OperatingSystem FROM dbo.RewardReferralVisit v
         WHERE v.LinkedReferralId = r.Id ORDER BY v.CreatedAt DESC) AS VisitOs
FROM dbo.RewardReferral r
LEFT JOIN dbo.RewardCustomer a ON a.Id = r.ReferrerId
LEFT JOIN dbo.RewardCustomer b ON b.Id = r.RefereeId
LEFT JOIN dbo.RewardReferralRule rr ON rr.Id = r.RuleId
WHERE (@status IS NULL OR r.Status = @status)
  AND (@statusGroup IS NULL
       OR (@statusGroup = N'pending' AND r.Status IN (N'REGISTERED', N'PENDING'))
       OR (@statusGroup = N'closed' AND r.Status IN (N'EXPIRED', N'NOT_ELIGIBLE', N'REVERSED')))
  AND (@currency IS NULL OR r.Currency = @currency)
  AND (@receive IS NULL OR r.ReceiveCurrency = @receive)
  AND (@ruleId IS NULL OR r.RuleId = @ruleId)
  AND (@fromDate IS NULL OR r.RegisteredAt >= @fromDate)
  AND (@toExclusive IS NULL OR r.RegisteredAt < @toExclusive)
  AND (@likeText IS NULL
       OR r.Id LIKE @likeText OR r.Code LIKE @likeText OR a.Id LIKE @likeText OR b.Id LIKE @likeText
       OR a.Email LIKE @likeText OR b.Email LIKE @likeText
       OR (ISNULL(a.FirstName, N'') + N' ' + ISNULL(a.LastName, N'')) LIKE @likeText
       OR (ISNULL(b.FirstName, N'') + N' ' + ISNULL(b.LastName, N'')) LIKE @likeText
       OR EXISTS (SELECT 1 FROM dbo.RewardReferralVisit v WHERE v.LinkedReferralId = r.Id AND v.TrackingId LIKE @likeText))
ORDER BY r.RegisteredAt DESC;",
                new
                {
                    limit,
                    status,
                    statusGroup = group,
                    currency,
                    receive,
                    ruleId,
                    fromDate,
                    toExclusive = toDate?.AddDays(1),
                    likeText = like
                },
                cancellationToken: cancellationToken);

            var columns = new (string Header, Func<TrackingCsvRow, object> Value)[]
            {
                ("Referral ID", r => r.Id),
                ("Tracking ID", r => r.TrackingId),
                ("Channel", r => ChannelFromOs(r.VisitOs)),
                ("Attribution Method", r => string.IsNullOrEmpty(r.TrackingId) ? "MANUAL_CODE" : "DIRECT_LINK"),
                ("Referrer ID", r => r.ReferrerId),
                ("Referrer", r => (r.ReferrerFirstName + " " + r.ReferrerLastName).Trim()),
                ("Referee ID", r => r.RefereeId),
                ("Referee", r => (r.RefereeFirstName + " " + r.RefereeLastName).Trim()),
                ("Rule", r => r.RuleName),
                ("Send Currency", r => r.Currency),
                ("Receive Currency", r => r.ReceiveCurrency),
                ("Registered On", r => r.RegisteredAt),
                ("Qualification Deadline", r => QualificationDeadlineDay(r.RegisteredAt, r.ConfigJson).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("Qualifying Transfer ID", r => r.QualifyingTransferId),
                ("Status", r => r.Status),
                ("Reason", r => r.StatusReason),
                ("Referrer Bonus", r => r.ReferrerCredited),
                ("Referee Bonus", r => r.RefereeCredited),
                ("Rewarded On", r => r.RewardedAt)
            };
            return ToCsv(columns, (rows ?? Array.Empty<TrackingCsvRow>()).ToList());
        }

        public Task<string> PerformanceCsvAsync(CancellationToken cancellationToken) =>
            PerformanceCsvAsync(null, cancellationToken);

        /// <summary>Per-rule performance export. Optional args: from, to (yyyy-MM-dd), include_archived (1|true).</summary>
        public async Task<string> PerformanceCsvAsync(JObject args, CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var fromText = Opt(args?["from"]);
            var toText = Opt(args?["to"]);
            var fromDate = ParseDay(fromText);
            var toExclusive = ParseDay(toText)?.AddDays(1);
            var includeArchived = Opt(args?["include_archived"]) is "1" or "true" or "True" or "TRUE";

            var rules = (await _dapper.QueryAsync<RuleRow>(@"
SELECT Id, Name, Status, BaseCurrency, ReceiveCurrency, ConfigJson
FROM dbo.RewardReferralRule
ORDER BY BaseCurrency, ReceiveCurrency, Name;",
                new { },
                cancellationToken: cancellationToken) ?? Array.Empty<RuleRow>()).ToList();

            var today = UkToday();
            var rows = new List<PerformanceRow>();
            foreach (var rule in rules)
            {
                if (!includeArchived && IsArchived(rule)) continue;

                var counts = (await _dapper.QueryAsync<StatusCountRow>(@"
SELECT Status, COUNT(1) AS Total FROM dbo.RewardReferral
WHERE RuleId = @ruleId
  AND (@fromDate IS NULL OR RegisteredAt >= @fromDate)
  AND (@toExclusive IS NULL OR RegisteredAt < @toExclusive)
GROUP BY Status;",
                    new { ruleId = rule.Id, fromDate, toExclusive },
                    cancellationToken: cancellationToken) ?? Array.Empty<StatusCountRow>()).ToList();
                int Cnt(params string[] statuses) => counts
                    .Where(c => statuses.Contains((c.Status ?? string.Empty).ToUpperInvariant()))
                    .Sum(c => c.Total);

                var ruleReceive = RuleReceive(rule);
                // A visit that named a destination counts for that corridor only; one that did not
                // counts for every corridor of the send currency (same as the SQLite reference).
                var visits = await _dapper.ExecuteScalarAsync<int>(@"
SELECT COUNT(1) FROM dbo.RewardReferralVisit
WHERE Currency = @currency
  AND (@receive IS NULL OR ReceiveCurrency IS NULL OR ReceiveCurrency = @receive)
  AND (@fromDate IS NULL OR CreatedAt >= @fromDate)
  AND (@toExclusive IS NULL OR CreatedAt < @toExclusive);",
                    new { currency = rule.BaseCurrency, receive = ruleReceive, fromDate, toExclusive },
                    cancellationToken: cancellationToken);

                var registrations = Cnt("REGISTERED", "PENDING", "REWARDED", "EXPIRED", "NOT_ELIGIBLE", "REVERSED");
                var rewarded = Cnt("REWARDED");

                // Bonus figures come from the credit ledger rows that belong to this rule's referrals.
                var issued = await _dapper.ExecuteScalarAsync<decimal>(@"
SELECT ISNULL(SUM(l.Amount), 0) FROM dbo.RewardCreditLedger l
WHERE l.Type = N'EARNED'
  AND l.ReferralId IN (SELECT r.Id FROM dbo.RewardReferral r
                       WHERE r.RuleId = @ruleId
                         AND (@fromDate IS NULL OR r.RegisteredAt >= @fromDate)
                         AND (@toExclusive IS NULL OR r.RegisteredAt < @toExclusive));",
                    new { ruleId = rule.Id, fromDate, toExclusive },
                    cancellationToken: cancellationToken);
                var movements = (await _dapper.QueryAsync<LedgerTotalRow>(@"
SELECT c.Type AS Type, ISNULL(SUM(ABS(c.Amount)), 0) AS Total
FROM dbo.RewardCreditLedger c
INNER JOIN dbo.RewardCreditLedger e ON e.Id = c.SourceCreditId AND e.Type = N'EARNED'
WHERE e.ReferralId IN (SELECT r.Id FROM dbo.RewardReferral r
                       WHERE r.RuleId = @ruleId
                         AND (@fromDate IS NULL OR r.RegisteredAt >= @fromDate)
                         AND (@toExclusive IS NULL OR r.RegisteredAt < @toExclusive))
GROUP BY c.Type;",
                    new { ruleId = rule.Id, fromDate, toExclusive },
                    cancellationToken: cancellationToken) ?? Array.Empty<LedgerTotalRow>()).ToList();
                decimal Moved(params string[] types) => movements
                    .Where(m => types.Contains((m.Type ?? string.Empty).ToUpperInvariant()))
                    .Sum(m => m.Total);
                var used = Moved("APPLIED");
                var returned = Moved("RETURNED", "RETURN", "REFUNDED");
                var expired = Moved("EXPIRED") + Moved("VOIDED");

                var volume = await _dapper.ExecuteScalarAsync<decimal>(@"
SELECT ISNULL(SUM(t.Amount), 0)
FROM dbo.RewardTransferFact t
INNER JOIN dbo.RewardReferral r ON r.RefereeId = t.UserId
WHERE r.RuleId = @ruleId
  AND t.Status IN (N'COMPLETED', N'SUCCESS', N'SETTLED', N'PROCESSED')
  AND t.Currency = r.Currency
  AND (r.ReceiveCurrency IS NULL OR t.ReceiveCurrency = r.ReceiveCurrency)
  AND (@fromDate IS NULL OR r.RegisteredAt >= @fromDate)
  AND (@toExclusive IS NULL OR r.RegisteredAt < @toExclusive);",
                    new { ruleId = rule.Id, fromDate, toExclusive },
                    cancellationToken: cancellationToken);

                rows.Add(new PerformanceRow
                {
                    Name = rule.Name,
                    Currency = rule.BaseCurrency,
                    ReceiveCurrency = ruleReceive,
                    Status = RuleStatus(rule, today),
                    LinkVisits = visits,
                    Registrations = registrations,
                    Pending = Cnt("REGISTERED", "PENDING"),
                    Rewarded = rewarded,
                    Closed = Cnt("EXPIRED", "NOT_ELIGIBLE", "REVERSED"),
                    ConversionRate = registrations > 0
                        ? Math.Round((decimal)rewarded / registrations * 100m, 1)
                        : (decimal?)null,
                    BonusIssued = issued,
                    BonusUsed = used - returned,
                    BonusUnused = issued + returned - used - expired,
                    BonusExpired = expired,
                    ReferredVolume = volume
                });
            }

            var rank = new Dictionary<string, int>
            {
                ["ACTIVE"] = 0, ["SCHEDULED"] = 1, ["INACTIVE"] = 2, ["ENDED"] = 3, ["ARCHIVED"] = 4
            };
            var ordered = rows
                .OrderBy(r => rank.TryGetValue(r.Status ?? string.Empty, out var n) ? n : 5)
                .ToList();

            var columns = new (string Header, Func<PerformanceRow, object> Value)[]
            {
                ("Rule", r => r.Name),
                ("Send Currency", r => r.Currency),
                ("Receive Currency", r => r.ReceiveCurrency),
                ("Status", r => r.Status),
                ("Link visits", r => r.LinkVisits),
                ("Registrations", r => r.Registrations),
                ("Pending", r => r.Pending),
                ("Rewarded", r => r.Rewarded),
                ("Expired / Not eligible", r => r.Closed),
                ("Conversion rate %", r => r.ConversionRate),
                ("Bonus issued", r => r.BonusIssued),
                ("Bonus used", r => r.BonusUsed),
                ("Bonus unused", r => r.BonusUnused),
                ("Bonus expired", r => r.BonusExpired),
                ("Referred volume", r => r.ReferredVolume),
                ("From", r => fromText ?? string.Empty),
                ("To", r => toText ?? string.Empty)
            };
            return ToCsv(columns, ordered);
        }

        /// <summary>UTF-8 bytes of a CSV string, with a BOM so spreadsheet apps detect the encoding.</summary>
        public static byte[] CsvBytes(string csv)
        {
            var utf8 = new UTF8Encoding(true);
            var preamble = utf8.GetPreamble();
            var body = utf8.GetBytes(csv ?? string.Empty);
            var result = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
            return result;
        }

        /// <summary>RFC 4180 field quoting: quote when the value has a comma, quote, CR or LF; double embedded quotes.</summary>
        public static string CsvEscape(object value)
        {
            string s;
            if (value == null) s = string.Empty;
            else if (value is DateTime dt) s = FormatUtc(dt);
            else if (value is DateTimeOffset dto) s = dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            else if (value is IFormattable f) s = f.ToString(null, CultureInfo.InvariantCulture);
            else s = value.ToString() ?? string.Empty;

            return s.IndexOfAny(new[] { '"', ',', '\r', '\n' }) >= 0
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
        }

        private static string ToCsv<T>((string Header, Func<T, object> Value)[] columns, IEnumerable<T> rows)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(",", columns.Select(c => CsvEscape(c.Header)))).Append("\r\n");
            foreach (var row in rows)
            {
                sb.Append(string.Join(",", columns.Select(c => CsvEscape(c.Value(row))))).Append("\r\n");
            }

            return sb.ToString();
        }

        private static string FormatUtc(DateTime value)
        {
            var utc = AsUtc(value);
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        private static string ChannelFromOs(string os)
        {
            if (string.Equals(os, "Android", StringComparison.OrdinalIgnoreCase)) return "ANDROID";
            if (string.Equals(os, "iOS", StringComparison.OrdinalIgnoreCase)) return "IOS";
            return "WEB";
        }

        // ------------------------------------------------------------------------------------------------
        // STAGING-SYNC: qualification window, caps, expiry sweep
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Closes REGISTERED / PENDING referrals whose qualification window has passed (the referral.js daily
        /// job). Safe to call from a scheduler; returns { expired: n }.
        /// </summary>
        public async Task<JObject> ExpireOverdueAsync(CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var rows = await _dapper.QueryAsync<ReferralRow>(@"
SELECT Id, Code, ReferrerId, RefereeId, RuleId, Status, StatusReason, Currency, ReceiveCurrency,
       ReferrerReward, RefereeReward, ReferrerCredited, RefereeCredited, QualifyingTransferId,
       QualifyingAmount, RegisteredAt, RewardedAt, ConfigJson
FROM dbo.RewardReferral
WHERE Status IN (N'REGISTERED', N'PENDING');",
                new { },
                cancellationToken: cancellationToken);
            var now = DateTime.UtcNow;
            var expired = 0;
            foreach (var row in rows ?? Array.Empty<ReferralRow>())
            {
                if (!IsPastQualificationWindow(row, now)) continue;
                await ExpireReferralAsync(row.Id, "Qualification window ended", cancellationToken);
                expired++;
            }

            return new JObject { ["expired"] = expired };
        }

        private async Task ExpireReferralAsync(string referralId, string reason, CancellationToken cancellationToken)
        {
            await _dapper.ExecuteAsync(@"
UPDATE dbo.RewardReferral SET Status = N'EXPIRED', StatusReason = @reason
WHERE Id = @id AND Status IN (N'REGISTERED', N'PENDING');",
                new { id = referralId, reason },
                cancellationToken: cancellationToken);
        }

        private static int QualificationWindowDays(string configJson)
        {
            var cfg = Parse(configJson);
            var days = (int)Money(cfg["qualification_window_days"] ?? cfg["qualificationWindowDays"]);
            return days <= 0 ? 30 : days;
        }

        // Deadline is a UK calendar day: registration day + qualification_window_days (same as referral.js).
        private static DateTime QualificationDeadlineDay(DateTime registeredAt, string configJson) =>
            UkDay(registeredAt).AddDays(QualificationWindowDays(configJson));

        private static bool IsPastQualificationWindow(ReferralRow referral, DateTime eventUtc) =>
            UkDay(eventUtc) > QualificationDeadlineDay(referral.RegisteredAt, referral.ConfigJson);

        private static string RewardType(JObject cfg) =>
            (Text(cfg["reward_type"] ?? cfg["rewardType"]) ?? "BOTH").Trim().ToUpperInvariant();

        private async Task<bool> IsReferrerCapReachedAsync(
            string referrerId,
            string ruleId,
            JObject cfg,
            decimal nextRewardAmount,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ruleId) || string.IsNullOrWhiteSpace(referrerId)) return false;
            var capType = (Text(cfg["max_referrals_cap_type"] ?? cfg["maxReferralsCapType"]) ?? "COUNT").Trim().ToUpperInvariant();
            if (capType == "AMOUNT")
            {
                var capAmt = Money(cfg["max_referrals_amount_per_referrer"] ?? cfg["maxReferralsAmountPerReferrer"]);
                if (capAmt <= 0) return false;
                var earned = await _dapper.ExecuteScalarAsync<decimal>(@"
SELECT ISNULL(SUM(ReferrerCredited), 0) FROM dbo.RewardReferral
WHERE ReferrerId = @referrerId AND RuleId = @ruleId AND ReferrerCredited > 0;",
                    new { referrerId, ruleId },
                    cancellationToken: cancellationToken);
                return earned >= capAmt
                    || (nextRewardAmount > 0 && Math.Round(earned + nextRewardAmount, 2) > capAmt);
            }

            var capCnt = (int)Money(cfg["max_referrals_per_referrer"] ?? cfg["maxReferralsPerReferrer"]);
            if (capCnt <= 0) return false;
            var rewarded = await _dapper.ExecuteScalarAsync<int>(@"
SELECT COUNT(1) FROM dbo.RewardReferral
WHERE ReferrerId = @referrerId AND RuleId = @ruleId AND ReferrerCredited > 0;",
                new { referrerId, ruleId },
                cancellationToken: cancellationToken);
            return rewarded >= capCnt;
        }

        private async Task<decimal> SchemeSpendAsync(string ruleId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(ruleId)) return 0m;
            return await _dapper.ExecuteScalarAsync<decimal>(@"
SELECT ISNULL(SUM(ISNULL(ReferrerCredited, 0) + ISNULL(RefereeCredited, 0)), 0)
FROM dbo.RewardReferral
WHERE RuleId = @ruleId AND Status <> N'REVERSED';",
                new { ruleId },
                cancellationToken: cancellationToken);
        }

        // Why the referrer must NOT be paid right now (null = payable). The friend's reward is unaffected.
        private async Task<string> ReferrerBlockReasonAsync(ReferralRow referral, CancellationToken cancellationToken)
        {
            var referrer = await GetCustomerAsync(referral.ReferrerId, cancellationToken);
            if (referrer != null && !IsActive(referrer)) return "Referrer account not active";

            var cfg = Parse(referral.ConfigJson);
            if (RewardType(cfg) == "REFEREE") return null;
            var reward = referral.ReferrerReward ?? 0;
            var budget = Money(cfg["max_scheme_budget"] ?? cfg["maxSchemeBudget"]);
            if (budget > 0 && Math.Round(await SchemeSpendAsync(referral.RuleId, cancellationToken) + reward, 2) > budget)
                return "Scheme maximum budget cap reached";
            if (await IsReferrerCapReachedAsync(referral.ReferrerId, referral.RuleId, cfg, reward, cancellationToken))
                return "Referrer cap reached";
            return null;
        }

        private static DateTime EventTimeUtc(JObject body)
        {
            var token = body?["completed_at"] ?? body?["completedAt"] ?? body?["event_at"]
                        ?? body?["transfer_date"] ?? body?["created_at"];
            if (token != null && token.Type == JTokenType.Date) return AsUtc(token.Value<DateTime>());
            var raw = Text(token);
            if (!string.IsNullOrWhiteSpace(raw)
                && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.UtcDateTime;
            }

            return DateTime.UtcNow;
        }

        // ------------------------------------------------------------------------------------------------
        // original helpers (CreditRefereeWelcomeAsync gains Floor / ReceiveCurrency stamping)
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The new customer can use their welcome bonus as soon as they join.
        /// The person who shared the link is still paid when the transfer completes.
        /// </summary>
        private async Task CreditRefereeWelcomeAsync(ReferralRow referral, CancellationToken cancellationToken)
        {
            if (referral == null) return;
            if ((referral.RefereeCredited ?? 0) > 0 || (referral.RefereeReward ?? 0) <= 0) return;
            var status = (referral.Status ?? string.Empty).ToUpperInvariant();
            if (status is "REWARDED" or "REVERSED" or "EXPIRED" or "NOT_ELIGIBLE") return;

            var validity = (int)Money(Parse(referral.ConfigJson)["bonus_validity_days"] ?? Parse(referral.ConfigJson)["validity_days"]);
            if (validity <= 0) validity = 90;
            await _wallet.IssueCreditAsync(
                referral.RefereeId,
                referral.RefereeReward.Value,
                referral.Currency ?? "GBP",
                validity,
                "REFERRAL",
                "REFEREE",
                referral.Id,
                referral.RuleId,
                null,
                referral.Id + ":referee",
                "Welcome bonus",
                "System",
                cancellationToken);
            // STAGING-SYNC: remember the rule's Floor + corridor on the credit so the wallet can enforce them.
            await StampCreditFloorAsync(referral, cancellationToken);
            referral.RefereeCredited = referral.RefereeReward;
            await _dapper.ExecuteAsync(@"
UPDATE dbo.RewardReferral
SET RefereeCredited = @amount
WHERE Id = @id AND (RefereeCredited IS NULL OR RefereeCredited = 0);",
                new { id = referral.Id, amount = referral.RefereeReward.Value },
                cancellationToken: cancellationToken);
        }

        // STAGING-SYNC: dbo.RewardCreditLedger.[Floor] DECIMAL(18,2) + ReceiveCurrency CHAR(3) on the referee credit.
        private async Task StampCreditFloorAsync(ReferralRow referral, CancellationToken cancellationToken)
        {
            var cfg = Parse(referral.ConfigJson);
            var floor = Money(cfg["min_transaction_threshold"] ?? cfg["min_amount"] ?? cfg["minimum"]);
            var receive = NormaliseCurrency(referral.ReceiveCurrency) ?? NormaliseCurrency(Text(cfg["receive_currency"]));
            if (floor <= 0 && receive == null) return;
            await _dapper.ExecuteAsync(@"
UPDATE dbo.RewardCreditLedger
SET [Floor] = @floor, ReceiveCurrency = @receive
WHERE ReferenceId = @referenceId AND Type = N'EARNED';",
                new
                {
                    floor = floor > 0 ? Math.Round(floor, 2) : (decimal?)null,
                    receive,
                    referenceId = referral.Id + ":referee"
                },
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// The qualifying transfer can finish before the referral row is saved.
        /// Credit both sides as soon as that completed transfer is already there.
        /// </summary>
        private async Task AwardExistingTransferAsync(ReferralRow referral, CancellationToken cancellationToken)
        {
            if (referral == null) return;
            var status = (referral.Status ?? string.Empty).ToUpperInvariant();
            if (status is not ("REGISTERED" or "PENDING")) return;

            QualifyingTransferRow transfer;
            try
            {
                transfer = await _dapper.QueryFirstOrDefaultAsync<QualifyingTransferRow>(@"
SELECT TOP 1 rt.RetailTransactionNumber,
       CASE WHEN ISNULL(rt.TransactionValue, 0) > ISNULL(rt.SendAmount, 0)
            THEN rt.TransactionValue ELSE rt.SendAmount END AS SendAmount,
       rt.SendCurrency
FROM dbo.RetailTransaction rt
INNER JOIN dbo.RetailTransactionStatus s
    ON s.RetailTransactionStatusId = rt.RetailTransactionStatusId
WHERE rt.UserId = CONVERT(uniqueidentifier, @userId)
  AND s.RetailTransactionStatusShortcode = N'PROCESSED'
ORDER BY CASE WHEN ISNULL(rt.TransactionValue, 0) > ISNULL(rt.SendAmount, 0)
              THEN rt.TransactionValue ELSE rt.SendAmount END DESC;",
                    new { userId = referral.RefereeId },
                    cancellationToken: cancellationToken);
            }
            catch
            {
                return;
            }

            if (transfer == null || transfer.SendAmount <= 0) return;
            var cfg = Parse(referral.ConfigJson);
            var minAmount = Money(cfg["min_transaction_threshold"] ?? cfg["min_amount"] ?? cfg["minimum"]);
            if (minAmount > 0 && transfer.SendAmount < minAmount) return;

            await CreditReferralAsync(
                referral,
                transfer.RetailTransactionNumber.ToString(CultureInfo.InvariantCulture),
                transfer.SendAmount,
                string.IsNullOrWhiteSpace(transfer.SendCurrency) ? referral.Currency ?? "GBP" : transfer.SendCurrency,
                new JArray(),
                cancellationToken);
        }

        /// <summary>
        /// You Send is stored on TransactionValue. SendAmount is what was left to pay
        /// after promo and bonus, so it is the wrong figure for the referral minimum.
        /// </summary>
        private async Task<decimal> QualifyingSendAsync(
            string userId,
            string transferId,
            decimal reported,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(transferId) || transferId.Any(ch => ch < '0' || ch > '9'))
            {
                return reported;
            }

            try
            {
                var original = await _dapper.ExecuteScalarAsync<decimal>(@"
SELECT TOP 1
    CASE WHEN ISNULL(rt.TransactionValue, 0) > ISNULL(rt.SendAmount, 0)
         THEN rt.TransactionValue ELSE rt.SendAmount END
FROM dbo.RetailTransaction rt
WHERE rt.RetailTransactionNumber = CONVERT(int, @transferId)
  AND rt.UserId = CONVERT(uniqueidentifier, @userId);",
                    new { transferId, userId },
                    cancellationToken: cancellationToken);
                return original > reported ? original : reported;
            }
            catch
            {
                return reported;
            }
        }

        private sealed class QualifyingTransferRow
        {
            public int RetailTransactionNumber { get; set; }
            public decimal SendAmount { get; set; }
            public string SendCurrency { get; set; }
        }

        private async Task<CustomerRow> GetCustomerAsync(string id, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return await _dapper.QueryFirstOrDefaultAsync<CustomerRow>(@"
SELECT Id, FirstName, LastName, Email, SendCurrency, AccountStatus, KycStatus, ReferralCode, CreatedAt, UpdatedAt
FROM dbo.RewardCustomer WHERE Id = @id;",
                new { id },
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Currency suffixes recognised on share codes ({BASE}{CUR}); the order is exact (prototype SUPPORTED_CURRENCIES).
        /// </summary>
        public static readonly string[] SupportedCurrencies =
            { "GBP", "USD", "EUR", "NGN", "CAD", "AUD", "JPY", "CNY", "INR", "ZAR", "KES", "GHS", "AED" };

        /// <summary>Canonical referral code: trim, upper-case, every non [A-Z0-9] char removed. Valid = ^[A-Z0-9]{6,16}$.</summary>
        public static string CanonicalCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var sb = new StringBuilder(raw.Length);
            foreach (var ch in raw.Trim().ToUpperInvariant())
            {
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            }

            return sb.ToString();
        }

        // STAGING-SYNC: exact ReferralCode match first; otherwise, when the code is at least 9 chars and ends with a
        // supported currency, look up the base code (code minus the 3-letter suffix) and report that suffix as codeCurrency.
        // (port of referral.js lookupCode / parseCurrencyCodeSuffix)
        private async Task<(CustomerRow Referrer, string CodeCurrency)> ResolveCodeAsync(string code, CancellationToken cancellationToken)
        {
            var clean = CanonicalCode(code);
            if (clean.Length == 0) return (null, null);
            var exact = await FindReferrerByCodeAsync(clean, cancellationToken);
            if (exact != null) return (exact, null);
            if (clean.Length >= 9)
            {
                foreach (var cur in SupportedCurrencies)
                {
                    if (!clean.EndsWith(cur, StringComparison.Ordinal)) continue;
                    var baseReferrer = await FindReferrerByCodeAsync(clean[..^cur.Length], cancellationToken);
                    return baseReferrer != null ? (baseReferrer, cur) : (null, null);
                }
            }

            return (null, null);
        }

        private Task<CustomerRow> FindReferrerByCodeAsync(string code, CancellationToken cancellationToken) =>
            _dapper.QueryFirstOrDefaultAsync<CustomerRow>(@"
SELECT Id, FirstName, LastName, Email, SendCurrency, AccountStatus, KycStatus, ReferralCode, CreatedAt, UpdatedAt
FROM dbo.RewardCustomer WHERE ReferralCode = @code;",
                new { code },
                cancellationToken: cancellationToken);

        // ------------------------------------------------------------------------------------------------
        // STAGING-SYNC: rule priority
        //   * only ACTIVE rules inside their start_date / end_date window (UK calendar day) are live
        //   * a ReceiveCurrency (corridor) rule beats a generic rule for the same send currency
        //   * unchanged legacy fallback: when nothing exists for the send currency, any live rule
        // ------------------------------------------------------------------------------------------------

        private async Task<List<RuleRow>> LoadLiveRulesAsync(CancellationToken cancellationToken)
        {
            var rows = await _dapper.QueryAsync<RuleRow>(@"
SELECT Id, Name, Status, BaseCurrency, ReceiveCurrency, ConfigJson
FROM dbo.RewardReferralRule
WHERE Status = N'ACTIVE'
ORDER BY UpdatedAt DESC;",
                new { },
                cancellationToken: cancellationToken);
            var today = UkToday();
            return (rows ?? Array.Empty<RuleRow>()).Where(r => RuleStatus(r, today) == "ACTIVE").ToList();
        }

        private static RuleRow PickRule(List<RuleRow> live, string currency, string receive)
        {
            var cur = NormaliseCurrency(currency) ?? "GBP";
            var forCurrency = live.Where(r => string.Equals(r.BaseCurrency?.Trim(), cur, StringComparison.OrdinalIgnoreCase)).ToList();
            if (forCurrency.Count == 0) return live.FirstOrDefault();

            if (receive != null)
            {
                var corridor = forCurrency.FirstOrDefault(r => string.Equals(RuleReceive(r), receive, StringComparison.OrdinalIgnoreCase));
                if (corridor != null) return corridor;
                return forCurrency.FirstOrDefault(r => RuleReceive(r) == null);
            }

            return forCurrency.FirstOrDefault(r => RuleReceive(r) == null) ?? forCurrency.First();
        }

        // Rules to show for a send currency: the exact corridor (else the generic rule) when the destination
        // is known, otherwise every live rule for that send currency.
        private static List<RuleRow> OfferRules(List<RuleRow> live, string currency, string receive)
        {
            var cur = NormaliseCurrency(currency) ?? "GBP";
            var forCurrency = live.Where(r => string.Equals(r.BaseCurrency?.Trim(), cur, StringComparison.OrdinalIgnoreCase)).ToList();
            if (receive == null) return forCurrency;
            var rule = forCurrency.FirstOrDefault(r => string.Equals(RuleReceive(r), receive, StringComparison.OrdinalIgnoreCase))
                       ?? forCurrency.FirstOrDefault(r => RuleReceive(r) == null);
            return rule == null ? new List<RuleRow>() : new List<RuleRow> { rule };
        }

        private static string RuleReceive(RuleRow rule) =>
            NormaliseCurrency(rule.ReceiveCurrency) ?? NormaliseCurrency(Text(rule.Cfg["receive_currency"] ?? rule.Cfg["receiveCurrency"]));

        private static bool IsArchived(RuleRow rule)
        {
            var token = rule.Cfg["is_archived"] ?? rule.Cfg["archived"];
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            var s = token.ToString();
            return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string RuleStatus(RuleRow rule, DateTime today)
        {
            if (IsArchived(rule)) return "ARCHIVED";
            var end = ParseDay(Text(rule.Cfg["end_date"] ?? rule.Cfg["endDate"]));
            if (end.HasValue && end.Value.Date < today.Date) return "ENDED";
            if (!string.Equals(rule.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase)) return "INACTIVE";
            var start = ParseDay(Text(rule.Cfg["start_date"] ?? rule.Cfg["startDate"]));
            if (start.HasValue && start.Value.Date > today.Date) return "SCHEDULED";
            return "ACTIVE";
        }

        private async Task<JObject> BuildOfferAsync(RuleRow rule, CustomerRow customer, CancellationToken cancellationToken)
        {
            var cfg = Parse(rule.ConfigJson);
            var referrerAmt = Money(cfg["referrer_reward"] ?? cfg["referrerReward"] ?? cfg["referrer_bonus"]);
            var refereeAmt = Money(cfg["referee_reward"] ?? cfg["refereeReward"] ?? cfg["referee_bonus"]);
            var floor = Money(cfg["min_transaction_threshold"] ?? cfg["min_amount"] ?? cfg["minimum"]);
            var windowDays = (int)Money(cfg["qualification_window_days"] ?? cfg["qualificationWindowDays"]);
            if (windowDays <= 0) windowDays = 30;
            var text = Text(cfg["offer_text"] ?? cfg["offerText"]);
            if (string.IsNullOrWhiteSpace(text))
            {
                text = referrerAmt > 0
                    ? $"Invite a friend and earn {referrerAmt:0.00} {rule.BaseCurrency} bonus credit when they complete their first qualifying transfer."
                    : $"Invite a friend — they can earn {refereeAmt:0.00} {rule.BaseCurrency} bonus credit on their first qualifying transfer.";
            }

            text = text.Replace("referral credit", "bonus credit", StringComparison.OrdinalIgnoreCase);

            // STAGING-SYNC: cap_reached for COUNT / AMOUNT referrer caps and the scheme budget.
            var validityDays = (int)Money(cfg["bonus_validity_days"] ?? cfg["validity_days"]);
            if (validityDays <= 0) validityDays = 90;
            var receive = RuleReceive(rule);
            var rewardType = RewardType(cfg);
            var capType = (Text(cfg["max_referrals_cap_type"] ?? cfg["maxReferralsCapType"]) ?? "COUNT").Trim().ToUpperInvariant();
            var budget = Money(cfg["max_scheme_budget"] ?? cfg["maxSchemeBudget"]);
            var schemeSpend = budget > 0 ? await SchemeSpendAsync(rule.Id, cancellationToken) : 0m;
            var schemeCapHit = budget > 0 && schemeSpend >= budget;
            var referrerCapHit = false;
            if (customer != null && rewardType != "REFEREE")
            {
                referrerCapHit = await IsReferrerCapReachedAsync(customer.Id, rule.Id, cfg, referrerAmt, cancellationToken);
            }

            return new JObject
            {
                ["text"] = text,
                ["referral_code"] = customer?.ReferralCode,
                ["cap_reached"] = customer != null && (referrerCapHit || schemeCapHit),
                ["rule_id"] = rule.Id,
                ["referee_reward"] = refereeAmt,
                ["referrer_reward"] = referrerAmt,
                ["currency"] = rule.BaseCurrency,
                ["floor"] = floor,
                ["qualification_window_days"] = windowDays,
                // additive keys
                ["name"] = rule.Name,
                ["receive_currency"] = receive,
                ["corridor"] = receive != null
                    ? rule.BaseCurrency + " \u2192 " + receive
                    : rule.BaseCurrency + " (all destinations)",
                ["reward_type"] = rewardType,
                ["bonus_validity_days"] = validityDays,
                ["max_referrals_cap_type"] = capType,
                ["max_referrals_per_referrer"] = Positive(cfg["max_referrals_per_referrer"] ?? cfg["maxReferralsPerReferrer"]),
                ["max_referrals_amount_per_referrer"] = Positive(cfg["max_referrals_amount_per_referrer"] ?? cfg["maxReferralsAmountPerReferrer"]),
                ["max_scheme_budget"] = Positive(cfg["max_scheme_budget"] ?? cfg["maxSchemeBudget"]),
                ["start_date"] = Text(cfg["start_date"] ?? cfg["startDate"]),
                ["end_date"] = Text(cfg["end_date"] ?? cfg["endDate"]),
                ["scheme_cap_reached"] = customer != null && schemeCapHit,
                ["scheme_spend"] = customer != null ? schemeSpend : 0m
            };
        }

        private static JToken Positive(JToken token)
        {
            var n = Money(token);
            return n > 0 ? new JValue(n) : JValue.CreateNull();
        }

        private static JObject InactiveReferrerError(int httpStatus, bool includeValid)
        {
            var result = new JObject
            {
                ["error"] = InactiveMessage,
                ["reason"] = "REFERRER_INACTIVE",
                ["error_code"] = "REFERRER_INACTIVE",
                ["message"] = InactiveMessage,
                ["_httpStatus"] = httpStatus
            };
            if (includeValid) result["valid"] = false;
            return result;
        }

        private const string InactiveMessage = "This referral link is no longer active. You can still sign up.";

        // Blank AccountStatus (legacy rows written outside UpsertCustomerAsync) is treated as ACTIVE.
        private static bool IsActive(CustomerRow customer) =>
            string.IsNullOrWhiteSpace(customer?.AccountStatus)
            || string.Equals(customer.AccountStatus.Trim(), "ACTIVE", StringComparison.OrdinalIgnoreCase);

        private static string[] SendCurrenciesFor(string primary)
        {
            var p = NormaliseCurrency(primary) ?? "GBP";
            return new[] { p, "GBP", "USD", "EUR" }.Distinct().ToArray();
        }

        private static JObject TrackingError(string error, string message) =>
            new JObject { ["valid"] = false, ["error"] = error, ["message"] = message, ["_httpStatus"] = 404 };

        private async Task<VisitRow> FindClickAsync(JObject body, JObject referee, string code, CancellationToken cancellationToken)
        {
            var rawTracking = Text(
                body?["tracking_id"] ?? body?["trackingId"]
                ?? referee?["tracking_id"] ?? referee?["trackingId"]) ?? string.Empty;
            var parsed = ParseReferrerToken(rawTracking);
            if (!string.IsNullOrEmpty(parsed.TrackingId))
            {
                return await FindVisitByTrackingAsync(parsed.TrackingId, cancellationToken);
            }

            // No explicit tracking id: the latest unlinked visit for this code in the last 7 days.
            return await _dapper.QueryFirstOrDefaultAsync<VisitRow>(VisitSelect + @"
WHERE Code = @code AND LinkedReferralId IS NULL AND CreatedAt >= @since
ORDER BY CreatedAt DESC;",
                new { code, since = DateTime.UtcNow.AddDays(-7) },
                cancellationToken: cancellationToken);
        }

        private Task<VisitRow> FindVisitByTrackingAsync(string trackingId, CancellationToken cancellationToken) =>
            _dapper.QueryFirstOrDefaultAsync<VisitRow>(VisitSelect + " WHERE TrackingId = @trackingId;",
                new { trackingId },
                cancellationToken: cancellationToken);

        private const string VisitSelect = @"
SELECT TOP 1 Id, TrackingId, Code, ReferrerId, RuleId, Currency, ReceiveCurrency, VisitorId, DeviceType,
       OperatingSystem, SourceChannel, AttributionStatus, StoreTarget, ResolvedAt, LinkedReferralId,
       LinkedRefereeId, ExpiresAt, CreatedAt
FROM dbo.RewardReferralVisit";

        // TRK-XXXXXXXX (8 hex chars, unpredictable); re-rolled on the (unlikely) clash.
        private async Task<string> NewTrackingIdAsync(CancellationToken cancellationToken)
        {
            for (var i = 0; i < 20; i++)
            {
                var candidate = "TRK-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
                var clash = await _dapper.ExecuteScalarAsync<int>(@"
SELECT COUNT(1) FROM dbo.RewardReferralVisit WHERE TrackingId = @candidate;",
                    new { candidate },
                    cancellationToken: cancellationToken);
                if (clash == 0) return candidate;
            }

            return "TRK-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        }

        private static (string TrackingId, string ReferralCode) ParseReferrerToken(string input)
        {
            var raw = (input ?? string.Empty).Trim();
            if (raw.Length == 0) return (string.Empty, string.Empty);
            var decoded = raw;
            try { decoded = Uri.UnescapeDataString(raw); } catch { /* keep raw */ }
            if (decoded.Contains('=') || decoded.Contains('&'))
            {
                string tracking = string.Empty, referral = string.Empty;
                foreach (var part in decoded.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var idx = part.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = part.Substring(0, idx).Trim();
                    var value = part.Substring(idx + 1).Trim().ToUpperInvariant();
                    if (key == "tracking_id" || key == "trackingId") tracking = value;
                    else if (key == "referral_code" || key == "referralCode" || key == "code") referral = value;
                }

                return (tracking, referral);
            }

            return (decoded.ToUpperInvariant(), string.Empty);
        }

        private static (string DeviceType, string OperatingSystem) DetectDevice(string userAgent, string explicitDevice, string explicitOs)
        {
            var ua = userAgent ?? string.Empty;
            var os = string.IsNullOrWhiteSpace(explicitOs) ? string.Empty : explicitOs.Trim();
            if (os.Length == 0)
            {
                if (Regex.IsMatch(ua, "android", RegexOptions.IgnoreCase)) os = "Android";
                else if (Regex.IsMatch(ua, "ipad|iphone|ipod|ios", RegexOptions.IgnoreCase)) os = "iOS";
                else if (Regex.IsMatch(ua, "windows", RegexOptions.IgnoreCase)) os = "Windows";
                else if (Regex.IsMatch(ua, "macintosh|mac os x", RegexOptions.IgnoreCase)) os = "macOS";
                else if (Regex.IsMatch(ua, "linux", RegexOptions.IgnoreCase)) os = "Linux";
                else os = "Other";
            }

            var device = string.IsNullOrWhiteSpace(explicitDevice) ? string.Empty : explicitDevice.Trim().ToLowerInvariant();
            if (device.Length == 0)
            {
                if (Regex.IsMatch(ua, "ipad|tablet", RegexOptions.IgnoreCase)) device = "tablet";
                else if (Regex.IsMatch(ua, "mobi|android|iphone|ipod", RegexOptions.IgnoreCase)) device = "mobile";
                else device = "desktop";
            }

            return (Limit(device, 32), Limit(os, 32));
        }

        private static string PlayStoreUrlFor(string trackingId, string referralCode)
        {
            var pkg = Environment.GetEnvironmentVariable("RHEMITO_ANDROID_PACKAGE_ID");
            if (string.IsNullOrWhiteSpace(pkg)) pkg = "com.funtechcom.rhemito";
            var referrerParam = !string.IsNullOrEmpty(trackingId)
                ? "tracking_id=" + trackingId + (!string.IsNullOrEmpty(referralCode) ? "&referral_code=" + referralCode : string.Empty)
                : (!string.IsNullOrEmpty(referralCode) ? "referral_code=" + referralCode : string.Empty);
            var baseUrl = "https://play.google.com/store/apps/details?id=" + Uri.EscapeDataString(pkg);
            return referrerParam.Length > 0 ? baseUrl + "&referrer=" + Uri.EscapeDataString(referrerParam) : baseUrl;
        }

        private static string IosAppStoreUrl()
        {
            var url = Environment.GetEnvironmentVariable("RHEMITO_IOS_APP_STORE_URL");
            return string.IsNullOrWhiteSpace(url)
                ? "https://apps.apple.com/gb/app/rhemito-money-transfer/id1494717263"
                : url;
        }

        private static TimeZoneInfo ResolveUkZone()
        {
            foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch { /* try next */ }
            }

            return TimeZoneInfo.Utc;
        }

        private static DateTime AsUtc(DateTime value) =>
            value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        private static DateTime UkDay(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), UkTz).Date;

        private static DateTime UkToday() => UkDay(DateTime.UtcNow);

        private static DateTime? ParseDay(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var s = value.Trim();
            if (s.Length >= 10
                && DateTime.TryParseExact(s.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return day.Date;
            }

            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var any)
                ? any.Date
                : (DateTime?)null;
        }

        private static string NormaliseCurrency(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var s = value.Trim().ToUpperInvariant();
            return s.Length == 3 ? s : null;
        }

        private static string Opt(JToken token)
        {
            var s = Text(token);
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        private static string Limit(string value, int max) =>
            value != null && value.Length > max ? value.Substring(0, max) : value;

        private static string EscapeLike(string value) =>
            value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

        private async Task<string> UniqueCodeAsync(CancellationToken cancellationToken)
        {
            for (var i = 0; i < 8; i++)
            {
                var bytes = RandomNumberGenerator.GetBytes(4);
                var code = Convert.ToHexString(bytes)[..6];
                var clash = await _dapper.QueryFirstOrDefaultAsync<CustomerRow>(@"
SELECT TOP 1 Id, FirstName, LastName, Email, SendCurrency, AccountStatus, KycStatus, ReferralCode, CreatedAt, UpdatedAt
FROM dbo.RewardCustomer WHERE ReferralCode = @code;",
                    new { code },
                    cancellationToken: cancellationToken);
                if (clash == null) return code;
            }

            return Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        }

        private static JObject Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JObject();
            try { return JObject.Parse(json); }
            catch { return new JObject(); }
        }

        private static decimal Money(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0m;
            if (token.Type is JTokenType.Float or JTokenType.Integer) return token.Value<decimal>();
            return decimal.TryParse(token.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
                ? n
                : 0m;
        }

        private static string Text(JToken token) =>
            token == null || token.Type == JTokenType.Null ? null : token.ToString();

        private sealed class CustomerRow
        {
            public string Id { get; set; }
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string Email { get; set; }
            public string SendCurrency { get; set; }
            public string AccountStatus { get; set; }
            public string KycStatus { get; set; }
            public string ReferralCode { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
        }

        private sealed class RuleRow
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Status { get; set; }
            public string BaseCurrency { get; set; }
            public string ReceiveCurrency { get; set; }
            public string ConfigJson { get; set; }

            // STAGING-SYNC: parsed ConfigJson (not a column — Dapper ignores it).
            private JObject _cfg;
            public JObject Cfg => _cfg ??= Parse(ConfigJson);
        }

        private sealed class ReferralRow
        {
            public string Id { get; set; }
            public string Code { get; set; }
            public string ReferrerId { get; set; }
            public string RefereeId { get; set; }
            public string RuleId { get; set; }
            public string Status { get; set; }
            public string StatusReason { get; set; }
            public string Currency { get; set; }
            public string ReceiveCurrency { get; set; }
            public decimal? ReferrerReward { get; set; }
            public decimal? RefereeReward { get; set; }
            public decimal? ReferrerCredited { get; set; }
            public decimal? RefereeCredited { get; set; }
            public string QualifyingTransferId { get; set; }
            public decimal? QualifyingAmount { get; set; }
            public DateTime RegisteredAt { get; set; }
            public DateTime? RewardedAt { get; set; }
            public string ConfigJson { get; set; }
        }

        // STAGING-SYNC: dbo.RewardReferralVisit row.
        private sealed class VisitRow
        {
            public string Id { get; set; }
            public string TrackingId { get; set; }
            public string Code { get; set; }
            public string ReferrerId { get; set; }
            public string RuleId { get; set; }
            public string Currency { get; set; }
            public string ReceiveCurrency { get; set; }
            public string VisitorId { get; set; }
            public string DeviceType { get; set; }
            public string OperatingSystem { get; set; }
            public string SourceChannel { get; set; }
            public string AttributionStatus { get; set; }
            public string StoreTarget { get; set; }
            public DateTime? ResolvedAt { get; set; }
            public string LinkedReferralId { get; set; }
            public string LinkedRefereeId { get; set; }
            public DateTime? ExpiresAt { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        private sealed class TrackingCsvRow
        {
            public string Id { get; set; }
            public string Code { get; set; }
            public string ReferrerId { get; set; }
            public string RefereeId { get; set; }
            public string RuleId { get; set; }
            public string Status { get; set; }
            public string StatusReason { get; set; }
            public string Currency { get; set; }
            public string ReceiveCurrency { get; set; }
            public decimal? ReferrerCredited { get; set; }
            public decimal? RefereeCredited { get; set; }
            public string QualifyingTransferId { get; set; }
            public DateTime RegisteredAt { get; set; }
            public DateTime? RewardedAt { get; set; }
            public string ConfigJson { get; set; }
            public string RuleName { get; set; }
            public string ReferrerFirstName { get; set; }
            public string ReferrerLastName { get; set; }
            public string RefereeFirstName { get; set; }
            public string RefereeLastName { get; set; }
            public string TrackingId { get; set; }
            public string VisitOs { get; set; }
        }

        private sealed class StatusCountRow
        {
            public string Status { get; set; }
            public int Total { get; set; }
        }

        private sealed class LedgerTotalRow
        {
            public string Type { get; set; }
            public decimal Total { get; set; }
        }

        private sealed class PerformanceRow
        {
            public string Name { get; set; }
            public string Currency { get; set; }
            public string ReceiveCurrency { get; set; }
            public string Status { get; set; }
            public int LinkVisits { get; set; }
            public int Registrations { get; set; }
            public int Pending { get; set; }
            public int Rewarded { get; set; }
            public int Closed { get; set; }
            public decimal? ConversionRate { get; set; }
            public decimal BonusIssued { get; set; }
            public decimal BonusUsed { get; set; }
            public decimal BonusUnused { get; set; }
            public decimal BonusExpired { get; set; }
            public decimal ReferredVolume { get; set; }
        }
    }
}
