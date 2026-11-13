using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Furp.Application.Interfaces.Repositories;
using Newtonsoft.Json.Linq;

namespace Furp.Application.Features.Rewards.Engine
{
    /// <summary>One-balance bonus wallet (BS-80…84, API-S3/S4/S5).</summary>
    public sealed class RewardWalletService
    {
        private readonly IDapperRepository _dapper;
        private readonly RewardDebtBlocks _debt;
        private readonly RewardFeed _feed;

        // STAGING-SYNC: latch for the additive Floor / ReceiveCurrency columns (see EnsureAsync).
        private static int _columnsReady;

        // STAGING-SYNC: idempotent additive DDL for credit Floor + corridor (ReceiveCurrency).
        // Keep in sync with RewardSchema.DdlSql / the ops migration script.
        private const string ColumnsDdl = @"
IF COL_LENGTH(N'dbo.RewardCreditLedger', N'Floor') IS NULL
BEGIN
    ALTER TABLE dbo.RewardCreditLedger ADD [Floor] DECIMAL(18,2) NULL;
END;

IF COL_LENGTH(N'dbo.RewardCreditLedger', N'ReceiveCurrency') IS NULL
BEGIN
    ALTER TABLE dbo.RewardCreditLedger ADD ReceiveCurrency CHAR(3) NULL;
END;";

        // STAGING-SYNC: atomic apply (debit) under a per-user application lock.
        // sp_getapplock (Transaction owner) serialises concurrent applies for the same user, the eligible
        // credits are re-read INSIDE the lock, and the APPLIED rows are inserted before the lock is released
        // at COMMIT. Status: 0 = applied, 1 = BALANCE_CHANGED, 2 = ALREADY_APPLIED, 3 = lock timeout.
        // Eligible credits: not expired, remaining > 0, corridor matches (ReceiveCurrency NULL or equals request),
        // Floor satisfied (Floor NULL/0, or no amount basis supplied, or basis >= Floor). Floored / wrong-corridor
        // credits are skipped entirely - never partially consumed. Soonest expiry first, then oldest.
        private const string ApplyAtomicSql = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @outer INT = @@TRANCOUNT;
DECLARE @status INT = 0;
DECLARE @applied DECIMAL(18,2) = 0;
DECLARE @amt DECIMAL(18,2) = @amount;
DECLARE @rc INT = 0;
DECLARE @plan TABLE (
    Id NVARCHAR(64) NOT NULL,
    TakeAmount DECIMAL(18,2) NOT NULL,
    CreditSource NVARCHAR(32) NULL,
    CreditSourceDetail NVARCHAR(64) NULL,
    [Floor] DECIMAL(18,2) NULL,
    ReceiveCurrency CHAR(3) NULL
);

IF @outer = 0 BEGIN TRANSACTION;

EXEC @rc = sp_getapplock
    @Resource = @lockKey,
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 10000;

IF @rc < 0
BEGIN
    SET @status = 3;
END
ELSE IF EXISTS (
    SELECT 1 FROM dbo.RewardCreditLedger
    WHERE UserId = @userId AND TransferId = @transferId AND Type = N'APPLIED')
BEGIN
    SET @status = 2;
END
ELSE
BEGIN
    ;WITH Spent AS (
        SELECT SourceCreditId, SUM(ABS(Amount)) AS Used
        FROM dbo.RewardCreditLedger
        WHERE UserId = @userId
          AND Currency = @currency
          AND Type IN (N'APPLIED', N'EXPIRED', N'VOIDED')
          AND SourceCreditId IS NOT NULL
        GROUP BY SourceCreditId
    ),
    Rem AS (
        SELECT c.Id, c.CreditSource, c.CreditSourceDetail, c.[Floor], c.ReceiveCurrency,
               c.ExpiresAt, c.CreatedAt,
               c.Amount - ISNULL(s.Used, 0) AS Remaining
        FROM dbo.RewardCreditLedger c
        LEFT JOIN Spent s ON s.SourceCreditId = c.Id
        WHERE c.UserId = @userId
          AND c.Currency = @currency
          AND c.Type IN (N'EARNED', N'RETURNED')
          AND (c.ExpiresAt IS NULL OR c.ExpiresAt > SYSUTCDATETIME())
          AND (NULLIF(LTRIM(RTRIM(c.ReceiveCurrency)), N'') IS NULL
               OR (@receiveCurrency IS NOT NULL AND UPPER(LTRIM(RTRIM(c.ReceiveCurrency))) = @receiveCurrency))
          AND (ISNULL(c.[Floor], 0) <= 0
               OR @floorBasis IS NULL
               OR @floorBasis >= c.[Floor])
          -- STAGING-SYNC: optional source filter. NULL = unrestricted multi-source (unchanged behaviour).
          -- REFERRAL = referral credits only; BONUS (SCHEME/BONUS) = every non-referral credit; other value = exact CreditSource.
          AND (@creditSource IS NULL
               OR (@creditSource = N'REFERRAL'
                   AND (UPPER(ISNULL(c.CreditSource, N'')) = N'REFERRAL'
                        OR (NULLIF(LTRIM(RTRIM(c.CreditSource)), N'') IS NULL AND c.ReferralId IS NOT NULL)))
               OR (@creditSource = N'BONUS'
                   AND NOT (UPPER(ISNULL(c.CreditSource, N'')) = N'REFERRAL'
                            OR (NULLIF(LTRIM(RTRIM(c.CreditSource)), N'') IS NULL AND c.ReferralId IS NOT NULL)))
               OR (@creditSource NOT IN (N'REFERRAL', N'BONUS')
                   AND UPPER(ISNULL(c.CreditSource, N'')) = @creditSource))
    ),
    Run AS (
        SELECT Id, CreditSource, CreditSourceDetail, [Floor], ReceiveCurrency, Remaining,
               ISNULL(SUM(Remaining) OVER (
                   ORDER BY ISNULL(ExpiresAt, CONVERT(DATETIME2, '9999-12-31')), CreatedAt, Id
                   ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS Prior
        FROM Rem
        WHERE Remaining > 0
    )
    INSERT INTO @plan (Id, TakeAmount, CreditSource, CreditSourceDetail, [Floor], ReceiveCurrency)
    SELECT Id,
           CASE WHEN Remaining < @amt - Prior THEN Remaining ELSE @amt - Prior END,
           CreditSource, CreditSourceDetail, [Floor], ReceiveCurrency
    FROM Run
    WHERE Prior < @amt;

    SELECT @applied = ISNULL(SUM(TakeAmount), 0) FROM @plan;

    IF @applied < @amt
    BEGIN
        SET @status = 1;
    END
    ELSE
    BEGIN
        INSERT INTO dbo.RewardCreditLedger
            (Id, UserId, Amount, Type, Currency, ReferenceId, ReasonCode, CreatedAt,
             SourceCreditId, TransferId, CreditSource, CreditSourceDetail, SourceType, [Floor], ReceiveCurrency)
        SELECT N'crd_' + LOWER(REPLACE(CONVERT(NVARCHAR(36), NEWID()), N'-', N'')),
               @userId, -p.TakeAmount, N'APPLIED', @currency,
               N'apply:' + @transferId + N':' + p.Id, N'BONUS_APPLIED', SYSUTCDATETIME(),
               p.Id, @transferId, p.CreditSource, p.CreditSourceDetail, N'BONUS', p.[Floor], p.ReceiveCurrency
        FROM @plan p;
    END
END

IF @outer = 0 COMMIT TRANSACTION;

SELECT @status AS Status, @applied AS Applied;";

        public RewardWalletService(IDapperRepository dapper, RewardDebtBlocks debt, RewardFeed feed)
        {
            _dapper = dapper;
            _debt = debt;
            _feed = feed;
        }

        // STAGING-SYNC: RewardSchema bootstrap + additive Floor / ReceiveCurrency columns.
        private async Task EnsureAsync(CancellationToken cancellationToken)
        {
            await RewardSchema.EnsureAsync(_dapper, cancellationToken);
            if (Interlocked.CompareExchange(ref _columnsReady, 1, 1) == 1) return;

            try
            {
                await _dapper.ExecuteAsync(ColumnsDdl, cancellationToken: cancellationToken);
            }
            catch (Exception)
            {
                // Another node may have added the column concurrently; the DDL is idempotent, so retry once.
                await _dapper.ExecuteAsync(ColumnsDdl, cancellationToken: cancellationToken);
            }

            Interlocked.Exchange(ref _columnsReady, 1);
        }

        public async Task<JObject> GetWalletAsync(
            string customerId,
            string currency,
            string creditSourceFilter,
            CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var userId = (customerId ?? string.Empty).Trim();
            var cur = string.IsNullOrWhiteSpace(currency)
                ? null
                : currency.Trim().ToUpperInvariant();

            IEnumerable<LedgerRow> rows;
            try
            {
                rows = await _dapper.QueryAsync<LedgerRow>(@"
SELECT Id, UserId, Amount, Type, Currency, SchemeId, ReferenceId, ReasonCode, Notes,
       AdminUser, ExpiresAt, CreatedAt, ReferralId, ReferralRuleId, SourceCreditId,
       TransferId, CreditSource, CreditSourceDetail, ReturnedFromCreditId, SourceType,
       [Floor], ReceiveCurrency
FROM dbo.RewardCreditLedger
WHERE UserId = @userId
  AND (@currency IS NULL OR Currency = @currency)
ORDER BY CreatedAt ASC, Id ASC;",
                    new { userId, currency = cur },
                    cancellationToken: cancellationToken);
            }
            catch
            {
                // Older live tables may predate CreditSource/SourceType; re-run DDL ALTERs once.
                RewardSchema.ResetForTests();
                Interlocked.Exchange(ref _columnsReady, 0);
                await EnsureAsync(cancellationToken);
                rows = await _dapper.QueryAsync<LedgerRow>(@"
SELECT Id, UserId, Amount, Type, Currency, SchemeId, ReferenceId, ReasonCode, Notes,
       AdminUser, ExpiresAt, CreatedAt, ReferralId, ReferralRuleId, SourceCreditId,
       TransferId, CreditSource, CreditSourceDetail, ReturnedFromCreditId, SourceType,
       [Floor], ReceiveCurrency
FROM dbo.RewardCreditLedger
WHERE UserId = @userId
  AND (@currency IS NULL OR Currency = @currency)
ORDER BY CreatedAt ASC, Id ASC;",
                    new { userId, currency = cur },
                    cancellationToken: cancellationToken);
            }

            var list = (rows ?? Array.Empty<LedgerRow>()).ToList();
            EnrichSources(list);

            var blocked = await IsBlockedAsync(userId, cancellationToken);
            var currencies = list.Select(r => r.Currency).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (currencies.Count == 0 && cur != null) currencies.Add(cur);

            var balances = new JArray();
            foreach (var c in currencies)
            {
                var slice = list.Where(r => string.Equals(r.Currency, c, StringComparison.OrdinalIgnoreCase)).ToList();
                balances.Add(BuildBalance(c, slice));
            }

            var filter = (creditSourceFilter ?? string.Empty).Trim().ToUpperInvariant();
            var history = list.AsEnumerable();
            if (!string.IsNullOrEmpty(filter))
            {
                history = history.Where(r =>
                    string.Equals(r.CreditSource, filter, StringComparison.OrdinalIgnoreCase));
            }

            var historyArr = new JArray(history.Select(ToHistoryRow));
            var unused = new JArray(
                RemainingCredits(list)
                    .Where(x => string.IsNullOrEmpty(filter)
                        || string.Equals(x.row.CreditSource, filter, StringComparison.OrdinalIgnoreCase))
                    .Select(x =>
                    {
                        var o = ToHistoryRow(x.row);
                        o["remaining"] = x.remaining;
                        return o;
                    }));

            return new JObject
            {
                ["customer_id"] = userId,
                ["balances"] = balances,
                ["bonus_blocked"] = blocked,
                ["unused"] = unused,
                ["credits"] = unused,
                ["history"] = historyArr,
                ["promo_redemptions"] = new JArray()
            };
        }

        public async Task<JObject> ApplyAsync(
            string customerId,
            JObject body,
            CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var userId = customerId.Trim();
            var amount = Money(body?["amount"]);
            var currency = Text(body?["currency"])?.ToUpperInvariant() ?? "GBP";
            var transferId = Text(body?["transfer_id"]) ?? Text(body?["transferId"]);
            var sendAmount = Money(body?["send_amount"] ?? body?["sendAmount"]);

            // STAGING-SYNC: optional gross transfer amount (used for credit Floor) and receive currency (corridor).
            var transferAmount = MoneyOrNull(body?["transfer_amount"] ?? body?["transferAmount"]);
            var receiveCurrency = NormalizeCurrency(Text(body?["receive_currency"] ?? body?["receiveCurrency"]));
            decimal? floorBasis = null;
            if (transferAmount.HasValue && transferAmount.Value > 0m)
            {
                floorBasis = transferAmount.Value;
            }
            else if (sendAmount > 0m)
            {
                floorBasis = sendAmount;
            }

            // STAGING-SYNC: optional additive source filter. NULL (key omitted / blank) = existing unrestricted
            // multi-source behaviour. "REFERRAL" debits referral credits only; "SCHEME" / "BONUS" debits non-referral credits only.
            var creditSource = NormalizeCreditSourceFilter(Text(body?["credit_source"] ?? body?["creditSource"]));

            if (amount <= 0)
            {
                return Err(400, "Amount must be greater than zero.");
            }

            if (string.IsNullOrWhiteSpace(transferId))
            {
                return Err(400, "transfer_id is required.");
            }

            if (await IsBlockedAsync(userId, cancellationToken))
            {
                return Err(403, RewardEngineGateway.NotQualifiedMessage);
            }

            var existing = await _dapper.QueryFirstOrDefaultAsync<LedgerRow>(@"
SELECT TOP 1 Id, UserId, Amount, Type, Currency, SchemeId, ReferenceId, ReasonCode, Notes,
       AdminUser, ExpiresAt, CreatedAt, ReferralId, ReferralRuleId, SourceCreditId,
       TransferId, CreditSource, CreditSourceDetail, ReturnedFromCreditId, SourceType
FROM dbo.RewardCreditLedger
WHERE UserId = @userId AND TransferId = @transferId AND Type = N'APPLIED';",
                new { userId, transferId },
                cancellationToken: cancellationToken);
            if (existing != null)
            {
                return Err(409, "ALREADY_APPLIED");
            }

            // STAGING-SYNC: advisory pre-check (gives precise BELOW_FLOOR / WRONG_CORRIDOR errors).
            // The authoritative, race-free check + debit happens in ApplyAtomicSql below under sp_getapplock.
            var remainingAll = RemainingCredits(
                (await LoadUserRowsAsync(userId, currency, cancellationToken)).ToList())
                .Where(x => SourceOk(x.row, creditSource))
                .ToList();
            var eligibleAvailable = remainingAll
                .Where(x => CorridorOk(x.row, receiveCurrency) && FloorOk(x.row, floorBasis))
                .Sum(x => x.remaining);
            var max = sendAmount > 0 ? Math.Min(eligibleAvailable, sendAmount) : eligibleAvailable;
            if (amount > max)
            {
                var allAvailable = remainingAll.Sum(x => x.remaining);
                var ceiling = sendAmount > 0 ? Math.Min(allAvailable, sendAmount) : allAvailable;
                if (amount <= ceiling)
                {
                    // Enough credit exists, but corridor / Floor rules exclude it.
                    if (remainingAll.Any(x => !CorridorOk(x.row, receiveCurrency)))
                    {
                        var wrong = remainingAll.First(x => !CorridorOk(x.row, receiveCurrency)).row;
                        return Err(
                            400,
                            "Transfer must be on the " + currency + " \u2192 " + NormalizeCurrency(wrong.ReceiveCurrency)
                                + " corridor to redeem this bonus.",
                            "WRONG_CORRIDOR");
                    }

                    var floored = remainingAll.Where(x => !FloorOk(x.row, floorBasis)).ToList();
                    if (floored.Count > 0)
                    {
                        var minFloor = floored.Min(x => x.row.Floor ?? 0m);
                        return Err(
                            400,
                            "Transfer amount must be at least "
                                + minFloor.ToString("0.00", CultureInfo.InvariantCulture) + " " + currency
                                + " to redeem this bonus.",
                            "BELOW_FLOOR");
                    }
                }

                return Err(409, "BALANCE_CHANGED");
            }

            // STAGING-SYNC: read-then-write under a per-user application lock (prevents concurrent double-spend).
            var result = await _dapper.QueryFirstOrDefaultAsync<ApplyResultRow>(
                ApplyAtomicSql,
                new
                {
                    userId,
                    currency,
                    transferId,
                    amount,
                    receiveCurrency,
                    floorBasis,
                    creditSource,
                    lockKey = "reward-wallet:" + userId
                },
                cancellationToken: cancellationToken);

            if (result == null)
            {
                return Err(409, "BALANCE_CHANGED");
            }

            switch (result.Status)
            {
                case 0:
                    break;
                case 2:
                    return Err(409, "ALREADY_APPLIED");
                case 3:
                    return Err(409, "Your bonus balance is being updated. Please try again.", "WALLET_BUSY");
                default:
                    return Err(409, "BALANCE_CHANGED");
            }

            var appliedAmount = result.Applied;
            var after = AvailableFromWallet(
                await GetWalletAsync(userId, currency, null, cancellationToken), currency);
            if (appliedAmount > 0)
            {
                await _feed.EmitAsync(
                    userId,
                    "BONUS_USED",
                    "bu:" + transferId,
                    new JObject
                    {
                        ["amount"] = appliedAmount,
                        ["currency"] = currency,
                        ["transfer_id"] = transferId
                    },
                    cancellationToken);
            }

            return new JObject
            {
                ["applied"] = appliedAmount,
                ["available"] = after
            };
        }

        public async Task<JObject> ReleaseAsync(
            string customerId,
            JObject body,
            CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var userId = customerId.Trim();
            var transferId = Text(body?["transfer_id"]) ?? Text(body?["transferId"]);
            if (string.IsNullOrWhiteSpace(transferId))
            {
                return Err(400, "transfer_id is required.");
            }

            var applied = await _dapper.QueryAsync<LedgerRow>(@"
SELECT Id, UserId, Amount, Type, Currency, SchemeId, ReferenceId, ReasonCode, Notes,
       AdminUser, ExpiresAt, CreatedAt, ReferralId, ReferralRuleId, SourceCreditId,
       TransferId, CreditSource, CreditSourceDetail, ReturnedFromCreditId, SourceType,
       [Floor], ReceiveCurrency
FROM dbo.RewardCreditLedger
WHERE UserId = @userId AND TransferId = @transferId AND Type = N'APPLIED';",
                new { userId, transferId },
                cancellationToken: cancellationToken);

            decimal returned = 0;
            foreach (var row in applied ?? Array.Empty<LedgerRow>())
            {
                var already = await _dapper.QueryFirstOrDefaultAsync<LedgerRow>(@"
SELECT TOP 1 Id, UserId, Amount, Type, Currency, SchemeId, ReferenceId, ReasonCode, Notes,
       AdminUser, ExpiresAt, CreatedAt, ReferralId, ReferralRuleId, SourceCreditId,
       TransferId, CreditSource, CreditSourceDetail, ReturnedFromCreditId, SourceType
FROM dbo.RewardCreditLedger
WHERE ReturnedFromCreditId = @id AND Type = N'RETURNED';",
                    new { id = row.Id },
                    cancellationToken: cancellationToken);
                if (already != null) continue;

                var amount = Math.Abs(row.Amount);
                returned += amount;
                DateTime? expires = null;
                if (!string.IsNullOrWhiteSpace(row.SourceCreditId))
                {
                    var source = await _dapper.QueryFirstOrDefaultAsync<LedgerRow>(@"
SELECT TOP 1 Id, UserId, Amount, Type, Currency, SchemeId, ReferenceId, ReasonCode, Notes,
       AdminUser, ExpiresAt, CreatedAt, ReferralId, ReferralRuleId, SourceCreditId,
       TransferId, CreditSource, CreditSourceDetail, ReturnedFromCreditId, SourceType
FROM dbo.RewardCreditLedger WHERE Id = @id;",
                        new { id = row.SourceCreditId },
                        cancellationToken: cancellationToken);
                    expires = source?.ExpiresAt;
                }

                await InsertAsync(new LedgerRow
                {
                    Id = "crd_" + Guid.NewGuid().ToString("N"),
                    UserId = userId,
                    Amount = amount,
                    Type = "RETURNED",
                    Currency = row.Currency,
                    TransferId = transferId,
                    SourceCreditId = row.SourceCreditId,
                    ReturnedFromCreditId = row.Id,
                    CreditSource = row.CreditSource,
                    CreditSourceDetail = row.CreditSourceDetail,
                    SourceType = "BONUS",
                    ReferenceId = "return:" + transferId + ":" + row.Id,
                    ReasonCode = "BONUS_RETURNED",
                    ExpiresAt = expires,
                    CreatedAt = DateTime.UtcNow,
                    // STAGING-SYNC: a returned credit keeps the Floor / corridor of the credit it came from.
                    Floor = row.Floor,
                    ReceiveCurrency = row.ReceiveCurrency
                }, cancellationToken);
            }

            if (returned > 0)
            {
                var currency = (applied ?? Array.Empty<LedgerRow>()).FirstOrDefault()?.Currency ?? "GBP";
                await _feed.EmitAsync(
                    userId,
                    "BONUS_RETURNED",
                    "br:" + transferId,
                    new JObject
                    {
                        ["amount"] = returned,
                        ["currency"] = currency,
                        ["transfer_id"] = transferId
                    },
                    cancellationToken);
            }

            return new JObject { ["returned"] = returned };
        }

        public async Task<string> IssueCreditAsync(
            string customerId,
            decimal amount,
            string currency,
            int validityDays,
            string creditSource,
            string creditSourceDetail,
            string referralId,
            string ruleId,
            string schemeId,
            string referenceId,
            string notes,
            string actor,
            CancellationToken cancellationToken,
            // STAGING-SYNC: optional credit restrictions (additive; existing callers unaffected).
            decimal? floor = null,
            string receiveCurrency = null)
        {
            await EnsureAsync(cancellationToken);
            if (amount <= 0) throw new InvalidOperationException("Amount must be positive.");
            if (!string.IsNullOrWhiteSpace(referenceId))
            {
                var existing = await _dapper.QueryFirstOrDefaultAsync<LedgerRow>(@"
SELECT TOP 1 Id, UserId, Amount, Type, Currency, SchemeId, ReferenceId, ReasonCode, Notes,
       AdminUser, ExpiresAt, CreatedAt, ReferralId, ReferralRuleId, SourceCreditId,
       TransferId, CreditSource, CreditSourceDetail, ReturnedFromCreditId, SourceType
FROM dbo.RewardCreditLedger WHERE ReferenceId = @referenceId;",
                    new { referenceId },
                    cancellationToken: cancellationToken);
                if (existing != null) return existing.Id;
            }

            var id = "crd_" + Guid.NewGuid().ToString("N");
            var days = validityDays > 0 ? validityDays : 90;
            var cur = (currency ?? "GBP").Trim().ToUpperInvariant();
            var source = (creditSource ?? "MANUAL").ToUpperInvariant();
            await InsertAsync(new LedgerRow
            {
                Id = id,
                UserId = customerId.Trim(),
                Amount = amount,
                Type = "EARNED",
                Currency = cur,
                SchemeId = schemeId,
                ReferralId = referralId,
                ReferralRuleId = ruleId,
                ReferenceId = referenceId,
                ReasonCode = string.Equals(creditSource, "REFERRAL", StringComparison.OrdinalIgnoreCase)
                    ? "REFERRAL_REWARD"
                    : string.Equals(creditSource, "SCHEME", StringComparison.OrdinalIgnoreCase)
                        ? "SCHEME_BONUS"
                        : (creditSourceDetail ?? "MANUAL_ADJUSTMENT"),
                Notes = notes,
                AdminUser = actor ?? "System",
                CreditSource = source,
                CreditSourceDetail = (creditSourceDetail ?? "MANUAL_ADJUSTMENT").ToUpperInvariant(),
                SourceType = "BONUS",
                ExpiresAt = DateTime.UtcNow.Date.AddDays(days),
                CreatedAt = DateTime.UtcNow,
                Floor = floor.HasValue && floor.Value > 0m ? floor : null,
                ReceiveCurrency = NormalizeCurrency(receiveCurrency)
            }, cancellationToken);

            // BS-51: new earnings settle outstanding clawback debt first
            await _debt.SettleDebtAsync(customerId.Trim(), cur, cancellationToken);

            await _feed.EmitAsync(
                customerId.Trim(),
                "BONUS_EARNED",
                "bearn:" + id,
                new JObject
                {
                    ["amount"] = amount,
                    ["currency"] = cur,
                    ["scheme_name"] = notes,
                    ["credit_source"] = source,
                    ["expires_on"] = DateTime.UtcNow.Date.AddDays(days).ToString("yyyy-MM-dd"),
                    ["by_source"] = new JArray
                    {
                        new JObject
                        {
                            ["credit_source"] = source,
                            ["amount"] = amount,
                            ["currency"] = cur
                        }
                    }
                },
                cancellationToken);

            return id;
        }

        public async Task<(decimal voided, decimal spent)> VoidCreditAsync(
            string creditId,
            string reason,
            string notes,
            CancellationToken cancellationToken)
        {
            await EnsureAsync(cancellationToken);
            var credit = await _dapper.QueryFirstOrDefaultAsync<LedgerRow>(@"
SELECT TOP 1 Id, UserId, Amount, Type, Currency, SchemeId, ReferenceId, ReasonCode, Notes,
       AdminUser, ExpiresAt, CreatedAt, ReferralId, ReferralRuleId, SourceCreditId,
       TransferId, CreditSource, CreditSourceDetail, ReturnedFromCreditId, SourceType
FROM dbo.RewardCreditLedger WHERE Id = @id AND Type = N'EARNED';",
                new { id = creditId },
                cancellationToken: cancellationToken);
            if (credit == null) return (0, 0);

            var rem = RemainingCredits(new List<LedgerRow> { credit }
                .Concat(await LoadUserRowsAsync(credit.UserId, credit.Currency, cancellationToken))
                .ToList())
                .FirstOrDefault(x => x.row.Id == credit.Id).remaining;

            if (rem > 0)
            {
                await InsertAsync(new LedgerRow
                {
                    Id = "crd_" + Guid.NewGuid().ToString("N"),
                    UserId = credit.UserId,
                    Amount = -rem,
                    Type = "VOIDED",
                    Currency = credit.Currency,
                    SourceCreditId = credit.Id,
                    CreditSource = credit.CreditSource,
                    CreditSourceDetail = credit.CreditSourceDetail,
                    SourceType = "BONUS",
                    ReferenceId = "void:" + credit.Id + ":" + Guid.NewGuid().ToString("N")[..8],
                    ReasonCode = reason ?? "VOID",
                    Notes = notes,
                    CreatedAt = DateTime.UtcNow
                }, cancellationToken);
            }

            return (rem, Math.Max(0, credit.Amount - rem));
        }

        private async Task<IReadOnlyList<LedgerRow>> LoadUserRowsAsync(
            string userId,
            string currency,
            CancellationToken cancellationToken)
        {
            var rows = await _dapper.QueryAsync<LedgerRow>(@"
SELECT Id, UserId, Amount, Type, Currency, SchemeId, ReferenceId, ReasonCode, Notes,
       AdminUser, ExpiresAt, CreatedAt, ReferralId, ReferralRuleId, SourceCreditId,
       TransferId, CreditSource, CreditSourceDetail, ReturnedFromCreditId, SourceType,
       [Floor], ReceiveCurrency
FROM dbo.RewardCreditLedger
WHERE UserId = @userId AND Currency = @currency;",
                new { userId, currency },
                cancellationToken: cancellationToken);
            var list = (rows ?? Array.Empty<LedgerRow>()).ToList();
            EnrichSources(list);
            return list;
        }

        private async Task InsertAsync(LedgerRow row, CancellationToken cancellationToken)
        {
            await _dapper.ExecuteAsync(@"
INSERT INTO dbo.RewardCreditLedger
(Id, UserId, Amount, Type, Currency, SchemeId, ReferenceId, ReasonCode, Notes, AdminUser,
 ExpiresAt, CreatedAt, ReferralId, ReferralRuleId, SourceCreditId, TransferId,
 CreditSource, CreditSourceDetail, ReturnedFromCreditId, SourceType, [Floor], ReceiveCurrency)
VALUES
(@Id, @UserId, @Amount, @Type, @Currency, @SchemeId, @ReferenceId, @ReasonCode, @Notes, @AdminUser,
 @ExpiresAt, @CreatedAt, @ReferralId, @ReferralRuleId, @SourceCreditId, @TransferId,
 @CreditSource, @CreditSourceDetail, @ReturnedFromCreditId, @SourceType, @Floor, @ReceiveCurrency);",
                row,
                cancellationToken: cancellationToken);
        }

        private async Task<bool> IsBlockedAsync(string userId, CancellationToken cancellationToken)
        {
            var row = await _dapper.QueryFirstOrDefaultAsync<BlockRow>(@"
SELECT UserId, StrikeCount, Blocked, Reason, UpdatedAt
FROM dbo.RewardBonusBlock WHERE UserId = @userId;",
                new { userId },
                cancellationToken: cancellationToken);
            return row?.Blocked == true;
        }

        // STAGING-SYNC: "REFERRAL" -> REFERRAL, "SCHEME"/"BONUS" -> BONUS (all non-referral), blank -> null (unrestricted),
        // anything else -> that exact (upper-cased) CreditSource.
        private static string NormalizeCreditSourceFilter(string raw)
        {
            var s = (raw ?? string.Empty).Trim().ToUpperInvariant();
            if (s.Length == 0) return null;
            if (s == "SCHEME" || s == "BONUS") return "BONUS";
            return s;
        }

        private static bool SourceOk(LedgerRow row, string creditSourceFilter)
        {
            if (creditSourceFilter == null) return true;
            var isReferral = string.Equals(row.CreditSource, "REFERRAL", StringComparison.OrdinalIgnoreCase);
            if (creditSourceFilter == "REFERRAL") return isReferral;
            if (creditSourceFilter == "BONUS") return !isReferral;
            return string.Equals(row.CreditSource, creditSourceFilter, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnrichSources(List<LedgerRow> rows)
        {
            // Duplicate ledger ids must not crash wallet/history (ToDictionary throws).
            var byId = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Id))
                .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.CreditSource))
                {
                    if (!string.IsNullOrWhiteSpace(row.SourceCreditId)
                        && byId.TryGetValue(row.SourceCreditId, out var parent)
                        && !string.IsNullOrWhiteSpace(parent.CreditSource))
                    {
                        row.CreditSource = parent.CreditSource;
                        row.CreditSourceDetail = parent.CreditSourceDetail;
                    }
                    else
                    {
                        var inferred = RewardCreditLabels.InferFromReason(row.ReasonCode, row.ReferenceId);
                        row.CreditSource = inferred.source;
                        row.CreditSourceDetail = inferred.detail;
                    }
                }
            }
        }

        private static JObject BuildBalance(string currency, List<LedgerRow> rows)
        {
            decimal earned = 0, used = 0, expired = 0, removed = 0, returned = 0;
            foreach (var row in rows)
            {
                var t = (row.Type ?? string.Empty).ToUpperInvariant();
                switch (t)
                {
                    case "EARNED":
                        earned += row.Amount;
                        break;
                    case "RETURNED":
                        returned += row.Amount;
                        earned += row.Amount;
                        break;
                    case "APPLIED":
                        used += Math.Abs(row.Amount);
                        break;
                    case "EXPIRED":
                        expired += Math.Abs(row.Amount);
                        break;
                    case "VOIDED":
                        removed += Math.Abs(row.Amount);
                        break;
                }
            }

            var available = RemainingCredits(rows).Sum(x => x.remaining);
            used = Math.Max(0, used - returned);

            var bySource = new JArray();
            foreach (var group in new[] { "REFERRAL", "SCHEME", "MANUAL" })
            {
                var slice = rows.Where(r =>
                    string.Equals(r.CreditSource, group, StringComparison.OrdinalIgnoreCase)).ToList();
                if (slice.Count == 0) continue;
                decimal gEarned = 0, gUsed = 0, gExpired = 0, gRemoved = 0, gReturned = 0;
                foreach (var row in slice)
                {
                    var t = (row.Type ?? string.Empty).ToUpperInvariant();
                    if (t == "EARNED") gEarned += row.Amount;
                    else if (t == "RETURNED") { gReturned += row.Amount; gEarned += row.Amount; }
                    else if (t == "APPLIED") gUsed += Math.Abs(row.Amount);
                    else if (t == "EXPIRED") gExpired += Math.Abs(row.Amount);
                    else if (t == "VOIDED") gRemoved += Math.Abs(row.Amount);
                }

                var gAvailable = RemainingCredits(slice).Sum(x => x.remaining);

                // STAGING-SYNC: surface the credit Floor / corridor for this source group (additive fields).
                var gCredits = slice.Where(r =>
                    string.Equals(r.Type, "EARNED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.Type, "RETURNED", StringComparison.OrdinalIgnoreCase)).ToList();
                var gFloor = gCredits.Count == 0 ? 0m : gCredits.Max(r => r.Floor ?? 0m);
                var gReceive = gCredits
                    .Select(r => NormalizeCurrency(r.ReceiveCurrency))
                    .FirstOrDefault(c => c != null);

                bySource.Add(new JObject
                {
                    ["credit_source"] = group,
                    ["label"] = RewardCreditLabels.SourceGroup(group),
                    ["earned"] = gEarned,
                    ["used"] = Math.Max(0, gUsed - gReturned),
                    ["expired"] = gExpired,
                    ["removed"] = gRemoved,
                    ["available"] = gAvailable,
                    ["currency"] = currency,
                    ["floor"] = gFloor,
                    ["receive_currency"] = gReceive
                });
            }

            return new JObject
            {
                ["currency"] = currency,
                ["available"] = available,
                ["earned"] = earned,
                ["used"] = used,
                ["expired"] = expired,
                ["outstanding_debt"] = 0m,
                ["used_transfer_count"] = rows.Count(r =>
                    string.Equals(r.Type, "APPLIED", StringComparison.OrdinalIgnoreCase)),
                ["referral_credit_count"] = rows.Count(r =>
                    string.Equals(r.CreditSource, "REFERRAL", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.Type, "EARNED", StringComparison.OrdinalIgnoreCase)),
                ["other_credit_count"] = rows.Count(r =>
                    !string.Equals(r.CreditSource, "REFERRAL", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.Type, "EARNED", StringComparison.OrdinalIgnoreCase)),
                ["by_source"] = bySource
            };
        }

        private static List<(LedgerRow row, decimal remaining)> RemainingCredits(List<LedgerRow> rows)
        {
            var earned = rows
                .Where(r =>
                    string.Equals(r.Type, "EARNED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.Type, "RETURNED", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.ExpiresAt ?? DateTime.MaxValue)
                .ThenBy(r => r.CreatedAt)
                .ToList();

            var spent = rows
                .Where(r =>
                    string.Equals(r.Type, "APPLIED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.Type, "EXPIRED", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r.Type, "VOIDED", StringComparison.OrdinalIgnoreCase))
                .GroupBy(r => r.SourceCreditId ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.Sum(x => Math.Abs(x.Amount)), StringComparer.OrdinalIgnoreCase);

            var result = new List<(LedgerRow, decimal)>();
            foreach (var row in earned)
            {
                spent.TryGetValue(row.Id, out var used);
                // RETURNED rows don't have their own spend bucket keyed to themselves as source for apply
                var rem = row.Amount - used;
                if (string.Equals(row.Type, "RETURNED", StringComparison.OrdinalIgnoreCase))
                {
                    // Treat returned as new credit with its own id
                    rem = row.Amount - used;
                }

                if (rem > 0 && (row.ExpiresAt == null || row.ExpiresAt > DateTime.UtcNow))
                {
                    result.Add((row, rem));
                }
            }

            return result;
        }

        // STAGING-SYNC: corridor rule - a credit with a ReceiveCurrency is only usable when the apply request
        // names the same receive currency (case-insensitive). Credits without ReceiveCurrency are always usable.
        private static bool CorridorOk(LedgerRow row, string receiveCurrency)
        {
            var credit = NormalizeCurrency(row?.ReceiveCurrency);
            if (credit == null) return true;
            return receiveCurrency != null
                && string.Equals(credit, receiveCurrency, StringComparison.OrdinalIgnoreCase);
        }

        // STAGING-SYNC: Floor rule - credit with Floor > 0 is only usable when the transfer amount basis
        // (transfer_amount ?? send_amount) reaches the Floor. No basis supplied => not enforced.
        private static bool FloorOk(LedgerRow row, decimal? floorBasis)
        {
            var floor = row?.Floor ?? 0m;
            if (floor <= 0m) return true;
            if (!floorBasis.HasValue) return true;
            return floorBasis.Value >= floor;
        }

        private static string NormalizeCurrency(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

        private static JObject ToHistoryRow(LedgerRow row)
        {
            var label = RewardCreditLabels.SourceLabel(
                row.CreditSource, row.CreditSourceDetail, row.Notes, row.Notes, null);
            return new JObject
            {
                ["id"] = row.Id,
                ["user_id"] = row.UserId,
                ["amount"] = row.Amount,
                ["type"] = row.Type,
                ["currency"] = row.Currency,
                ["scheme_id"] = row.SchemeId,
                ["scheme_name"] = row.Notes,
                ["reference_id"] = row.ReferenceId,
                ["reason_code"] = row.ReasonCode,
                ["notes"] = row.Notes,
                ["admin_user"] = row.AdminUser,
                ["expires_at"] = row.ExpiresAt,
                ["expires_on"] = row.ExpiresAt,
                ["created_at"] = row.CreatedAt,
                ["referral_id"] = row.ReferralId,
                ["referral_rule_id"] = row.ReferralRuleId,
                ["source_credit_id"] = row.SourceCreditId,
                ["transfer_id"] = row.TransferId,
                ["credit_source"] = row.CreditSource,
                ["credit_source_detail"] = row.CreditSourceDetail,
                ["credit_source_label"] = label,
                ["source_label"] = label,
                ["source_type"] = row.SourceType ?? "BONUS",
                ["returned_from_credit_id"] = row.ReturnedFromCreditId,
                // STAGING-SYNC: credit restrictions (additive fields)
                ["floor"] = row.Floor ?? 0m,
                ["receive_currency"] = NormalizeCurrency(row.ReceiveCurrency)
            };
        }

        private static decimal AvailableFromWallet(JObject wallet, string currency)
        {
            var balances = wallet?["balances"] as JArray;
            if (balances == null) return 0m;
            foreach (var row in balances)
            {
                if (string.Equals(Text(row["currency"]), currency, StringComparison.OrdinalIgnoreCase))
                {
                    return Money(row["available"]);
                }
            }

            return balances.Count > 0 ? Money(balances[0]["available"]) : 0m;
        }

        // STAGING-SYNC: optional machine-readable code (additive "code" field; existing errors unchanged).
        private static JObject Err(int status, string message, string code = null)
        {
            var o = new JObject
            {
                ["error"] = message,
                ["status"] = status,
                ["_httpStatus"] = status
            };
            if (!string.IsNullOrWhiteSpace(code))
            {
                o["code"] = code;
            }

            return o;
        }

        private static decimal Money(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0m;
            if (token.Type is JTokenType.Float or JTokenType.Integer) return token.Value<decimal>();
            return decimal.TryParse(token.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
                ? n
                : 0m;
        }

        // STAGING-SYNC: like Money, but distinguishes "absent / not a number" (null) from 0.
        private static decimal? MoneyOrNull(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type is JTokenType.Float or JTokenType.Integer) return token.Value<decimal>();
            return decimal.TryParse(token.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n)
                ? n
                : (decimal?)null;
        }

        private static string Text(JToken token) =>
            token == null || token.Type == JTokenType.Null ? null : token.ToString();

        private sealed class BlockRow
        {
            public string UserId { get; set; }
            public int StrikeCount { get; set; }
            public bool Blocked { get; set; }
            public string Reason { get; set; }
            public DateTime UpdatedAt { get; set; }
        }

        // STAGING-SYNC: result row of ApplyAtomicSql.
        private sealed class ApplyResultRow
        {
            public int Status { get; set; }
            public decimal Applied { get; set; }
        }

        public sealed class LedgerRow
        {
            public string Id { get; set; }
            public string UserId { get; set; }
            public decimal Amount { get; set; }
            public string Type { get; set; }
            public string Currency { get; set; }
            public string SchemeId { get; set; }
            public string ReferenceId { get; set; }
            public string ReasonCode { get; set; }
            public string Notes { get; set; }
            public string AdminUser { get; set; }
            public DateTime? ExpiresAt { get; set; }
            public DateTime CreatedAt { get; set; }
            public string ReferralId { get; set; }
            public string ReferralRuleId { get; set; }
            public string SourceCreditId { get; set; }
            public string TransferId { get; set; }
            public string CreditSource { get; set; }
            public string CreditSourceDetail { get; set; }
            public string ReturnedFromCreditId { get; set; }
            public string SourceType { get; set; }

            // STAGING-SYNC: optional credit restrictions (new nullable columns on dbo.RewardCreditLedger).
            public decimal? Floor { get; set; }
            public string ReceiveCurrency { get; set; }
        }
    }
}
