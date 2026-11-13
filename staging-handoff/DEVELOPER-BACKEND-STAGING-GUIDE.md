# DEVELOPER BACKEND STAGING GUIDE — `furp02-core` Rewards Sync

**Audience:** the backend developer who owns `furp02-core` (Azure DevOps, org `Whytelion`, project `Funtech`).
**Goal:** land the PromoCode/Mito-Admin prototype behaviours on the live `LocalRewardEngine` **additively**, prove them on **Staging**, then promote the same commits to **`pri_prod`** with zero edits.

| Environment | Branch | Pipeline | URL |
| :--- | :--- | :--- | :--- |
| Staging | `Staging` | **33** | `https://furp02-staging.funtechcom.com/api/v1/` |
| Live | `pri_prod` | **99 + 49 (run together)** | `https://furp02-pre-pord.funtechcom.com/api/v1/` |

> [!IMPORTANT]
> Everything in this package is **additive**: new table, new nullable columns, new indexes, new optional fields. No route, flag, table or column is renamed or removed. Existing wallet/ledger rows are never rewritten.

---

## 0. What is in the package

```
staging-handoff/
├─ DEVELOPER-BACKEND-STAGING-GUIDE.md                ← this runbook
└─ furp02-core/
   ├─ Database/Migrations/UpgradeRewardEngineStagingSync.sql
   └─ Furp.Application/Features/Rewards/
      ├─ RewardCheckout.cs                           ← drop-in (replaces file)
      ├─ PATCHES-AND-WIRING.md                       ← surgical diffs for 7 other files
      └─ Engine/
         ├─ RewardReferralService.cs                 ← drop-in (replaces file)
         └─ RewardWalletService.cs                   ← drop-in (replaces file)
```

| # | Change | Fixes / adds |
| :--- | :--- | :--- |
| 1 | `UpgradeRewardEngineStagingSync.sql` | `dbo.RewardReferralVisit`; `RewardCreditLedger.Floor` + `ReceiveCurrency`; `RewardTransferFact.PaymentMethod`; promo redemption unique + lookup indexes |
| 2 | `RewardReferralService.cs` | real visit/tracking (`VisitAsync`, `ResolveTrackingAsync`), cap enforcement + `cap_reached`, qualification window, rule date window + corridor priority, `REFERRER_INACTIVE`, closed-account duplicate check, `currency_offers`, floor/corridor stored on welcome credit, tracking & performance CSV |
| 3 | `RewardWalletService.cs` | `sp_getapplock` concurrency guard on apply; `BELOW_FLOOR` and corridor (`ReceiveCurrency`) enforcement |
| 4 | `RewardCheckout.cs` | **live stacking bonus over-debit bug** (promo `REDUCE_SEND` + bonus); `send_amount`/`transfer_amount` sent to ledger; `affiliate_id`/`site_key` on redeem; configurable `MinPayableAmount` (default `0m`) |
| 5 | `PATCHES-AND-WIRING.md` | `RewardSchema.cs`, `LocalRewardEngine.cs`, `RewardCustomerCommands.cs`, `RewardCommands.cs`, `RewardPromoService.cs`, `RewardSchemeAwards.cs`, `CreateRetailTransactionCommandHandler.cs` |

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

### Step 1 — Database migration (idempotent)

```powershell
sqlcmd -S <staging-sql-host> -d <staging-db> -E -I -b -i Database\Migrations\UpgradeRewardEngineStagingSync.sql
```

* `-I` keeps `QUOTED_IDENTIFIER ON` (needed for the filtered indexes; the script also sets it).
* Re-running is a no-op. The last result set must show `Present = 1` on all 6 rows.
* If it stops with **error 51003** (duplicate `REDEEMED` promo rows for one `(PromoCode, TransferId)`), **nothing was changed**. Review the query printed in the message, mark the surplus rows `RELEASED`, then re-run. Do not delete rows.

### Step 2 — Copy the three drop-in files

Copy into the matching paths in `furp02-core` (overwrite):

| From (this package) | To (`furp02-core`) |
| :--- | :--- |
| `Furp.Application/Features/Rewards/Engine/RewardReferralService.cs` | same path |
| `Furp.Application/Features/Rewards/Engine/RewardWalletService.cs` | same path |
| `Furp.Application/Features/Rewards/RewardCheckout.cs` | same path |

Run `git diff` and eyeball that only `// STAGING-SYNC:` regions changed relative to `Staging`. If your branch drifted from the dump these files were written against, **merge by hand** — keep your changes, add the `STAGING-SYNC` regions.

### Step 3 — Apply the surgical patches

Follow `PATCHES-AND-WIRING.md` top to bottom (7 files, ~10 small edits). Order matters only for §1 (`RewardSchema.cs`) — apply it first so fresh environments bootstrap the same schema as the migration.

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
| 3 | `GET $H/Account/Rewards/Code/ZZ` | `succeeded:false` — format message (3–32 chars after patch) |
| 4 | `POST $H/Account/Rewards/Visit` `{code,visitorId,receiveCurrency}` (anon) | `succeeded:true`; second identical call within 24h returns the **same** `trackingId` |
| 5 | `POST $H/Account/Rewards/Link` `{code,trackingId}` (auth, new customer) | `succeeded:true`, message `Referral linked.`, `data: "REGISTERED"` |
| 6 | `POST $H/Account/Rewards/Promo/Validate` `{code,amount,fee,currency}` | `valid:true`, `discount>0`, `discountBasis`, `customerChoice` |
| 7 | Create a transfer with **promo (`REDUCE_SEND`) + bonus** | bonus debit ≤ `SendAmount − MinPayableAmount`; `RewardCreditLedger.APPLIED` row sums to the bonus shown at checkout |
| 8 | Two parallel `wallet/{id}/apply` calls for the same credit | exactly one succeeds; the other returns `409` (`WALLET_BUSY` / `BALANCE_CHANGED` / `ALREADY_APPLIED`) — never a double debit |
| 9 | `PUT $H/Affiliate/AdminSettings/Rewards/...` | `405` (IIS rule intact) |
| 10 | `mito-admin` Referral Tracking / Performance → **Export CSV** | file downloads, header row present, commas/quotes escaped |

SQL spot checks:

```sql
-- new table is receiving visits
SELECT TOP 5 TrackingId, Code, AttributionStatus, DeviceType, OperatingSystem, CreatedAt
FROM dbo.RewardReferralVisit ORDER BY CreatedAt DESC;

-- floors / corridors are stored on welcome credits
SELECT TOP 5 Id, Amount, [Floor], ReceiveCurrency, CreditSource FROM dbo.RewardCreditLedger
WHERE [Floor] IS NOT NULL ORDER BY CreatedAt DESC;

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

---

## 6. IIS notes (PUT / PATCH 405 rule)

IIS on the FURP host rejects `PUT`, `PATCH` (and `DELETE`) with **405** — that is why `mito-admin` tunnels every non-GET admin call as:

```
POST /api/v1/Affiliate/AdminSettings/Rewards/engine
{ "method": "PUT", "path": "/api/referral-rules/7", "query": {}, "body": { ... } }
```

* **Do not** "fix" the 405 by enabling WebDAV/verbs globally — the tunnel is the supported path and `LocalRewardEngine.HandleAsync(method, pathAndQuery, body)` already accepts the tunnelled `method`.
* New routes in this package (`referral/performance.csv`, `referral/tracking.csv`, `referral/visits/resolve`) are all **GET/POST** only, so they work through the existing tunnel/IIS config unchanged.
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
