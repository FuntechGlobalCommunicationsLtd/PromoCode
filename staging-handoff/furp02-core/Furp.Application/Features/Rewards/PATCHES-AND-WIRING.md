# PATCHES-AND-WIRING — surgical diffs for `furp02-core`

> Companion to the drop-in files (`RewardReferralService.cs`, `RewardWalletService.cs`, `RewardCheckout.cs`, **new** `Engine/RewardReferralRuleGuards.cs`) and `Database/Migrations/UpgradeRewardEngineStagingSync.sql`.
> Line numbers refer to the `furp02-core` `pri_prod` dump the package was written against; **match on the quoted anchor text, not the line number**.
> Every edit is additive or a one-line tightening. Nothing here renames a route, table, column, flag or JSON field.
> Code that was not in the workspace (`LocalRewardEngine.cs`, `RewardPromoService.cs`, `RewardCustomerCommands.cs`, ...) is patched by anchor text and marked **verify** wherever a type or signature had to be assumed.

| § | File | Edit size | Behaviour change for existing callers |
| :--- | :--- | :--- | :--- |
| 1 | `Engine/RewardSchema.cs` | append DDL | none (new tables / nullable columns; identical to the migration) |
| 2 | `Engine/LocalRewardEngine.cs` | +routes, small arg changes | `referral/visits` also carries `trackingId` etc.; `referral/codes` accepts `rule_id` |
| 3 | `RewardCustomerCommands.cs` | referral code rule 6–16, +fields, **new** `Visit/Track` route | `Account/Rewards/Visit` stays `Result<bool>` (unchanged for mobile / widget) |
| 4 | `RewardCommands.cs` | 1 SQL line | `promoSavings` no longer counts `RELEASED` (bug fix) |
| 5 | `Engine/RewardPromoService.cs` | +1 check, atomic redeem, FixedAmount + Receive Money codes | affiliate-restricted codes enforced; no duplicate redeems |
| 6 | `Engine/RewardSchemeAwards.cs` | +`paymentMethods` check | schemes with `paymentMethods` now honoured |
| 7 | `CreateRetailTransactionCommandHandler.cs` | 1 line | `INCREASE_RECEIVE` boost now reaches the payout (bug fix) |
| 8 | `Engine/LocalRewardEngine.cs` (`referral-rules`, list handlers) | wire `RewardReferralRuleGuards` / `RewardSort` | 409 on a second active rule per send currency; audit history; active-first ordering |

> [!IMPORTANT]
> **Apply order:** §2 migration script first (`sqlcmd -b -I`, before the application deploy), then §1, §6, the drop-in files, §2 wiring, §5, §4, §7, §3, §8. See the checklist at the end.

---
## 1. `Furp.Application/Features/Rewards/Engine/RewardSchema.cs`

Append **inside** `DdlSql`, immediately after the `dbo.RewardRateAuditLog` block and **before** the closing `";`.
(The string is a C# verbatim `@"..."` literal; this DDL contains no double quotes, so no escaping is needed.)

> [!IMPORTANT]
> **One text, three places.** The block below is byte-for-byte the same DDL as (a) `Database/Migrations/UpgradeRewardEngineStagingSync.sql` (objects 1-8), (b) this `RewardSchema.DdlSql` append, and (c) `RewardReferralService.StagingDdlSql` (already replaced in the drop-in). The only difference is that the runtime copies (b, c) use the *dup-guarded* `EXEC` form for `UX_RewardPromoRedemption_Code_Transfer_Redeemed` (they never THROW on dirty data); the migration keeps its `THROW 51003` pre-flight so a DBA sees the duplicates.

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
+END;
+
+IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RewardReferralVisit_TrackingId' AND object_id = OBJECT_ID(N'dbo.RewardReferralVisit'))
+    EXEC (N'CREATE UNIQUE INDEX UX_RewardReferralVisit_TrackingId
+                ON dbo.RewardReferralVisit (TrackingId) WHERE TrackingId IS NOT NULL;');
+IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardReferralVisit_CodeVisitor' AND object_id = OBJECT_ID(N'dbo.RewardReferralVisit'))
+    CREATE INDEX IX_RewardReferralVisit_CodeVisitor
+        ON dbo.RewardReferralVisit (Code, VisitorId, CreatedAt DESC);
+IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardReferralVisit_Referrer' AND object_id = OBJECT_ID(N'dbo.RewardReferralVisit'))
+    CREATE INDEX IX_RewardReferralVisit_Referrer
+        ON dbo.RewardReferralVisit (ReferrerId, CreatedAt DESC);
+IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardReferralVisit_LinkedReferee' AND object_id = OBJECT_ID(N'dbo.RewardReferralVisit'))
+    EXEC (N'CREATE INDEX IX_RewardReferralVisit_LinkedReferee
+                ON dbo.RewardReferralVisit (LinkedRefereeId) WHERE LinkedRefereeId IS NOT NULL;');
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
+
+IF OBJECT_ID(N'dbo.RewardReferralRuleAudit', N'U') IS NULL
+    CREATE TABLE dbo.RewardReferralRuleAudit (
+        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RewardReferralRuleAudit PRIMARY KEY,
+        RuleId NVARCHAR(64) NOT NULL,
+        Field NVARCHAR(64) NOT NULL,
+        OldValue NVARCHAR(MAX) NULL,
+        NewValue NVARCHAR(MAX) NULL,
+        AdminUser NVARCHAR(128) NULL,
+        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_RewardReferralRuleAudit_CreatedAt DEFAULT (SYSUTCDATETIME()));
+IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardReferralRuleAudit_Rule' AND object_id = OBJECT_ID(N'dbo.RewardReferralRuleAudit'))
+    CREATE INDEX IX_RewardReferralRuleAudit_Rule ON dbo.RewardReferralRuleAudit (RuleId, Id DESC);
+
+IF COL_LENGTH(N'dbo.RewardPromoRedemption', N'Flow') IS NULL
+    ALTER TABLE dbo.RewardPromoRedemption ADD Flow NVARCHAR(16) NULL;
 ";
```

> [!WARNING]
> In the C# verbatim string, the inner `N''REDEEMED''` (doubled single quotes inside `EXEC (N'...')`) is **T-SQL** escaping and must stay exactly as written; no extra C# escaping is required.
>
> A table created earlier by the *old* lazy DDL (indexes `UX_RewardReferralVisit_Tracking`, `IX_RewardReferralVisit_Linked`, `IX_RewardReferralVisit_Currency`) still gets the canonical indexes, because each index has its own `IF NOT EXISTS` guard outside the `CREATE TABLE` block. The three old indexes are harmless and are **left in place** (a DBA may drop them; no script does).
>
> `RewardReferralService.StagingDdlSql` and the wallet's private `EnsureAsync` self-apply the same DDL lazily (idempotent, with one retry for the first-call race), so the order of deployment vs migration does not matter. Once this section is in `RewardSchema.cs` those lazy calls become no-ops.

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

Add the private helpers next to `Money`/`Text` at the bottom of the class:

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

// VERIFY the real type of `query` in DispatchAsync (signature not available in this package) and keep the matching overload(s).
private static JObject QueryToJObject(IDictionary<string, string> query)
{
    var o = new JObject();
    if (query == null) return o;
    foreach (var kv in query) o[kv.Key] = kv.Value;
    return o;
}

// If `query` is an ASP.NET Core IQueryCollection (needs `using Microsoft.AspNetCore.Http;`):
private static JObject QueryToJObject(IQueryCollection query)
{
    var o = new JObject();
    if (query == null) return o;
    foreach (var kv in query) o[kv.Key] = kv.Value.ToString();
    return o;
}

// If `query` is already a JObject:
private static JObject QueryToJObject(JObject query) => query ?? new JObject();
```

> [!NOTE]
> Delete the overloads whose parameter type your project does not reference; keep only the one that matches the `query` argument of `DispatchAsync`.
### 2.2 Visit now returns the tracking id (existing route, same path)

```diff
             if (path.Equals("referral/visits", StringComparison.OrdinalIgnoreCase) && method == "POST")
             {
-                await _referral.VisitAsync(body, cancellationToken);
-                return new JObject { ["success"] = true };
+                var visit = await _referral.VisitAsync(body, cancellationToken);   // STAGING-SYNC
+                // keep the old field, but NEVER overwrite the engine's own success:false (INVALID_FORMAT / NOT_FOUND)
+                if (visit["success"] == null) visit["success"] = true;
+                return visit;
             }
```

> `VisitAsync` was `Task` and is now `Task<JObject>`; other awaiting callers keep compiling.
> `rule_id` / `ruleId` in the POST body (sent by `RecordReferralVisitCommand` and `TrackReferralVisitCommand`, §3.3) is read by `VisitAsync` and locks the visit to that rule.
> There is no HTTP header access inside `LocalRewardEngine`; the gateway forwards the browser `User-Agent` as `body["user_agent"]` and `RewardReferralService.VisitAsync` uses it for device/OS detection (otherwise device fields stay `UNKNOWN` unless the caller sends `device_type` / `operating_system`).
### 2.3 Offer / code lookups accept the corridor and the rule lock

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
+                    Get(query, "receive_currency") ?? Get(query, "receiveCurrency"),
+                    Get(query, "rule_id") ?? Get(query, "ruleId"),                          // STAGING-SYNC: ?rule= link lock
+                    cancellationToken);
```

(The old 3- and 4-argument `GetCodeAsync` overloads still exist and delegate, so any other caller is unaffected.)

`GetCodeAsync` now also (a) resolves **currency-suffixed share codes** (`{BASE}{CUR}`, e.g. `A1B2C3GBP`) to the referrer's base code and (b) answers `LINK_EXPIRED` (HTTP 400, `"Link Expired"`) for an ended rule — see the code comments in the drop-in. Currency precedence is `explicit ?? codeCurrency ?? referrer.SendCurrency ?? "GBP"`.
### 2.4 Transfer events carry the payment method

No change in `LocalRewardEngine`; the body already reaches `TransferEventAsync`. See §6 for how `payment_method` is persisted.

### 2.5 Optional: daily sweep for the qualification window

`RewardReferralService.ExpireOverdueAsync(ct)` closes referrals past `qualification_window_days`. `TransferEventAsync` already enforces the window when a transfer arrives; schedule the sweep only if you also want unmatched referrals to flip to `EXPIRED` without waiting for a transfer (e.g. from the existing `bonus/run-jobs` handler: `await _referral.ExpireOverdueAsync(ct);`).

---

## 3. `Furp.Application/Features/Rewards/RewardCustomerCommands.cs`

### 3.1 `NormaliseCode` — one rule everywhere: 6–16 letters and numbers

Canonical rule (identical to `RewardReferralService.CanonicalCode`, the engine validator and the Next.js / compat layers):

```
normalise(raw) = raw.Trim().ToUpperInvariant() with every non [A-Z0-9] char removed
valid          = ^[A-Z0-9]{6,16}$
message        = "Referral codes are 6–16 letters and numbers."
SupportedCurrencies (suffix order, exact) = GBP, USD, EUR, NGN, CAD, AUD, JPY, CNY, INR, ZAR, KES, GHS, AED
```

```diff
         public static string NormaliseCode(string raw)
         {
             if (string.IsNullOrWhiteSpace(raw)) return null;
             var code = new string(raw.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
-            if (code.Length < 6 || code.Length > 12) return null;
+            if (code.Length < 6 || code.Length > 16) return null;   // STAGING-SYNC: was 6..12 (6..16 admits currency-suffixed share codes)
             return code;
         }
```

> [!NOTE]
> `char.IsLetterOrDigit` also accepts non-ASCII letters; if you want byte-for-byte parity with the prototype, filter with `c is >= 'A' and <= 'Z' or >= '0' and <= '9'` instead (the engine's `CanonicalCode` already does, and is the final judge).

and the three user-facing messages:

```diff
-                return Result<ReferralCodeCheckDto>.Fail("Referral codes are 6–12 letters and numbers.");
+                return Result<ReferralCodeCheckDto>.Fail("Referral codes are 6–16 letters and numbers.");
...
-                return Result<string>.Fail("Referral codes are 6–12 letters and numbers.");
+                return Result<string>.Fail("Referral codes are 6–16 letters and numbers.");
```

> [!NOTE]
> **Conflict note (validation rule).** Existing behaviour: codes outside 6–12 characters were rejected before the engine was called. New rule: 6–16. The widening from 12 to 16 exists **only** to admit currency-suffixed share codes (`{BASE 6–12}{CUR 3}`, at most 15 characters, e.g. `A1B2C3D4E5GBP`); generated base codes are still 6–12 and nothing that was valid before becomes invalid. Ship this together with the Rhemito-web `referralStore` / `/ref/[code]` change (same regex). The engine remains the final judge (`INVALID_FORMAT` / `NOT_FOUND`); the earlier 3–32 relaxation is **withdrawn**.

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

> [!IMPORTANT]
> Forward `currency=` **only when `request.Currency` is non-blank.** A hard default of `GBP` would always beat the currency that a suffixed code (`…USD`) locks to, so the lock could never win.

```diff
-            var call = await _gateway.GetAsync(
-                "/api/referral/codes/" + Uri.EscapeDataString(code) + "?currency=" + Uri.EscapeDataString(currency),
-                cancellationToken);
+            var qs = new List<string>();
+            if (!string.IsNullOrWhiteSpace(request.Currency))                      // STAGING-SYNC: only when supplied
+                qs.Add("currency=" + Uri.EscapeDataString(request.Currency.Trim().ToUpperInvariant()));
+            if (!string.IsNullOrWhiteSpace(request.ReceiveCurrency))
+                qs.Add("receive_currency=" + Uri.EscapeDataString(request.ReceiveCurrency.Trim().ToUpperInvariant()));
+            if (request.RuleId.HasValue && request.RuleId.Value > 0)
+                qs.Add("rule_id=" + request.RuleId.Value);
+            var call = await _gateway.GetAsync(
+                "/api/referral/codes/" + Uri.EscapeDataString(code) + (qs.Count > 0 ? "?" + string.Join("&", qs) : string.Empty),
+                cancellationToken);
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

(`using System.Collections.Generic;` if the file does not already have it. If the existing handler computed `currency` for something else — e.g. the DTO fallback — keep that variable; only the query string changes.)

The engine answers `{ valid:false, error:"LINK_EXPIRED", message:"Link Expired" }` with HTTP 400 for an ended rule; the existing "not valid" mapping of this handler surfaces `message`.

### 3.3 Visit — `Account/Rewards/Visit` is UNCHANGED, plus the new additive `Visit/Track`

> [!IMPORTANT]
> **Resolved decision (board #8 / #11): backward-compatible, not a breaking change.**
> `POST Account/Rewards/Visit` **keeps returning `Result<bool>`** (`data: true/false`) — 100 % compatible for mobile / widget callers that read `data === true`. The only edit to that handler is the additive `rule_id` in the engine POST. The tracking id (needed by the Android Play Install Referrer / iOS hand-off) comes from the **new** route `POST Account/Rewards/Visit/Track` returning `Result<ReferralVisitDto>`. The earlier proposal to change `Visit` itself is **withdrawn**.

**(a) `Visit` — only add `RuleId` (and send it):**

```diff
     public class RecordReferralVisitCommand : IRequest<Result<bool>>
     {
         public string Code { get; set; }
         public string VisitorId { get; set; }
         public string ReceiveCurrency { get; set; }
+        public int? RuleId { get; set; }   // STAGING-SYNC (additive, binds automatically)
     }
...
-            await _gateway.PostAsync("/api/referral/visits", new { code, visitor_id = visitor, receive_currency = receive }, cancellationToken);
+            await _gateway.PostAsync("/api/referral/visits", new { code, visitor_id = visitor, receive_currency = receive, rule_id = request.RuleId }, cancellationToken);
             return Result<bool>.Success(true, "Success");     // exactly today's behaviour; Success(false) for invalid code / unconfigured gateway
```

**(b) NEW `ReferralVisitDto`, `TrackReferralVisitCommand` and handler** (put next to `RecordReferralVisitCommand`; model the constructor on the existing `RecordReferralVisitCommandHandler` — the gateway field is the same `_gateway`):

```csharp
// STAGING-SYNC: JSON camelCase on the wire: tracked, trackingId, playStoreUrl, appStoreUrl
public class ReferralVisitDto
{
    public bool Tracked { get; set; }
    public string TrackingId { get; set; }
    public string PlayStoreUrl { get; set; }
    public string AppStoreUrl { get; set; }
}

public class TrackReferralVisitCommand : IRequest<Result<ReferralVisitDto>>
{
    public string Code { get; set; }
    public string VisitorId { get; set; }
    public string ReceiveCurrency { get; set; }
    public int? RuleId { get; set; }
    public string DeviceType { get; set; }        // DESKTOP | MOBILE | TABLET
    public string OperatingSystem { get; set; }
    public string SourceChannel { get; set; }     // LINK | QR | SHARE | ...
    public string StoreTarget { get; set; }       // GOOGLE_PLAY | APPLE_APP_STORE | WEB
    public string UserAgent { get; set; }         // set by the controller from Request.Headers.UserAgent
}

public class TrackReferralVisitCommandHandler : IRequestHandler<TrackReferralVisitCommand, Result<ReferralVisitDto>>
{
    private readonly RewardEngineGateway _gateway;
    public TrackReferralVisitCommandHandler(RewardEngineGateway gateway) { _gateway = gateway; }   // same ctor shape as the Visit handler

    public async Task<Result<ReferralVisitDto>> Handle(TrackReferralVisitCommand request, CancellationToken cancellationToken)
    {
        // Never fails the landing page: invalid code, unconfigured gateway or engine error => Tracked = false.
        var none = Result<ReferralVisitDto>.Success(new ReferralVisitDto { Tracked = false }, "Success");
        var code = NormaliseCode(request.Code);   // VERIFY: the same static method as §3.1 (qualify it with its host class if the handler lives elsewhere)
        if (code == null || !_gateway.IsConfigured) return none;
        try
        {
            var call = await _gateway.PostAsync("/api/referral/visits", new
            {
                code,
                visitor_id = string.IsNullOrWhiteSpace(request.VisitorId) ? null : request.VisitorId.Trim(),
                receive_currency = string.IsNullOrWhiteSpace(request.ReceiveCurrency) ? null : request.ReceiveCurrency.Trim().ToUpperInvariant(),
                rule_id = request.RuleId,
                device_type = request.DeviceType,
                operating_system = request.OperatingSystem,
                source_channel = request.SourceChannel,
                store_target = request.StoreTarget,
                user_agent = request.UserAgent
            }, cancellationToken);

            var tracked = call.IsSuccess && call.Json?["success"] != null ? RewardEngineGateway.Flag(call.Json["success"]) : call.IsSuccess;
            return Result<ReferralVisitDto>.Success(new ReferralVisitDto
            {
                Tracked = tracked,
                TrackingId = tracked ? RewardEngineGateway.Text(call.Json?["trackingId"] ?? call.Json?["tracking_id"]) : null,
                PlayStoreUrl = RewardEngineGateway.Text(call.Json?["playStoreUrl"] ?? call.Json?["play_store_url"]),
                AppStoreUrl = RewardEngineGateway.Text(call.Json?["appStoreUrl"] ?? call.Json?["app_store_url"])
            }, "Success");
        }
        catch (Exception)
        {
            return none;
        }
    }
}
```

`Tracked = call.IsSuccess && json.success != false`. (This is why §2.2 must **not** overwrite the engine's `success:false`.)

**(c) NEW controller action** next to the existing `Account/Rewards/Visit` action (copy its attributes / return style exactly):

```csharp
// STAGING-SYNC: additive route; the existing Visit route is untouched.
[AllowAnonymous]
[HttpPost("Account/Rewards/Visit/Track")]
public async Task<IActionResult> TrackRewardsVisit([FromBody] TrackReferralVisitCommand command)
{
    command.UserAgent = Request.Headers["User-Agent"].ToString();
    return Ok(await Mediator.Send(command));      // VERIFY: use the same Mediator / Ok(...) pattern as the Visit action
}
```

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

(`CreateReferralAsync` in the drop-in already reads `tracking_id`, `rule_id`, `registration_channel`, resolves currency-suffixed codes and links the visit row.)

### 3.5 Customer `Promo/Validate` — forward `flow`, `affiliate_id`, `site_key`

In the FURP customer handler that posts `/api/promocodes/validate` (`RewardCustomerCommands`, the compat twin is `furpV1CompatRoutes.ts` L339-395):

* add an optional `Flow` property to the request (`"send"` default | `"receive"`) and forward it as `flow` **only when supplied**;
* send the same tenant keys as `RewardCheckout`: `affiliate_id` (omit when null) and `site_key = "rhemito"`.

```diff
+            var promoBody = new Dictionary<string, object>
+            {
+                // ...every field the handler already sends (code, amount, fee, currency, user_id, source_currency, dest_currency, payment_method)...
+            };
+            if (!string.IsNullOrWhiteSpace(request.Flow)) promoBody["flow"] = request.Flow.Trim().ToLowerInvariant();   // STAGING-SYNC (additive)
+            var affiliateId = RewardAffiliateKey.AffiliateId(user);                                                    // STAGING-SYNC (RewardCheckout.cs)
+            if (affiliateId != null) promoBody["affiliate_id"] = affiliateId;
+            promoBody["site_key"] = RewardAffiliateKey.SiteKey;
-            var call = await _gateway.PostAsync("/api/promocodes/validate", new { ... }, cancellationToken);
+            var call = await _gateway.PostAsync("/api/promocodes/validate", promoBody, cancellationToken);
```

The response already carries the additive keys of §5.3 (`applies_to`, `appliesTo`); pass them through untouched.

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

`RewardCheckout` (and the customer `Promo/Validate`, §3.5) now send `affiliate_id` (the user's affiliate GUID, lower-case `"D"` format; **omitted** when the user has none) **and** `site_key` (`"rhemito"`) on **validate and redeem**; the service must honour `restrictions.affiliates`.

> [!NOTE]
> The prototype promo engine stores `restrictions.affiliates` but never enforces it (`server/promo/validation.js`), so this is **new FURP behaviour that was already in the package**. It stays **fail-open**. Mito Admin "affiliates" entries must be the affiliate GUID or `rhemito`.

In `ValidateAsync`, right after `var dest = ...;` add:

```csharp
// STAGING-SYNC: tenant / affiliate scoping (restrictions.affiliates)
var affiliateId = Text(body?["affiliate_id"] ?? body?["affiliateId"]);
var siteKey = Text(body?["site_key"] ?? body?["siteKey"]);
```

and right after the existing `ruleError` block:

```diff
             var ruleError = await RuleErrorAsync(cfg, row.Code, amount, currency, userId, transferId, paymentMethod, source, dest, cancellationToken);
             if (ruleError != null)
             {
                 return new JObject { ["valid"] = false, ["error"] = ruleError, ["_httpStatus"] = 400 };
             }
+
+            if (!AffiliateAllowed(cfg, affiliateId, siteKey))
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
// STAGING-SYNC: empty or missing list = allowed (exactly as before). Otherwise allowed when EITHER key equals a list item
// (trimmed, case-insensitive). Both keys missing = allowed (fail-open, as today).
private static bool AffiliateAllowed(JObject cfg, string affiliateId, string siteKey)
{
    var restrictions = cfg["restrictions"] as JObject;
    var list = restrictions?["affiliates"] as JArray ?? cfg["affiliates"] as JArray;
    if (list == null || list.Count == 0) return true;
    var a = string.IsNullOrWhiteSpace(affiliateId) ? null : affiliateId.Trim();
    var s = string.IsNullOrWhiteSpace(siteKey) ? null : siteKey.Trim();
    if (a == null && s == null) return true;
    return list.Any(item =>
    {
        var v = (Text(item) ?? string.Empty).Trim();
        return (a != null && string.Equals(v, a, StringComparison.OrdinalIgnoreCase))
            || (s != null && string.Equals(v, s, StringComparison.OrdinalIgnoreCase));
    });
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
+INSERT INTO dbo.RewardPromoRedemption (Id, PromoCode, UserId, TransferId, Discount, Currency, Status, Flow)
+VALUES (@Id, @code, @UserId, @transferId, @Discount, @Currency, N'REDEEMED', @flow);   -- Flow: see 5.3 (NULL = send)
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
+                    flow = flow == "receive" ? "receive" : null,   // 5.3 (flow is read at the top of RedeemAsync)
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

### 5.3 Promo `FixedAmount` send discounts and Receive Money bonus codes

Port of `server/promo/engine.js` L57-97, L158-251, L254-279, L316-330 and `validation.js` L20-51, L108-109. **Verify** the local names (`cfg`, `row`, `amount`, `fee`, `currency`, `Money(...)`, `RoundFor`, ...) against your `RewardPromoService.cs`; nothing here changes the response of the existing types (`Fixed`, `Percentage`, `Waiver`).

**Types** (`ConfigJson.type`): `Fixed`, `FixedAmount` (new), `Percentage`, `Waiver`.
**Request key (additive):** `flow` — `"send"` (default) | `"receive"`. **Promo config:** `restrictions.applies_to_flow` — `"send"` (default) | `"receive"` (a legacy `"both"` still passes; the admin API rejects it for new codes).

**(a) Read the flow** — in `ValidateAsync` / `RedeemAsync` next to `var dest = ...;`:

```csharp
// STAGING-SYNC: Send Money (default) vs Receive Money bonus codes
var flow = string.Equals(Text(body?["flow"]), "receive", StringComparison.OrdinalIgnoreCase) ? "receive" : "send";
var promoType = (Text(cfg["type"]) ?? string.Empty).Trim();
```

**(b) Flow + FixedAmount validation** — insert **after the budget check** and before the currency check (same order as the prototype):

```csharp
// STAGING-SYNC: flow mismatch -> 400 FLOW
var promoFlow = (Text((cfg["restrictions"] as JObject)?["applies_to_flow"]) ?? "send").Trim().ToLowerInvariant();
if (promoFlow != "both" && promoFlow != flow)
{
    return new JObject
    {
        ["valid"] = false,
        ["error"] = flow == "receive"
            ? "This code is for Send Money, so it can't be used on a request."
            : "This code is for Receive Money requests, so it can't be used on a transfer.",
        ["error_code"] = "FLOW",
        ["_httpStatus"] = 400
    };
}

if (flow == "receive" && promoType != "Fixed" && promoType != "Percentage")
{
    return new JObject { ["valid"] = false, ["error"] = "This code can't be used on a request.", ["error_code"] = "FLOW", ["_httpStatus"] = 400 };
}
```

and, with the other amount checks (after the existing `min_threshold` `BELOW_MIN` check):

```csharp
// STAGING-SYNC: a FixedAmount discount can never exceed the send amount
if (promoType == "FixedAmount" && amount < Money(cfg["value"]))
{
    return new JObject
    {
        ["valid"] = false,
        ["error"] = "Send at least " + FormatMoney(Money(cfg["value"]), promoCurrencyOrRequestCurrency)
                    + " to use this code (discount cannot exceed send amount).",
        ["error_code"] = "BELOW_MIN",
        ["_httpStatus"] = 400
    };
}
```

(Skip the corridor check for `flow == "receive"`, as the prototype does: `if (n.flow !== 'receive' && corridors...)`. `FormatMoney` = whatever helper the existing `BELOW_MIN` message already uses.)

> The prototype puts the code in `error`/`code`; match the key the existing `RuleErrorAsync` failures use in your service and keep it consistent (`error_code` above is the additive key).

**(c) Discount** — replace the discount computation with:

```csharp
// STAGING-SYNC: port of engine.js computeDiscount
private static decimal ComputeDiscount(JObject cfg, string type, decimal amount, decimal fee, string flow)
{
    var value = Money(cfg["value"]);
    var maxDiscount = Money(cfg["max_discount"] ?? cfg["maxDiscount"]);
    decimal d;
    if (flow == "receive")
    {
        // Receive Money: a bonus for the requester on the requested amount; never a fee discount, no fee cap
        d = type == "Percentage" ? amount * value / 100m : type == "Fixed" ? value : 0m;
        if (maxDiscount > 0 && d > maxDiscount) d = maxDiscount;
        return Math.Max(0m, d);   // then RoundFor(...) exactly as the existing code does
    }

    switch (type)
    {
        case "Percentage": d = fee * value / 100m; break;     // keep your existing base (fee, or amount when no fee is supplied)
        case "Fixed":      d = value; break;
        case "FixedAmount": d = value; break;
        case "Waiver":     d = fee; break;
        default:           d = 0m; break;
    }

    if (maxDiscount > 0 && d > maxDiscount) d = maxDiscount;
    if (type == "FixedAmount") { if (d > amount) d = amount; }   // capped by the send AMOUNT, not the fee
    else if (d > fee) d = fee;                                   // every other send type is still capped by the fee
    return Math.Max(0m, d);
}
```

(Keep the service's existing `Percentage` base and rounding helper if they differ; only the `receive` branch, the `FixedAmount` type and its amount-cap are new.)

**(d) Validate response — additive keys:**

```csharp
// STAGING-SYNC: tells RewardCheckout / the web client what the discount reduces
var appliesTo = flow == "receive" ? "bonus" : promoType == "FixedAmount" ? "amount" : "fee";
result["applies_to"] = appliesTo;
result["appliesTo"] = appliesTo;
// display text
result["display_text"] = flow == "receive"
    ? currencyLabel + " " + discount.ToString("0.00", CultureInfo.InvariantCulture) + " bonus paid into your bank with your payout"
    : promoType == "FixedAmount"
        ? currencyLabel + " " + discount.ToString("0.00", CultureInfo.InvariantCulture) + " off transfer amount (full send amount delivered)"
        : /* existing text for the other types */ existingDisplayText;
```

(Use the response key your service already uses for the human-readable text — `display_text` / `displayText` — and leave existing values as they are for the other types.)

**(e) Redeem** — store the flow: the atomic `INSERT` of §5.2 writes `Flow` (`NULL` = send, `'receive'` = Receive Money; column from migration object 8). `TransferId` holds the **request id** for receive codes:

```diff
-INSERT INTO dbo.RewardPromoRedemption (Id, PromoCode, UserId, TransferId, Discount, Currency, Status)
-VALUES (@Id, @code, @UserId, @transferId, @Discount, @Currency, N'REDEEMED');
+INSERT INTO dbo.RewardPromoRedemption (Id, PromoCode, UserId, TransferId, Discount, Currency, Status, Flow)
+VALUES (@Id, @code, @UserId, @transferId, @Discount, @Currency, N'REDEEMED', @flow);
...
-                    Currency = currency,
+                    Currency = currency,
+                    flow = flow == "receive" ? "receive" : null,
```

(§5.2 above already shows the block with these two lines applied.)

**(f) NEW engine route `POST promocodes/request-events`** — wire it in `LocalRewardEngine.DispatchAsync` next to the other `promocodes/...` routes, backed by a new `RewardPromoService.RequestEventAsync`:

```csharp
// LocalRewardEngine (STAGING-SYNC)
if (path.Equals("promocodes/request-events", StringComparison.OrdinalIgnoreCase) && method == "POST")
    return await _promos.RequestEventAsync(body, cancellationToken);   // VERIFY: field name of the promo service in LocalRewardEngine
```

```csharp
// RewardPromoService (STAGING-SYNC): Receive Money request activity reported by the payments side.
//   PAID_OUT                                  -> { confirmed: n }  (REDEEMED rows with TransferId = request_id AND Flow = 'receive')
//   CANCELLED | EXPIRED | REFUNDED | FAILED  -> { released: n }   (those rows are released)
//   anything else                             -> 400 VALIDATION
public async Task<JObject> RequestEventAsync(JObject body, CancellationToken cancellationToken)
{
    var requestId = Text(body?["request_id"] ?? body?["requestId"]);
    var status = (Text(body?["status"]) ?? string.Empty).Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(requestId) || status.Length == 0)
        return new JObject { ["error"] = "VALIDATION", ["message"] = "request_id and status are required.", ["_httpStatus"] = 400 };

    if (status == "PAID_OUT")
    {
        var confirmed = await _dapper.ExecuteScalarAsync<int>(@"
SELECT COUNT(1) FROM dbo.RewardPromoRedemption
WHERE TransferId = @requestId AND Flow = N'receive' AND Status = N'REDEEMED';",
            new { requestId = requestId.Trim() }, cancellationToken: cancellationToken);
        return new JObject { ["confirmed"] = confirmed };
    }

    if (status is "CANCELLED" or "EXPIRED" or "REFUNDED" or "FAILED")
    {
        var released = await _dapper.ExecuteScalarAsync<int>(@"
UPDATE dbo.RewardPromoRedemption SET Status = N'RELEASED'
WHERE TransferId = @requestId AND Flow = N'receive' AND Status = N'REDEEMED';
SELECT @@ROWCOUNT;",
            new { requestId = requestId.Trim() }, cancellationToken: cancellationToken);
        return new JObject { ["released"] = released };
    }

    return new JObject { ["error"] = "VALIDATION", ["message"] = "Unknown request status.", ["_httpStatus"] = 400 };
}
```

> **Verify:** if your `RewardPromoService.ReleaseAsync` also decrements code counters / budget (some builds keep `UsageCount` / `TotalDiscountUtilized` columns), call it per row instead of the raw `UPDATE`, exactly like the existing `release` route does. `Status` for released rows must be the same literal your existing `release` writes (`RELEASED`, as used by §4).

**(g) Admin validation (Mito Admin `promocodes` POST/PUT validation)** — add next to the existing field validators:

* `FixedAmount`: when `min_threshold > 0` and `value > min_threshold` → *"Fixed amount discount cannot be greater than the minimum send amount."*
* `restrictions.applies_to_flow == "both"` (or anything but send/receive) → *"Choose Send Money or Receive Money. One code cannot cover both."*
* `applies_to_flow == "receive"` and `type` not in (`Fixed`, `Percentage`) → *"Choose Fixed bonus or Percentage bonus."*

**(h) `RewardCheckout.cs` (drop-in, already done):** reads `applies_to ?? appliesTo`; when it is `"amount"` it does `SendAmount -= discount` and leaves **Commission unchanged**; behaviour for every other basis is unchanged.

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

## 8. `LocalRewardEngine.cs` — `referral-rules` handlers: guards, country scope, audit, active-first sorting

The `referral-rules` CRUD handlers live in furp02-core's `LocalRewardEngine` and are **not** part of this package, so they are wired by anchor text onto the **new drop-in** `Furp.Application/Features/Rewards/Engine/RewardReferralRuleGuards.cs` (constructor `IDapperRepository`; also contains the static `RewardSort`). Nothing else is needed beyond §1 / the migration: every rule extra lives in `dbo.RewardReferralRule.ConfigJson` (already read by the drop-in service: `IsArchived`, `RuleStatus`, `RuleReceive`), plus the new `dbo.RewardReferralRuleAudit` table.

**DI:** `services.AddScoped<RewardReferralRuleGuards>();` and inject it into `LocalRewardEngine` (`private readonly RewardReferralRuleGuards _ruleGuards;`).

> [!IMPORTANT]
> **Behaviour change to announce (409):** from this point **editing** (or creating) a referral rule returns HTTP 409 `DUPLICATE_CURRENCY` / `DUPLICATE_NAME` when another **non-archived** rule has the same send currency / name. The guard is **never retroactive**, but if Staging already has more than one active rule per send currency (corridor rules), editing either one answers 409 until one is archived. Run the read-only pre-check at the end of `UpgradeRewardEngineStagingSync.sql` (or the `DEVELOPER-BACKEND-STAGING-GUIDE.md` DBA pre-check) **before** shipping §8 and decide with the product owner which rule to archive. Nothing is archived automatically.

### 8.1 Create — `POST referral-rules`

In the existing create handler, after the request body has been validated/cleaned into `clean` (the rule object the handler writes to `ConfigJson`) and **before** the INSERT:

```diff
+            // STAGING-SYNC: country scope = stored + echoed metadata only (never enforced); one active rule per send currency.
+            RewardReferralRuleGuards.NormaliseCountryScope(clean, isNew: true);
+            using (await _ruleGuards.LockAsync(Text(clean["base_currency"]), cancellationToken))
+            {
+                var conflict = await _ruleGuards.EnsureUniqueAsync(clean, excludeId: null, cancellationToken);
+                if (conflict != null) return conflict;                 // { error, message, _httpStatus = 409 }

                 ...existing INSERT of the rule, returns the new id (ruleId)...

+                await _ruleGuards.RecordCreatedAsync(ruleId, Text(clean["name"]),
+                    RewardReferralRuleGuards.ResolveAdminUser(adminName, body), cancellationToken);
+            }
```

`adminName` = the authenticated admin's display name from FURP, if `DispatchAsync` can see one (**verify**; otherwise pass `null` and `body.admin_user` / `"Admin"` is used).

### 8.2 Update / status / archive — `PUT referral-rules/{id}`, `PATCH referral-rules/{id}/status`, `POST referral-rules/{id}/archive`, `DELETE referral-rules/{id}`

```diff
             // PUT / PATCH …/status  (after merging the patch into `merged`, before the UPDATE)
+            RewardReferralRuleGuards.NormaliseCountryScope(merged, isNew: false);
+            var before = RewardReferralRuleGuards.RuleSnapshot(oldRow.Name, oldRow.Status, oldRow.BaseCurrency, oldRow.ReceiveCurrency, oldRow.ConfigJson);
+            using (await _ruleGuards.LockAsync(Text(merged["base_currency"]), cancellationToken))
+            {
+                var conflict = await _ruleGuards.EnsureUniqueAsync(merged, excludeId: id, cancellationToken);
+                if (conflict != null) return conflict;

                 ...existing UPDATE...

+                var after = RewardReferralRuleGuards.RuleSnapshot(Text(merged["name"]), newStatus, Text(merged["base_currency"]), Text(merged["receive_currency"]), mergedConfigJson);
+                await _ruleGuards.RecordUpdateAsync(id, before, after,
+                    RewardReferralRuleGuards.ResolveAdminUser(adminName, body), cancellationToken);   // one row per changed RULE_FIELDS entry
+            }
```

```diff
             // POST …/archive and DELETE (soft archive; rules are never hard-deleted)
                 ...existing archive UPDATE...
+                await _ruleGuards.RecordArchivedAsync(id, wasAlreadyArchived,
+                    RewardReferralRuleGuards.ResolveAdminUser(adminName, body), cancellationToken);   // archived: '0' -> '1', only the first time
```

### 8.3 Audit history — `GET referral-rules/{id}/audit` (alias `…/history`)

```csharp
// STAGING-SYNC: this is the route mito-admin calls (ReferralSettings.jsx -> ReferralHistoryModal)
if (method == "GET" && path.StartsWith("referral-rules/", StringComparison.OrdinalIgnoreCase)
    && (path.EndsWith("/audit", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/history", StringComparison.OrdinalIgnoreCase)))
{
    var mid = path["referral-rules/".Length..];
    var ruleId = Uri.UnescapeDataString(mid[..mid.LastIndexOf('/')].Trim('/'));
    return await _ruleGuards.AuditAsync(ruleId, cancellationToken);   // { data: [ { id, rule_id, field, old_value, new_value, admin_user, created_at } ] } newest first
}
```

Place it **before** the generic `referral-rules/{id}` GET so the longer path wins. The verb tunnel and IIS 405 are unaffected (GET).

### 8.4 Country scope — stored and echoed, **not enforced**

`ConfigJson` keys (snake_case, exact): `send_country`, `receive_country` (legacy single), `send_country_mode`, `receive_country_mode` (`ALL` default | `INCLUDE` | `EXCLUDE`), `send_countries`, `receive_countries`. Input accepts an array or a CSV string; `NormaliseCountryScope` stores a **CSV string** of upper-case ISO-2 codes (e.g. `"GB,IE"`) or `null`. Rule GET / list responses must return these keys unchanged (Mito Admin's `CountryScopeModal` / `ReferralHistoryModal` read them).

> [!NOTE]
> **Metadata only — by decision.** The prototype never enforces the scope when an offer is built, a visit is recorded or a reward is awarded, so this package does not either (that would be new business logic). Documented in `DEVELOPER-BACKEND-STAGING-GUIDE.md`.

### 8.5 Active-first sorting (stable; changes the ORDER only, never the content)

Load each list exactly as today (its existing SQL order is the secondary order — `OrderBy` in `RewardSort` is stable), then sort:

| List | Call | Rank | Existing secondary order kept |
| :--- | :--- | :--- | :--- |
| `GET referral-rules` | `RewardSort.Rules(rows)` (status key `status`) | ACTIVE 0, SCHEDULED 1, INACTIVE 2, ENDED 3, ARCHIVED 4, else 5 | `CreatedAt DESC, Id DESC` |
| `GET referral/performance` (+ `performance.csv`) | `RewardSort.Rules(rows)` | same | `BaseCurrency, ReceiveCurrency` |
| `GET promocodes` (+ `.csv`) | `RewardSort.Promos(rows)` (key `display_status`) | Active 0, Scheduled 1, Disabled 2, Fully redeemed 3, Budget spent 4, Expired 5, else 6 | `StartDate DESC` |
| `GET bonus/schemes` | `RewardSort.Schemes(rows)` (key `display_status`) | Active 0, Scheduled 1, Inactive 2, Ended 3, Archived 4, else 5 | `CreatedAt DESC, Id DESC` |

```diff
-                return new JObject { ["data"] = new JArray(rows) };
+                return new JObject { ["data"] = new JArray(RewardSort.Rules(rows)) };   // STAGING-SYNC (rows: IEnumerable<JObject>)
```

For typed rows use `RewardSort.By(rows, r => r.Status, RewardSort.RuleRank)`.

### 8.6 Quick checks after wiring

* create rule `GBP` twice → second answers 409 `DUPLICATE_CURRENCY` with the prototype message; archive the first → second create succeeds;
* update a rule → one `dbo.RewardReferralRuleAudit` row per changed field (22 tracked fields), `GET …/audit` returns them newest first;
* `send_countries: ["gb","ie"]` is stored as `"GB,IE"` and echoed unchanged on GET.

---
## Apply-order checklist

- [ ] **Migration first:** `sqlcmd -S <server> -d <db> -b -I -i Database/Migrations/UpgradeRewardEngineStagingSync.sql` **before the application deploy**; review the duplicate-rule report at the end (send currencies with >1 non-archived rule) with the product owner.
- [ ] §1 `RewardSchema.cs` (so fresh DBs match the migration; identical DDL text)
- [ ] §6 `RewardSchemeAwards.cs` (the drop-in referral service already passes `paymentMethod:` — needs the optional parameter to compile)
- [ ] Copy the drop-in `.cs` files: `RewardReferralService.cs`, `RewardWalletService.cs`, `RewardCheckout.cs`, **new** `Engine/RewardReferralRuleGuards.cs`
- [ ] DI registrations (see below)
- [ ] §2 `LocalRewardEngine.cs` (wiring: CSV, visit resolve, `rule_id`, `Visit` success fix)
- [ ] §5 `RewardPromoService.cs` (5.1 affiliate, 5.2 atomic redeem, 5.3 FixedAmount / Receive Money)
- [ ] §4 `RewardCommands.cs`, §7 retail handler
- [ ] §3 `RewardCustomerCommands.cs` (3.1 6–16 codes, 3.2, 3.3 additive `Visit/Track`, 3.4, 3.5)
- [ ] §8 `LocalRewardEngine.cs` (`referral-rules` guards / audit / sorting) — **after** the duplicate-rule report has been reviewed
- [ ] `dotnet build` + existing Reward tests green, then add the new tests from the guide

### DI / constructor requirements (verify before building)

| Type | Constructor | Notes |
| :--- | :--- | :--- |
| `RewardWalletService` | `(IDapperRepository dapper, RewardDebtBlocks debt, RewardFeed feed)` | `RewardDebtBlocks` and `RewardFeed` must be registered |
| `RewardReferralService` | `(IDapperRepository dapper, RewardWalletService wallet, RewardSchemeAwards schemes, RewardDebtBlocks debt)` | register `RewardWalletService` and `RewardSchemeAwards` first |
| `RewardReferralRuleGuards` | `(IDapperRepository dapper)` | new; `AddScoped` |
| `RewardCheckout` | `(RewardEngineGateway gateway, IDapperRepository dapper)` | unchanged; new static `RewardAffiliateKey` in the same file |

`RewardWalletService.ApplyAsync` accepts an **optional** `credit_source` in the apply body (`REFERRAL` = referral credits only; `SCHEME` / `BONUS` = non-referral credits only). Omitted / blank = the existing unrestricted multi-source debit (unchanged). `RewardCheckout` does not send it.