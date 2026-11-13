# DEVELOPER BACKEND STAGING GUIDE — `furp02-core` Rewards Sync

**Audience:** the backend developer who owns `furp02-core` (Azure DevOps, org `Whytelion`, project `Funtech`).
**Goal:** land the PromoCode/Mito-Admin prototype behaviours on the live `LocalRewardEngine` **additively**, prove them on **Staging**, then promote the same commits to **`pri_prod`** with zero edits.

| Environment | Branch | Pipeline | URL |
| :--- | :--- | :--- | :--- |
| Staging | `Staging` | **33** | `https://furp02-staging.funtechcom.com/api/v1/` |
| Live | `pri_prod` | **99 + 49 (run together)** | `https://furp02-pre-pord.funtechcom.com/api/v1/` |

> [!IMPORTANT]
> Everything in this package is **additive**: new tables, new nullable columns, new indexes, new optional fields and new routes. No route, flag, table or column is renamed or removed. Existing wallet/ledger rows are never rewritten.

---

## 0. What is in the package

```
staging-handoff/
├─ DEVELOPER-BACKEND-STAGING-GUIDE.md                ← this runbook
└─ furp02-core/
   ├─ Database/Migrations/UpgradeRewardEngineStagingSync.sql
   └─ Furp.Application/Features/Rewards/
      ├─ RewardCheckout.cs                           ← drop-in (replaces file)
      ├─ PATCHES-AND-WIRING.md                       ← surgical diffs for the other files (§1–§8)
      └─ Engine/
         ├─ RewardReferralService.cs                 ← drop-in (replaces file)
         ├─ RewardWalletService.cs                   ← drop-in (replaces file)
         └─ RewardReferralRuleGuards.cs              ← NEW drop-in (referral-rules guards, audit, sorting)
```

| # | Change | Fixes / adds |
| :--- | :--- | :--- |
| 1 | `UpgradeRewardEngineStagingSync.sql` | `dbo.RewardReferralVisit`; `RewardCreditLedger.Floor` + `ReceiveCurrency`; `RewardTransferFact.PaymentMethod`; promo redemption unique + lookup indexes; **`dbo.RewardReferralRuleAudit`** (rule history); **`RewardPromoRedemption.Flow`** (Receive Money bonus codes); read-only duplicate-rule report |
| 2 | `RewardReferralService.cs` | real visit/tracking (`VisitAsync`, `ResolveTrackingAsync`), cap enforcement + `cap_reached`, qualification window, rule date window + corridor priority, `REFERRER_INACTIVE`, closed-account duplicate check, `currency_offers`, floor/corridor stored on welcome credit, tracking & performance CSV; **one referral-code rule everywhere (6–16 letters/numbers) and currency-suffixed share codes (`{BASE}{CUR}`) resolved**; `rule_id` link lock + `LINK_EXPIRED` |
| 3 | `RewardWalletService.cs` | `sp_getapplock` concurrency guard on apply; `BELOW_FLOOR` and corridor (`ReceiveCurrency`) enforcement; optional **`credit_source`** filter on apply (`REFERRAL` vs `SCHEME`/`BONUS`; omitted = unchanged multi-source behaviour) |
| 4 | `RewardCheckout.cs` | **live stacking bonus over-debit bug** (promo `REDUCE_SEND` + bonus); `send_amount`/`transfer_amount` sent to ledger; `affiliate_id` (GUID, omitted when none) + `site_key="rhemito"` on validate **and** redeem; promo **released** when the wallet apply fails; `FixedAmount` (`applies_to = amount`) discounts the amount, not the fee; configurable `MinPayableAmount` (default `0m`) |
| 5 | `RewardReferralRuleGuards.cs` (new) | one non-archived rule per send currency (409), country-scope normalisation (metadata only), rule audit history, active-first sorting (`RewardSort`) |
| 6 | `PATCHES-AND-WIRING.md` | §1 `RewardSchema.cs`, §2 `LocalRewardEngine.cs`, §3 `RewardCustomerCommands.cs` (+ additive `Visit/Track`), §4 `RewardCommands.cs`, §5 `RewardPromoService.cs` (affiliates, atomic redeem, FixedAmount / Receive Money), §6 `RewardSchemeAwards.cs`, §7 `CreateRetailTransactionCommandHandler.cs`, §8 `LocalRewardEngine.cs` referral-rules wiring |

> [!WARNING]
> **Financial behaviour change (needs sign-off before `pri_prod`):** item 4 changes how much bonus is debited when a promo and a bonus are combined (it was capped against the *pre-promo* `TransferAmount`, over-debiting the wallet). It is a **fix-forward only** change — historic ledger rows are untouched. Get product/finance approval before promoting.

---

## 1. Prerequisites

1. Branch from `Staging`: `feature/rewards-staging-sync`.
2. Confirm the Staging DB already has the 13 `dbo.Reward*` tables (`AddRewardEngineLocalSchema.sql` or one prior API call to `RewardSchema.EnsureAsync`).
3. Take a DB backup / snapshot of Staging (and later Live) before running the migration.
4. IIS: confirm the **PUT/PATCH/DELETE → 405** rule is still active (see §6). Do **not** remove it.
5. Do **not** commit `.env`, connection strings or API keys. Nothing in this package needs a new secret.

---

## 2. Step-by-step — Staging (Pipeline 33)

### Step 1 — Database migration (idempotent) — **run it BEFORE the application deploy**

```powershell
sqlcmd -S <staging-sql-host> -d <staging-db> -E -I -b -i Database\Migrations\UpgradeRewardEngineStagingSync.sql
```

* `-b` returns a non-zero exit code on any error (the script rolls back and `THROW`s); `-I` keeps `QUOTED_IDENTIFIER ON` (needed for the filtered indexes; the script also sets it and `ANSI_NULLS ON`).
* Re-running is a no-op. The verification result set must show `Present = 1` on all 8 rows.
* **DBA pre-check — duplicate referral rules (read the last result set!).** The last statement of the script is a **read-only report** that lists every send currency with **more than one non-archived referral rule** (`SendCurrency, NonArchivedRules, Rules`). It never fails and **never archives or edits anything**. See §8.2 for what to do with the result.
* Stray indexes `UX_RewardReferralVisit_Tracking`, `IX_RewardReferralVisit_Linked`, `IX_RewardReferralVisit_Currency` may exist on Staging from the old lazy DDL. They are harmless and are **left in place** (a DBA may drop them; the script never does). The canonical indexes are created next to them.
* If it stops with **error 51003** (duplicate `REDEEMED` promo rows for one `(PromoCode, TransferId)`), **nothing was changed**. Review the query printed in the message, mark the surplus rows `RELEASED`, then re-run. Do not delete rows.

### Step 2 — Copy the four drop-in files

Copy into the matching paths in `furp02-core` (overwrite):

| From (this package) | To (`furp02-core`) |
| :--- | :--- |
| `Furp.Application/Features/Rewards/Engine/RewardReferralService.cs` | same path |
| `Furp.Application/Features/Rewards/Engine/RewardWalletService.cs` | same path |
| `Furp.Application/Features/Rewards/RewardCheckout.cs` | same path |
| `Furp.Application/Features/Rewards/Engine/RewardReferralRuleGuards.cs` | same path (**new file**; register `AddScoped<RewardReferralRuleGuards>()`) |

Run `git diff` and eyeball that only `// STAGING-SYNC:` regions changed relative to `Staging`. If your branch drifted from the dump these files were written against, **merge by hand** — keep your changes, add the `STAGING-SYNC` regions.

### Step 3 — Apply the surgical patches

Follow `PATCHES-AND-WIRING.md` (sections §1–§8; it ends with the apply-order checklist and the DI / constructor table). Apply order: **migration first**, then §1 (`RewardSchema.cs`, so fresh environments bootstrap the same schema as the migration), §6, the drop-ins, §2, §5, §4, §7, §3, §8. Apply **§8 (referral-rules guards) only after the duplicate-rule report of Step 1 has been reviewed** (§8.2).

### Step 4 — Build & unit tests

```powershell
dotnet build Furp.sln -c Release
dotnet test Furp.sln --filter "FullyQualifiedName~Reward"
```

Existing Reward tests must stay green. Add new tests per the checklist in §5 (do not weaken existing ones).

### Step 5 — Deploy

1. Push `feature/rewards-staging-sync` → PR → merge into `Staging`.
2. Pipeline **33** builds and deploys to `https://furp02-staging.funtechcom.com`.
3. Wait for the app pool to recycle, then run the smoke tests (§4).

---

## 3. Promotion — `pri_prod` (Pipelines 99 + 49 together)

> [!CAUTION]
> Pipelines **99 and 49 must be run together** (same release window). Running only one leaves the API and the dependent service on different engine contracts.

1. Staging smoke tests (§4) **and** the frontend Staging QA checklist (see `Rhemito-UI/staging-handoff/DEVELOPER-STAGING-TO-LIVE-INSTRUCTION-PLAN.md`) all green.
2. Written approval for the **bonus over-debit fix** (§0 warning).
3. Run `UpgradeRewardEngineStagingSync.sql` on the **Live** DB first (it is additive and safe online), snapshot first.
4. Cherry-pick the exact Staging merge commit(s) onto a hotfix branch from `pri_prod` → PR → merge to `pri_prod`. **No further edits** between Staging and Live.
5. Trigger Pipelines **99** and **49** together.
6. Run the Live smoke tests (§4, read-only subset: steps 1–4 and 9).
7. Rollback plan: redeploy the previous build artefact for 99 + 49. The SQL changes are additive and backward-compatible, so **no DB rollback is needed** (old code ignores the new columns/table).

---

## 4. Smoke tests

Replace `$H` with the environment host and `$T` with a valid customer bearer token.

```powershell
$H = "https://furp02-staging.funtechcom.com/api/v1"
```

| # | Call | Expect |
| :--- | :--- | :--- |
| 1 | `GET $H/Account/Rewards?currency=GBP` (auth) | `succeeded:true`; `data.enabled`, `kinds`, `availableBalance`, `bySource[]`, `unusedCredits[]` (each with `floor`, `receive_currency`) |
| 2 | `GET $H/Account/Rewards/Code/<VALIDCODE>?currency=GBP` (anon) | `valid:true`, `offerText`, `refereeReward`, `minimumAmount`, `windowDays` |
| 3 | `GET $H/Account/Rewards/Code/ZZ` | `succeeded:false` — `Referral codes are 6–16 letters and numbers.` |
| 3b | `GET $H/Account/Rewards/Code/<BASECODE>USD` (currency-suffixed share code, anon, **no** `currency` param) | `valid:true`, `currency:"USD"`, locked single-currency offer; the visit row stores the **full** code |
| 4 | `POST $H/Account/Rewards/Visit` `{code,visitorId,receiveCurrency}` (anon) | `succeeded:true`, **`data: true`** (unchanged `Result<bool>` contract for mobile / widget callers) |
| 4b | `POST $H/Account/Rewards/Visit/Track` `{code,visitorId,receiveCurrency,ruleId?,deviceType?,sourceChannel?}` (anon) | `data: { tracked:true, trackingId, playStoreUrl, appStoreUrl }`; a second identical call within 24h returns the **same** `trackingId`; an invalid code answers `tracked:false` (never a failure) |
| 5 | `POST $H/Account/Rewards/Link` `{code,trackingId}` (auth, new customer) | `succeeded:true`, message `Referral linked.`, `data: "REGISTERED"` |
| 6 | `POST $H/Account/Rewards/Promo/Validate` `{code,amount,fee,currency}` | `valid:true`, `discount>0`, `discountBasis`, `customerChoice` |
| 7 | Create a transfer with **promo (`REDUCE_SEND`) + bonus** | bonus debit ≤ `SendAmount − MinPayableAmount`; `RewardCreditLedger.APPLIED` row sums to the bonus shown at checkout |
| 8 | Two parallel `wallet/{id}/apply` calls for the same credit | exactly one succeeds; the other returns `409` (`WALLET_BUSY` / `BALANCE_CHANGED` / `ALREADY_APPLIED`) — never a double debit |
| 9 | `PUT $H/Affiliate/AdminSettings/Rewards/...` | `405` (IIS rule intact) |
| 10 | `mito-admin` Referral Tracking / Performance → **Export CSV** | file downloads, header row present, commas/quotes escaped (`Rewards/engine` answers `{ csv, filename, content_type }` inside `Result<T>`) |
| 11 | Mito Admin → create a 2nd referral rule for the same send currency | `409 DUPLICATE_CURRENCY` — *"A referral rule for GBP already exists (…). Only one rule per send currency is allowed — edit or archive it first."*; same name → `409 DUPLICATE_NAME` |
| 12 | Edit a referral rule, then `GET $H/Affiliate/AdminSettings/Rewards/engine/referral-rules/<id>/audit` (and `/history`) | `data: [ { id, rule_id, field, old_value, new_value, admin_user, created_at } ]`, one row per changed field, newest first |
| 13 | Promo `FixedAmount` code on a transfer | `applies_to:"amount"`; the **fee is unchanged**, the send amount is reduced; `amount < value` → `400 BELOW_MIN` |
| 14 | `POST promocodes/request-events` `{ request_id, status:"PAID_OUT" }` for a Receive Money bonus code | `{ confirmed: n }`; `CANCELLED / EXPIRED / REFUNDED / FAILED` → `{ released: n }` |

SQL spot checks:

```sql
-- new table is receiving visits
SELECT TOP 5 TrackingId, Code, AttributionStatus, DeviceType, OperatingSystem, CreatedAt
FROM dbo.RewardReferralVisit ORDER BY CreatedAt DESC;

-- floors / corridors are stored on welcome credits
SELECT TOP 5 Id, Amount, [Floor], ReceiveCurrency, CreditSource FROM dbo.RewardCreditLedger
WHERE [Floor] IS NOT NULL ORDER BY CreatedAt DESC;

-- referral rule audit trail is being written
SELECT TOP 5 RuleId, Field, OldValue, NewValue, AdminUser, CreatedAt FROM dbo.RewardReferralRuleAudit ORDER BY Id DESC;

-- no duplicate redeemed rows
SELECT PromoCode, TransferId, COUNT(*) c FROM dbo.RewardPromoRedemption
WHERE Status = N'REDEEMED' AND TransferId IS NOT NULL GROUP BY PromoCode, TransferId HAVING COUNT(*) > 1;  -- expect 0 rows
```

---

## 5. Test checklist to add (do not edit existing tests)

* Referral: visit dedupe (24h), tracking resolve, expired tracking, `cap_reached` for `COUNT` and `AMOUNT`, referrer cap blocks reward, qualification window exceeded → no reward, rule `end_date` passed → `LINK_EXPIRED`, corridor rule beats generic rule, inactive referrer → `REFERRER_INACTIVE`, closed-account duplicate rejected.
* Wallet: `BELOW_FLOOR` against `transfer_amount ?? send_amount`, wrong `receive_currency` skipped, parallel apply serialised.
* Checkout: promo `REDUCE_SEND` + full bonus never exceeds `SendAmount − MinPayableAmount`; `MinPayableAmount` default `0m` keeps old maximum (minus promo).
* Promo: `PromoSavings` counts `REDEEMED` only; affiliate-restricted code rejected for another affiliate; parallel redeem of the same code/transfer yields one row.
* Retail: `ReceiveAmount` includes `ReceiveBoost * Rate` for `INCREASE_RECEIVE`.
* Referral code: canonical `6–16` rule; currency-suffixed code resolves to the base referrer and locks the currency (explicit `currency` still wins); `LINK_EXPIRED` for an ended `rule_id`; `Visit/Track` returns the DTO while `Visit` stays `Result<bool>`.
* Rule guards: one non-archived rule per send currency (409 on create **and** update, never retroactive), unique name, one audit row per changed field, `archived 0 → 1` only once, country scope stored as upper-case CSV and echoed unchanged, active-first ordering of rules / promo codes / schemes.
* Wallet: `credit_source = REFERRAL` debits referral credits only; `SCHEME` / `BONUS` debits non-referral credits only; omitted = unchanged multi-source behaviour.
* Checkout: promo is **released** (`reason = BONUS_APPLY_FAILED`) when the wallet apply fails; `applies_to = amount` reduces the send amount and leaves the commission untouched; `affiliate_id` omitted when the user has none, `site_key = rhemito` always.
* Promo: `FixedAmount` capped by the amount (not the fee); Receive Money (`flow = receive`) flow mismatch → `FLOW`; `request-events` confirm / release.

---

## 6. IIS notes (PUT / PATCH 405 rule)

IIS on the FURP host rejects `PUT`, `PATCH` (and `DELETE`) with **405** — that is why `mito-admin` tunnels every non-GET admin call as:

```
POST /api/v1/Affiliate/AdminSettings/Rewards/engine
{ "method": "PUT", "path": "/api/referral-rules/7", "query": {}, "body": { ... } }
```

* **Do not** "fix" the 405 by enabling WebDAV/verbs globally — the tunnel is the supported path and `LocalRewardEngine.HandleAsync(method, pathAndQuery, body)` already accepts the tunnelled `method`.
* New routes in this package (`referral/performance.csv`, `referral/tracking.csv`, `referral/visits/resolve`, `referral-rules/{id}/audit` + `/history`, `promocodes/request-events`, `Account/Rewards/Visit/Track`) are all **GET/POST** only, so they work through the existing tunnel/IIS config unchanged.
* **CSV through the tunnel:** FURP answers CSV exports as `Result<T>` with `data = { csv, filename, content_type }` (PATCHES §2.1); the mito-admin client downloads it as a Blob. The prototype shim (`furpAdminCompat.js`) does the same **only when the caller opts in** (`Accept: application/json` or `?format=json`); otherwise it still passes the raw CSV through.
* The prototype (`PromoCode/server/furpAdminCompat.js`) emulates this exact behaviour locally, including the 405 for direct `PUT/PATCH/DELETE`, so `mito-admin` can be exercised without IIS.

---

## 7. Compatibility matrix (what must keep working)

| Consumer | Must not change |
| :--- | :--- |
| `Rhemito-web` (`/rewards`, `/ref/[code]`, checkout `RewardAtPayment`) | `Account/Rewards*` routes, `Result<T>` envelope, existing JSON fields (new fields are additive) |
| `mito-money-web-widget`, `MitoHostedModel` | same endpoints; `RewardEngineGateway` contract |
| `mito-admin` | `Affiliate/AdminSettings/Rewards*` tunnel, CRUD verbs, CSV export names |
| Existing ledger rows | never rewritten; `Floor`/`ReceiveCurrency` `NULL` = unrestricted (old behaviour) |

> [!NOTE]
> The prototype servers mirror this contract for local development: `Rhemito-UI/server/furpV1CompatRoutes.ts` (customer `/api/v1/Account/Rewards*`, plus `GET /api/v1/Account/Rewards/SyncManifest`) and `PromoCode/server/furpAdminCompat.js` (admin proxy). Use `SyncManifest` to diff table/endpoint lists against this package.

---

## 8. Decisions, conflicts and notes for the DBA / product owner

### 8.1 `POST Account/Rewards/Visit` stays `Result<bool>` — backward compatible
`Visit` is **unchanged** (`data: true/false`), so mobile and widget callers that read `data === true` keep working. The tracking id (Play Install Referrer / iOS hand-off) is returned by the **new** additive route `POST Account/Rewards/Visit/Track` → `Result<ReferralVisitDto>` (`{ tracked, trackingId, playStoreUrl, appStoreUrl }`). The Rhemito-UI prototype compat layer keeps its `/Visit` returning the DTO (its web adapter reads `trackingId` from it) and also exposes `/Visit/Track`; the divergence is recorded in `SyncManifest` (`endpoints[].note`). Rhemito-web calls `Visit/Track` first and falls back to `Visit`.

### 8.2 One non-archived referral rule per send currency — DBA pre-check (nothing is auto-archived)
Once §8 of `PATCHES-AND-WIRING.md` ships, **editing or creating** a referral rule answers `409 DUPLICATE_CURRENCY` when another **non-archived** rule has the same send currency. The guard is **not retroactive**, but if Staging / Live already holds more than one active rule per send currency (for example old corridor rules), editing either one returns 409 until one of them is archived.

1. Run the migration (Step 1) and read the **last result set**; it lists exactly those currencies. Equivalent stand-alone query (read-only):

   ```sql
   SELECT r.BaseCurrency AS SendCurrency, COUNT(*) AS NonArchivedRules,
          STRING_AGG(CONCAT(CAST(r.Id AS NVARCHAR(64)), N':', r.Name), N' | ') AS Rules
   FROM dbo.RewardReferralRule r
   WHERE ISNULL(r.Status, N'') <> N'ARCHIVED'
     AND ISNULL(CASE WHEN ISJSON(r.ConfigJson) = 1 THEN COALESCE(JSON_VALUE(r.ConfigJson, '$.is_archived'), JSON_VALUE(r.ConfigJson, '$.archived')) END, N'0')
         NOT IN (N'1', N'true', N'True', N'TRUE')
   GROUP BY r.BaseCurrency HAVING COUNT(*) > 1;
   ```

2. **No rows**: nothing to do. **Rows**: the product owner decides which rule to keep and archives the others from Mito Admin (Referral Settings, Archive) **before** shipping §8. Neither the migration nor the application archives, edits or deletes any rule or ledger row.
3. Existing rules keep working for customers either way (offers and awards are unaffected); only admin edits are blocked until the duplicates are resolved.

### 8.3 Country scope is metadata only
`send_country`, `receive_country`, `send_country_mode`, `receive_country_mode`, `send_countries`, `receive_countries` are **stored** in `dbo.RewardReferralRule.ConfigJson`, **echoed** by the rule GET / list responses and **audited**, but **never enforced** when an offer is built, a visit is recorded or a reward is awarded. This is exact parity with the Mito Admin prototype. Do not add runtime blocking without a new business decision.

### 8.4 Referral code rule and currency-suffixed share codes
`normalise = trim, upper-case, remove every non [A-Z0-9]`; valid = `^[A-Z0-9]{6,16}$`; message *"Referral codes are 6–16 letters and numbers."*. Generated base codes are 6–12 characters; the upper bound 16 exists only to admit `{BASE}{CUR}` share codes (suffix order: GBP, USD, EUR, NGN, CAD, AUD, JPY, CNY, INR, ZAR, KES, GHS, AED; a code of at least 9 characters that ends with one of them resolves to the base code). Currency precedence for `GET …/Code/{code}`: explicit `currency`, then the suffix currency, then the referrer's `SendCurrency`, then `GBP`; therefore `ValidateReferralCodeQuery` must forward `currency=` **only when the caller supplied it**.

### 8.5 Promo affiliate scoping
`restrictions.affiliates` is enforced **fail-open**: an empty list, or a request with neither key, is allowed. Otherwise either `affiliate_id` (the user's affiliate **GUID**, lower-case) **or** `site_key` (`rhemito`) must match a list entry (trim, case-insensitive). Therefore the Mito Admin "affiliates" entries must be the affiliate GUID **or** `rhemito`. (The prototype stores this list but never enforces it, so this is FURP-only behaviour that was already in the package.)

### 8.6 Bonus wallet `credit_source` filter (additive)
`POST wallet/{id}/apply` accepts an optional `credit_source`: `REFERRAL` debits referral credits only, `SCHEME` / `BONUS` debits non-referral credits only. When omitted (or blank) the apply debits every eligible credit as before. `RewardCheckout` does not send it, so the checkout flow is unchanged.
