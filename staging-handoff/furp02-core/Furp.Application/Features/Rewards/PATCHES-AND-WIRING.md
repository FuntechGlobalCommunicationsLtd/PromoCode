# PATCHES-AND-WIRING — surgical diffs for `furp02-core`

> Companion to the three drop-in files (`RewardReferralService.cs`, `RewardWalletService.cs`, `RewardCheckout.cs`) and `Database/Migrations/UpgradeRewardEngineStagingSync.sql`.
> Line numbers refer to the `furp02-core` `pri_prod` dump the package was written against; **match on the quoted anchor text, not the line number**.
> Every edit is additive or a one-line tightening. Nothing here renames a route, table, column, flag or JSON field.

| § | File | Edit size | Behaviour change for existing callers |
| :--- | :--- | :--- | :--- |
| 1 | `Engine/RewardSchema.cs` | append DDL | none (new table/nullable columns) |
| 2 | `Engine/LocalRewardEngine.cs` | +3 routes, 3 tiny arg changes | `referral/visits` response now also carries `trackingId` etc. |
| 3 | `RewardCustomerCommands.cs` | relax code length, +fields | referral codes 3–32 chars (was 6–12) — **see conflict note** |
| 4 | `RewardCommands.cs` | 1 SQL line | `promoSavings` no longer counts `RELEASED` (bug fix) |
| 5 | `Engine/RewardPromoService.cs` | +1 check, atomic redeem | affiliate-restricted codes enforced; no duplicate redeems |
| 6 | `Engine/RewardSchemeAwards.cs` | +`paymentMethods` check | schemes with `paymentMethods` now honoured |
| 7 | `CreateRetailTransactionCommandHandler.cs` | 1 line | `INCREASE_RECEIVE` boost now reaches the payout (bug fix) |

---

## 1. `Furp.Application/Features/Rewards/Engine/RewardSchema.cs`

Append **inside** `DdlSql`, immediately after the `dbo.RewardRateAuditLog` block and **before** the closing `";`.
(The string is a C# verbatim `@"..."` literal; this DDL contains no double quotes, so no escaping is needed.)

```diff
     CREATE INDEX IX_RewardRateAuditLog_Created
         ON dbo.RewardRateAuditLog (CreatedAt DESC);
 END;
+
+-- ===== STAGING-SYNC (mirror of Database/Migrations/UpgradeRewardEngineStagingSync.sql) =====
+IF OBJECT_ID(N'dbo.RewardReferralVisit', N'U') IS NULL
+BEGIN
+    CREATE TABLE dbo.RewardReferralVisit (
+        Id NVARCHAR(64) NOT NULL CONSTRAINT PK_RewardReferralVisit PRIMARY KEY,
+        TrackingId NVARCHAR(64) NULL,
+        Code NVARCHAR(32) NULL,
+        ReferrerId NVARCHAR(64) NULL,
+        RuleId NVARCHAR(64) NULL,
+        Currency CHAR(3) NULL,
+        ReceiveCurrency CHAR(3) NULL,
+        VisitorId NVARCHAR(64) NULL,
+        DeviceType NVARCHAR(32) NULL,
+        OperatingSystem NVARCHAR(32) NULL,
+        SourceChannel NVARCHAR(32) NOT NULL CONSTRAINT DF_RewardReferralVisit_SourceChannel DEFAULT (N'LINK'),
+        AttributionStatus NVARCHAR(32) NOT NULL CONSTRAINT DF_RewardReferralVisit_AttributionStatus DEFAULT (N'CLICKED'),
+        StoreTarget NVARCHAR(32) NULL,
+        ResolvedAt DATETIME2 NULL,
+        LinkedReferralId NVARCHAR(64) NULL,
+        LinkedRefereeId NVARCHAR(64) NULL,
+        ExpiresAt DATETIME2 NULL,
+        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_RewardReferralVisit_CreatedAt DEFAULT (SYSUTCDATETIME())
+    );
+    CREATE UNIQUE INDEX UX_RewardReferralVisit_TrackingId
+        ON dbo.RewardReferralVisit (TrackingId) WHERE TrackingId IS NOT NULL;
+    CREATE INDEX IX_RewardReferralVisit_CodeVisitor
+        ON dbo.RewardReferralVisit (Code, VisitorId, CreatedAt DESC);
+    CREATE INDEX IX_RewardReferralVisit_Referrer
+        ON dbo.RewardReferralVisit (ReferrerId, CreatedAt DESC);
+    CREATE INDEX IX_RewardReferralVisit_LinkedReferee
+        ON dbo.RewardReferralVisit (LinkedRefereeId) WHERE LinkedRefereeId IS NOT NULL;
+END;
+
+IF COL_LENGTH(N'dbo.RewardCreditLedger', N'Floor') IS NULL
+    ALTER TABLE dbo.RewardCreditLedger ADD [Floor] DECIMAL(18,2) NULL;
+IF COL_LENGTH(N'dbo.RewardCreditLedger', N'ReceiveCurrency') IS NULL
+    ALTER TABLE dbo.RewardCreditLedger ADD ReceiveCurrency CHAR(3) NULL;
+IF COL_LENGTH(N'dbo.RewardTransferFact', N'PaymentMethod') IS NULL
+    ALTER TABLE dbo.RewardTransferFact ADD PaymentMethod NVARCHAR(50) NULL;
+
+-- Unique index only when live data has no duplicates, so bootstrap can never fail on dirty data.
+IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RewardPromoRedemption_Code_Transfer_Redeemed' AND object_id = OBJECT_ID(N'dbo.RewardPromoRedemption'))
+   AND NOT EXISTS (SELECT 1 FROM dbo.RewardPromoRedemption WHERE Status = N'REDEEMED' AND TransferId IS NOT NULL
+                   GROUP BY PromoCode, TransferId HAVING COUNT(*) > 1)
+    EXEC (N'CREATE UNIQUE INDEX UX_RewardPromoRedemption_Code_Transfer_Redeemed
+                ON dbo.RewardPromoRedemption (PromoCode, TransferId)
+                WHERE Status = N''REDEEMED'' AND TransferId IS NOT NULL;');
+
+IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardPromoRedemption_CodeStatusUser' AND object_id = OBJECT_ID(N'dbo.RewardPromoRedemption'))
+    CREATE INDEX IX_RewardPromoRedemption_CodeStatusUser
+        ON dbo.RewardPromoRedemption (PromoCode, Status, UserId)
+        INCLUDE (Discount, Currency, TransferId);
 ";
```

> [!WARNING]
> In the C# verbatim string, the inner `N''REDEEMED''` (doubled single quotes inside `EXEC (N'...')`) is **T-SQL** escaping and must stay exactly as written; no extra C# escaping is required.
>
> `RewardReferralService.StagingDdlSql` and the wallet's private `EnsureAsync` already self-apply the same `Floor`/`ReceiveCurrency`/visit-table DDL lazily (idempotent), so the order of deployment vs migration does not matter. Once this section is in `RewardSchema.cs` those lazy calls simply become no-ops.

---

## 2. `Furp.Application/Features/Rewards/Engine/LocalRewardEngine.cs`

Wire the new engine routes inside `DispatchAsync` (the `// Referral` block). All three new routes are `GET`/`POST`, so they pass IIS and the `mito-admin` verb tunnel unchanged.

### 2.1 CSV exports + visit resolve (new, after `referral/performance`)

```diff
             if (path.Equals("referral/performance", StringComparison.OrdinalIgnoreCase) && method == "GET")
                 return await _referral.PerformanceAsync(cancellationToken);
+
+            // STAGING-SYNC: CSV exports (mito-admin "Export CSV") — filters ride on the query string
+            if (path.Equals("referral/tracking.csv", StringComparison.OrdinalIgnoreCase) && method == "GET")
+                return CsvResult(
+                    await _referral.TrackingCsvAsync(QueryToJObject(query), cancellationToken),
+                    "referral-tracking.csv");
+            if (path.Equals("referral/performance.csv", StringComparison.OrdinalIgnoreCase) && method == "GET")
+                return CsvResult(
+                    await _referral.PerformanceCsvAsync(QueryToJObject(query), cancellationToken),
+                    "referral-performance.csv");
+
+            // STAGING-SYNC: Android Play Install Referrer / Universal Link resolve (+ iOS/Android store hand-off)
+            if ((path.Equals("referral/visits/resolve", StringComparison.OrdinalIgnoreCase)
+                 || path.Equals("referral/resolve", StringComparison.OrdinalIgnoreCase))
+                && method == "POST")
+                return await _referral.ResolveTrackingAsync(body, cancellationToken);
+            if (path.StartsWith("referral/clicks/", StringComparison.OrdinalIgnoreCase)
+                && path.EndsWith("/handoff", StringComparison.OrdinalIgnoreCase) && method == "POST")
+            {
+                var mid = path["referral/clicks/".Length..];
+                var trackingId = Uri.UnescapeDataString(mid[..^"/handoff".Length].Trim('/'));
+                return await _referral.HandoffAsync(trackingId, body, cancellationToken);
+            }
```

Add two private helpers next to `Money`/`Text` at the bottom of the class:

```csharp
// STAGING-SYNC: CSV answers travel as { csv, filename, content_type } so they pass through RewardHttpResult.Json
// unchanged. IMPORTANT: if the existing `credits/export` route (_ledger.ExportCsvAsync) already returns a different
// shape for raw CSV, copy THAT shape here instead and keep the HTTP layer untouched.
private static JObject CsvResult(string csv, string filename) =>
    new JObject
    {
        ["csv"] = csv,
        ["filename"] = filename,
        ["content_type"] = RewardReferralService.CsvContentType
    };

private static JObject QueryToJObject(IDictionary<string, string> query)
{
    var o = new JObject();
    if (query == null) return o;
    foreach (var kv in query) o[kv.Key] = kv.Value;
    return o;
}
```

### 2.2 Visit now returns the tracking id (existing route, same path)

```diff
             if (path.Equals("referral/visits", StringComparison.OrdinalIgnoreCase) && method == "POST")
             {
-                await _referral.VisitAsync(body, cancellationToken);
-                return new JObject { ["success"] = true };
+                var visit = await _referral.VisitAsync(body, cancellationToken);   // STAGING-SYNC
+                visit["success"] = true;                                            // keep the old field
+                return visit;
             }
```

> `VisitAsync` was `Task` and is now `Task<JObject>`; other awaiting callers keep compiling.
> There is no HTTP header access inside `LocalRewardEngine`; if the gateway can forward the browser `User-Agent`, put it in `body["user_agent"]` and `RewardReferralService.VisitAsync` will use it for device/OS detection (otherwise device fields stay `UNKNOWN` unless the caller sends `device_type` / `operating_system`).

### 2.3 Offer / code lookups accept the corridor

```diff
             if (path.Equals("referral/offer", StringComparison.OrdinalIgnoreCase) && method == "GET")
                 return await _referral.GetOfferAsync(
                     Get(query, "customer_id") ?? Get(query, "customerId"),
                     Get(query, "currency"),
+                    Get(query, "receive_currency") ?? Get(query, "receiveCurrency"),   // STAGING-SYNC
                     cancellationToken);
...
-                return await _referral.GetCodeAsync(code, Get(query, "currency"), cancellationToken);
+                return await _referral.GetCodeAsync(code, Get(query, "currency"),
+                    Get(query, "receive_currency") ?? Get(query, "receiveCurrency"), cancellationToken);   // STAGING-SYNC
```

(The old 3-argument overloads still exist and delegate, so any other caller is unaffected.)

### 2.4 Transfer events carry the payment method

No change in `LocalRewardEngine`; the body already reaches `TransferEventAsync`. See §6 for how `payment_method` is persisted.

### 2.5 Optional: daily sweep for the qualification window

`RewardReferralService.ExpireOverdueAsync(ct)` closes referrals past `qualification_window_days`. `TransferEventAsync` already enforces the window when a transfer arrives; schedule the sweep only if you also want unmatched referrals to flip to `EXPIRED` without waiting for a transfer (e.g. from the existing `bonus/run-jobs` handler: `await _referral.ExpireOverdueAsync(ct);`).

---

## 3. `Furp.Application/Features/Rewards/RewardCustomerCommands.cs`

### 3.1 `NormaliseCode` — accept 3–32 characters

Prototype codes carry a currency suffix and admin-created vanity codes are shorter than 6 characters.

```diff
         public static string NormaliseCode(string raw)
         {
             if (string.IsNullOrWhiteSpace(raw)) return null;
             var code = new string(raw.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
-            if (code.Length < 6 || code.Length > 12) return null;
+            if (code.Length < 3 || code.Length > 32) return null;   // STAGING-SYNC: was 6..12
             return code;
         }
```

and the three user-facing messages:

```diff
-                return Result<ReferralCodeCheckDto>.Fail("Referral codes are 6–12 letters and numbers.");
+                return Result<ReferralCodeCheckDto>.Fail("Referral codes are 3–32 letters and numbers.");
...
-                return Result<string>.Fail("Referral codes are 6–12 letters and numbers.");
+                return Result<string>.Fail("Referral codes are 3–32 letters and numbers.");
```

> [!NOTE]
> **Conflict detected (validation rule)** — Existing behaviour: codes outside 6–12 chars are rejected before the engine is called. New requirement: engine codes may be 3–32 chars. Impact: any client-side or other server-side check still enforcing 6–12 will disagree. Recommended: ship this relaxation together with the frontend `referralStore`/`/ref/[code]` change (already 3–32 in the Rhemito-UI mirror). The engine remains the final judge (`INVALID_FORMAT` / `NOT_FOUND`).

### 3.2 `ReferralCodeCheckDto` + query — additive fields

```diff
     public class ValidateReferralCodeQuery : IRequest<Result<ReferralCodeCheckDto>>
     {
         public string Code { get; set; }
         public string Currency { get; set; }
+        public string ReceiveCurrency { get; set; }   // STAGING-SYNC
+        public int? RuleId { get; set; }              // STAGING-SYNC (?rule=)
     }

     public class ReferralCodeCheckDto
     {
         ...
         public decimal RefereeReward { get; set; }
+        public decimal ReferrerReward { get; set; }    // STAGING-SYNC
         public decimal MinimumAmount { get; set; }
         public int WindowDays { get; set; }
         public string Currency { get; set; }
+        public string ReceiveCurrency { get; set; }    // STAGING-SYNC
+        public int? RuleId { get; set; }               // STAGING-SYNC
     }
```

In `Handle(ValidateReferralCodeQuery ...)`:

```diff
-            var call = await _gateway.GetAsync(
-                "/api/referral/codes/" + Uri.EscapeDataString(code) + "?currency=" + Uri.EscapeDataString(currency),
-                cancellationToken);
+            var qs = "?currency=" + Uri.EscapeDataString(currency);
+            if (!string.IsNullOrWhiteSpace(request.ReceiveCurrency))
+                qs += "&receive_currency=" + Uri.EscapeDataString(request.ReceiveCurrency.Trim().ToUpperInvariant());
+            if (request.RuleId.HasValue && request.RuleId.Value > 0)
+                qs += "&rule_id=" + request.RuleId.Value;
+            var call = await _gateway.GetAsync("/api/referral/codes/" + Uri.EscapeDataString(code) + qs, cancellationToken);
...
                 RefereeReward = RewardEngineGateway.Money(offer?["referee_reward"]),
+                ReferrerReward = RewardEngineGateway.Money(offer?["referrer_reward"]),   // STAGING-SYNC
                 MinimumAmount = RewardEngineGateway.Money(offer?["floor"]),
                 WindowDays = (int)RewardEngineGateway.Money(offer?["qualification_window_days"]),
-                Currency = RewardEngineGateway.Text(offer?["currency"])
+                Currency = RewardEngineGateway.Text(offer?["currency"]),
+                ReceiveCurrency = RewardEngineGateway.Text(offer?["receive_currency"]),  // STAGING-SYNC
+                RuleId = int.TryParse(RewardEngineGateway.Text(call.Json?["rule_id"] ?? offer?["rule_id"]), out var rid) ? rid : (int?)null
```

### 3.3 Visit — return the tracking id (`Result<bool>` → `Result<ReferralVisitDto>`)

> [!WARNING]
> **Conflict detected (wire contract)** — Existing behaviour: `POST Account/Rewards/Visit` returns `Result<bool>` (`data: true/false`). New requirement: the Android Play Install Referrer / iOS hand-off needs `trackingId`, `playStoreUrl`, `appStoreUrl` back. Impact: any consumer that reads `data === true` breaks. Recommended: change to the DTO below and read `data?.tracked` instead (the Rhemito-UI mirror already returns `{ tracked, trackingId, playStoreUrl, appStoreUrl }`). Alternative: keep `Result<bool>` and add a *new* route `POST Account/Rewards/Visit/Track` returning the DTO. **Do not apply 3.3 until this is approved.**

```diff
+    public class ReferralVisitDto
+    {
+        public bool Tracked { get; set; }
+        public string TrackingId { get; set; }
+        public string PlayStoreUrl { get; set; }
+        public string AppStoreUrl { get; set; }
+    }
+
-    public class RecordReferralVisitCommand : IRequest<Result<bool>>
+    public class RecordReferralVisitCommand : IRequest<Result<ReferralVisitDto>>
     {
         public string Code { get; set; }
         public string VisitorId { get; set; }
         public string ReceiveCurrency { get; set; }
+        public int? RuleId { get; set; }   // STAGING-SYNC
     }
```

Update the interface list (`IRequestHandler<RecordReferralVisitCommand, Result<ReferralVisitDto>>`) and the handler:

```diff
-        public async Task<Result<bool>> Handle(RecordReferralVisitCommand request, CancellationToken cancellationToken)
+        public async Task<Result<ReferralVisitDto>> Handle(RecordReferralVisitCommand request, CancellationToken cancellationToken)
         {
             var code = NormaliseCode(request.Code);
             if (code == null || !_gateway.IsConfigured)
             {
-                return Result<bool>.Success(false, "Success");
+                return Result<ReferralVisitDto>.Success(new ReferralVisitDto { Tracked = false }, "Success");
             }
             ...
-            await _gateway.PostAsync("/api/referral/visits", new { code, visitor_id = visitor, receive_currency = receive }, cancellationToken);
-            return Result<bool>.Success(true, "Success");
+            var call = await _gateway.PostAsync("/api/referral/visits", new
+            {
+                code,
+                visitor_id = visitor,
+                receive_currency = receive,
+                rule_id = request.RuleId
+            }, cancellationToken);
+            return Result<ReferralVisitDto>.Success(new ReferralVisitDto
+            {
+                Tracked = true,
+                TrackingId = RewardEngineGateway.Text(call.Json?["trackingId"] ?? call.Json?["tracking_id"]),
+                PlayStoreUrl = RewardEngineGateway.Text(call.Json?["playStoreUrl"] ?? call.Json?["play_store_url"]),
+                AppStoreUrl = RewardEngineGateway.Text(call.Json?["appStoreUrl"] ?? call.Json?["app_store_url"])
+            }, "Success");
         }
```

(Property names follow the engine's `VisitAsync` result keys. Controller binding is unchanged: the command is bound directly, so the new `RuleId` property binds automatically.)

### 3.4 Link — pass `trackingId` and `ruleId`

```diff
     public class LinkReferralCommand : IRequest<Result<string>>
     {
         public string Code { get; set; }
         public string ReceiveCurrency { get; set; }
+        public string TrackingId { get; set; }   // STAGING-SYNC
+        public int? RuleId { get; set; }         // STAGING-SYNC
     }
...
             var linked = await _gateway.PostAsync("/api/referral/referrals", new
             {
                 code,
                 referee_id = user.UserId.ToString(),
                 customer_id = user.UserId.ToString(),
+                tracking_id = string.IsNullOrWhiteSpace(request.TrackingId) ? null : request.TrackingId.Trim().ToUpperInvariant(),
+                rule_id = request.RuleId,
+                registration_channel = "WEB",
                 referee = new { id = user.UserId.ToString(), send_currency = currency, receive_currency = receive }
             }, cancellationToken);
```

(`CreateReferralAsync` in the drop-in already reads `tracking_id`, `rule_id`, `registration_channel` and links the visit row.)

---

## 4. `Furp.Application/Features/Rewards/RewardCommands.cs` — `PromoSavingsAsync` counts `REDEEMED` only

```diff
 SELECT Id, PromoCode, Discount, Currency, CreatedAt
 FROM dbo.RewardPromoRedemption
-WHERE Status IN (N'REDEEMED', N'RELEASED')
+WHERE Status = N'REDEEMED'
   AND (
         UserId = @userId
```

`RELEASED` rows are returned/cancelled transfers; counting them inflated `promoSavings`. Only `Status = N'REDEEMED'` is real savings. (If the older variant of this file in your branch has no such `IN (...)` list, ensure the equivalent `Status` predicate is `= N'REDEEMED'`.) The new `IX_RewardPromoRedemption_CodeStatusUser` covers this query.

---

## 5. `Furp.Application/Features/Rewards/Engine/RewardPromoService.cs`

### 5.1 `AffiliateAllowed` — enforce `restrictions.affiliates`

`RewardCheckout` now sends `affiliate_id` + `site_key` on **validate and redeem**; the service must honour `restrictions.affiliates`.

In `ValidateAsync`, right after `var dest = ...;` add:

```csharp
// STAGING-SYNC: tenant / affiliate scoping (restrictions.affiliates)
var affiliateKey = Text(body?["affiliate_id"] ?? body?["affiliateId"] ?? body?["site_key"] ?? body?["siteKey"]);
```

and right after the existing `ruleError` block:

```diff
             var ruleError = await RuleErrorAsync(cfg, row.Code, amount, currency, userId, transferId, paymentMethod, source, dest, cancellationToken);
             if (ruleError != null)
             {
                 return new JObject { ["valid"] = false, ["error"] = ruleError, ["_httpStatus"] = 400 };
             }
+
+            if (!AffiliateAllowed(cfg, affiliateKey))
+            {
+                return new JObject
+                {
+                    ["valid"] = false,
+                    ["error"] = "This promo code isn't available on this site.",
+                    ["_httpStatus"] = 400
+                };
+            }
```

New helper next to `CorridorAllowed`:

```csharp
// STAGING-SYNC: no list (or no caller key) = allowed, exactly as before. Match is case-insensitive.
private static bool AffiliateAllowed(JObject cfg, string affiliateKey)
{
    var restrictions = cfg["restrictions"] as JObject;
    var list = restrictions?["affiliates"] as JArray ?? cfg["affiliates"] as JArray;
    if (list == null || list.Count == 0) return true;
    if (string.IsNullOrWhiteSpace(affiliateKey)) return true;
    var key = affiliateKey.Trim();
    return list.Any(item =>
        string.Equals((Text(item) ?? string.Empty).Trim(), key, StringComparison.OrdinalIgnoreCase));
}
```

### 5.2 `RedeemAsync` — atomic redeem with `sp_getapplock`

Replace the *check-then-insert* (the `already` count + `INSERT`) with one T-SQL batch that takes an applock per promo code, re-checks the global / per-user limits **inside** the lock, and inserts once. Keep the `ValidateAsync` call at the top unchanged.

```diff
-            if (!string.IsNullOrWhiteSpace(transferId))
-            {
-                var already = await _dapper.ExecuteScalarAsync<int>(@"
-SELECT COUNT(1) FROM dbo.RewardPromoRedemption
-WHERE PromoCode = @code AND TransferId = @transferId AND Status = N'REDEEMED';", new { code, transferId }, cancellationToken: cancellationToken);
-                if (already > 0)
-                {
-                    return new JObject { ["success"] = true, ["discount"] = discount };
-                }
-            }
-
-            await _dapper.ExecuteAsync(@"
-INSERT INTO dbo.RewardPromoRedemption
-(Id, PromoCode, UserId, TransferId, Discount, Currency, Status)
-VALUES (@Id, @PromoCode, @UserId, @TransferId, @Discount, @Currency, N'REDEEMED');", new { ... }, cancellationToken: cancellationToken);
-
-            return new JObject { ["success"] = true, ["discount"] = discount };
+            // STAGING-SYNC: serialise redeems per code. 1 = inserted, 0 = this transfer already redeemed it (idempotent),
+            // -1 = a limit was reached by a concurrent redeem, -2 = lock timeout.
+            var cfgRow = await _dapper.QueryFirstOrDefaultAsync<PromoRow>(
+                "SELECT Id, Code, Status, ConfigJson FROM dbo.RewardPromoCode WHERE Code = @code;",
+                new { code }, cancellationToken: cancellationToken);
+            var redeemCfg = Parse(cfgRow?.ConfigJson);
+            var globalLimit = Limit(redeemCfg["usage_limit_global"] ?? redeemCfg["usageLimitGlobal"]);
+            var perUserLimit = Limit(redeemCfg["usage_limit_per_user"] ?? redeemCfg["usageLimitPerUser"]);
+
+            var outcome = await _dapper.ExecuteScalarAsync<int>(@"
+SET XACT_ABORT ON;
+BEGIN TRAN;
+DECLARE @rc INT;
+EXEC @rc = sp_getapplock @Resource = @lockKey, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 10000;
+IF @rc < 0 BEGIN ROLLBACK TRAN; SELECT -2; RETURN; END
+
+IF @transferId IS NOT NULL AND EXISTS (
+    SELECT 1 FROM dbo.RewardPromoRedemption
+    WHERE PromoCode = @code AND TransferId = @transferId AND Status = N'REDEEMED')
+BEGIN COMMIT TRAN; SELECT 0; RETURN; END
+
+IF @globalLimit >= 0 AND (
+    SELECT COUNT(1) FROM dbo.RewardPromoRedemption r
+    WHERE r.PromoCode = @code AND r.Status = N'REDEEMED'
+      AND (@transferId IS NULL OR r.TransferId <> @transferId)" + ActiveRedemptionSqlFilter("r") + @") >= @globalLimit
+BEGIN ROLLBACK TRAN; SELECT -1; RETURN; END
+
+IF @perUserLimit >= 0 AND (
+    SELECT COUNT(1) FROM dbo.RewardPromoRedemption r
+    WHERE r.PromoCode = @code AND r.Status = N'REDEEMED' AND r.UserId = @UserId
+      AND (@transferId IS NULL OR r.TransferId <> @transferId)" + ActiveRedemptionSqlFilter("r") + @") >= @perUserLimit
+BEGIN ROLLBACK TRAN; SELECT -1; RETURN; END
+
+INSERT INTO dbo.RewardPromoRedemption (Id, PromoCode, UserId, TransferId, Discount, Currency, Status)
+VALUES (@Id, @code, @UserId, @transferId, @Discount, @Currency, N'REDEEMED');
+COMMIT TRAN;
+SELECT 1;",
+                new
+                {
+                    lockKey = "reward-promo:" + code,
+                    code,
+                    transferId,
+                    UserId = userId,
+                    Id = "red_" + Guid.NewGuid().ToString("N"),
+                    Discount = discount,
+                    Currency = currency,
+                    globalLimit,
+                    perUserLimit
+                },
+                cancellationToken: cancellationToken);
+
+            if (outcome == -2)
+                return new JObject { ["valid"] = false, ["error"] = "Promo codes are busy. Please try again.", ["_httpStatus"] = 409 };
+            if (outcome == -1)
+                return new JObject { ["valid"] = false, ["error"] = "This promo code has been used up.", ["_httpStatus"] = 400 };
+
+            return new JObject { ["success"] = true, ["discount"] = discount };
```

Notes:
* `Limit(...)` already returns `-1` for "unlimited" in `RuleErrorAsync` (`global >= 0` test); the batch uses the same convention.
* `ActiveRedemptionSqlFilter("r")` is the service's existing helper that appends the "still-active transfer" predicate; it is concatenated exactly like in `CountAsync`.
* With `UX_RewardPromoRedemption_Code_Transfer_Redeemed` in place a retry race can no longer create two `REDEEMED` rows even if the lock were bypassed.
* `redeemCfg` falls back to `{}` if `Parse(null)` returns an empty object; if your `Parse` throws on null, guard with `cfgRow?.ConfigJson ?? "{}"`.

---

## 6. `Furp.Application/Features/Rewards/Engine/RewardSchemeAwards.cs` — honour `eligibility_rules.paymentMethods`

### 6.1 Persist the payment method on the transfer fact

```diff
         public async Task RecordTransferAsync(
             string transferId,
             string customerId,
             decimal amount,
             string currency,
             string receiveCurrency,
             string status,
-            CancellationToken cancellationToken)
+            CancellationToken cancellationToken,
+            string paymentMethod = null)   // STAGING-SYNC (optional → existing callers compile unchanged)
         {
...
 MERGE dbo.RewardTransferFact AS t
 USING (SELECT @TransferId AS TransferId) AS s ON t.TransferId = s.TransferId
 WHEN MATCHED THEN UPDATE SET
     UserId=@UserId, Amount=@Amount, Currency=@Currency, ReceiveCurrency=@ReceiveCurrency,
-    Status=@Status, UpdatedAt=SYSUTCDATETIME()
+    Status=@Status, PaymentMethod=COALESCE(@PaymentMethod, t.PaymentMethod), UpdatedAt=SYSUTCDATETIME()
 WHEN NOT MATCHED THEN INSERT
-    (TransferId, UserId, Amount, Currency, ReceiveCurrency, Status)
-    VALUES (@TransferId, @UserId, @Amount, @Currency, @ReceiveCurrency, @Status);",
+    (TransferId, UserId, Amount, Currency, ReceiveCurrency, Status, PaymentMethod)
+    VALUES (@TransferId, @UserId, @Amount, @Currency, @ReceiveCurrency, @Status, @PaymentMethod);",
                 new
                 {
                     ...
                     Status = (status ?? string.Empty).Trim().ToUpperInvariant(),
+                    PaymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? null : paymentMethod.Trim()
                 },
```

The one caller (`RewardReferralService.TransferEventAsync`, already in the drop-in) passes
`paymentMethod: Text(body?["payment_method"] ?? body?["paymentMethod"])`.

### 6.2 Check it in `AwardForTransferAsync`

In the `foreach (var scheme ...)` loop, immediately after the `SegmentOkAsync` check:

```diff
                 if (!InsideWindow(cfg)) continue;
                 if (!await SegmentOkAsync(cfg, customerId, cancellationToken)) continue;
+                if (!await PaymentMethodOkAsync(cfg, transferId, cancellationToken)) continue;   // STAGING-SYNC
```

New helper next to `SegmentOkAsync` (same bucket mapping as `RewardPromoService.PayBucket`; copy it or make it `internal static`):

```csharp
// STAGING-SYNC: eligibility_rules.paymentMethods. Empty / missing / "all" = every method (previous behaviour).
// An unknown payment method on the transfer fact does not block the award (fail open, as promo codes do).
private async Task<bool> PaymentMethodOkAsync(JObject cfg, string transferId, CancellationToken cancellationToken)
{
    var rules = cfg["eligibility_rules"] as JObject;
    var methods = rules?["paymentMethods"] as JArray ?? rules?["payment_methods"] as JArray;
    var wanted = methods?
        .Select(x => (x?.ToString() ?? string.Empty).Trim())
        .Where(x => x.Length > 0 && !x.Equals("all", StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (wanted == null || wanted.Count == 0) return true;

    var used = await _dapper.ExecuteScalarAsync<string>(
        "SELECT PaymentMethod FROM dbo.RewardTransferFact WHERE TransferId = @transferId;",
        new { transferId }, cancellationToken: cancellationToken);
    if (string.IsNullOrWhiteSpace(used)) return true;

    var bucket = PayBucket(used);
    return wanted.Any(m => PayBucket(m) == bucket);
}

private static string PayBucket(string method)
{
    var s = (method ?? string.Empty).Trim().ToUpperInvariant().Replace(" ", "_").Replace("-", "_");
    if (s is "BANK_TRANSFER" or "BANKPAYMENT" or "NOKRIN" or "BANK") return "BANK_TRANSFER";
    if (s is "PLAID" or "PAYBYBANK" or "INSTANT" or "INSTANT_BANK" or "INSTANT_PAY_BY_BANK") return "INSTANT_BANK";
    if (s.Contains("CARD") || s.Contains("CHECKOUT") || s is "APPLE" or "GOOGLE") return "CARD";
    if (s.Contains("MOBILE")) return "MOBILE_MONEY";
    if (s.Contains("USSD")) return "USSD";
    if (s.Contains("WALLET") || s == "TUNACCOUNT") return "WALLET";
    return s;
}
```

`RewardCheckout` / the transfer-event poster must send `payment_method` on `referral/transfer-events`; until it does, the column stays `NULL` and schemes behave exactly as before.

---

## 7. `Furp.Application/Features/Transactions/.../CreateRetailTransactionCommandHandler.cs` (~L220)

`INCREASE_RECEIVE` promos set `rewardQuote.ReceiveBoost` (send-currency value) but the retail handler ignores it, so the customer is charged and the recipient gets no boost. The classic `Transaction` handler already adds `quote.ReceiveBoost * rate` (L256); make retail consistent.

```diff
                 SendAmount = rewardQuote.SendAmount,
                 TransactionValue = rate.SendAmount,
-                ReceiveAmount = rate.ReceiveAmount,
+                ReceiveAmount = rate.ReceiveAmount + rewardQuote.ReceiveBoost * rate.Rate,   // STAGING-SYNC
                 AdminCommission = rewardQuote.Commission,
```

* `ReceiveBoost` is a plain `decimal` (default `0`), so every transfer **without** an `INCREASE_RECEIVE` promo is byte-for-byte unchanged.
* If your payout/provider payload re-reads `rate.ReceiveAmount` later in the handler (e.g. when building the `CreateTransactionResponse`), add the same boost there so quote, DB row and payout agree. Rounding: keep the project's existing rounding helper (`CommonAutoGenerate.ConvertTo4DecimalPlaces` is used for `Rate`) if the column requires it.
* Add a regression test: promo `REDUCE_SEND` ⇒ `ReceiveAmount == rate.ReceiveAmount`; promo `INCREASE_RECEIVE` ⇒ `ReceiveAmount == rate.ReceiveAmount + ReceiveBoost × Rate`.

---

## Apply-order checklist

- [ ] §1 `RewardSchema.cs` (so fresh DBs match the migration)
- [ ] Copy the three drop-in `.cs` files
- [ ] §6 `RewardSchemeAwards.cs` (the drop-in referral service already passes `paymentMethod:` — needs the optional parameter to compile)
- [ ] §2 `LocalRewardEngine.cs`
- [ ] §5 `RewardPromoService.cs`
- [ ] §4 `RewardCommands.cs`, §7 retail handler
- [ ] §3 `RewardCustomerCommands.cs` (3.3 only after the visit-contract approval)
- [ ] `dotnet build` + existing Reward tests green, then add the new tests from the guide
