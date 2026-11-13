# Developer Handoff Guide: `mito-admin` Staging (Pipeline 74) → Live Production (Pipeline 65)

This guide covers deploying and verifying the **Mito Admin Rewards Hub** (`Referral Settings`, `Referral Performance`, `Referral Tracking`, `Promo Codes`, `Bonus Scheme Manager`, `User Segments`, `Bonus Wallet / Ledger`, `Blocked Customers`, `Rate Audit Log`, and `Financials` `[PC]`/`[RF]`/`[BS]` reward tags) into the **`mito-admin`** repository — first on **Staging (`staging` branch → Azure Pipeline 74)** against **`https://furp02-staging.funtechcom.com/`**, then promoted with **zero code edits** to **Live (`prod` branch → Azure Pipeline 65)**.

---

## 1. Environment & Pipeline Topology

| Environment | `mito-admin` Branch | Azure Pipeline | Admin Portal URL | Target `furp02-core` Origin | `furp02-core` Gateway Prefix |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Staging** | `staging` | **Pipeline 74** | Staging MITO Admin | `https://furp02-staging.funtechcom.com/` | `https://furp02-staging.funtechcom.com/api/v1/Affiliate/AdminSettings/Rewards` |
| **Live Production** | `prod` | **Pipeline 65** (`mito-admin` Heroku) | `https://admin.mito.money/` | `https://furp02-pre-pord.funtechcom.com/` | `https://furp02-pre-pord.funtechcom.com/api/v1/Affiliate/AdminSettings/Rewards` |

> **Backend Prerequisite:** Before testing `mito-admin` on Staging (or promoting to Live), ensure `UpgradeRewardEngineStagingSync.sql` has been executed via `sqlcmd -b -I` and `furp02-core` has been deployed (**Pipeline 33** on Staging; **Pipelines 99 + 49** on Live) per `PromoCode/staging-handoff/DEVELOPER-BACKEND-STAGING-GUIDE.md`.

---

## 2. Why `furpRewardsAdminClient.js` & `downloadCsv.js` Are Required

1. **IIS HTTP 405 Verb Tunneling (`PUT` / `PATCH` / `DELETE` Forbidden on Wire):**
   - `furp02-core` runs behind Windows Server IIS (`https://furp02-staging.funtechcom.com/`), where WebDAV / request filtering blocks raw `PUT`, `PATCH`, and `DELETE` verbs with **`HTTP 405 Method Not Allowed`**.
   - `src/api/furpRewardsAdminClient.js` routes every Rewards admin request through `AffiliateAdminSettingsController`:
     - **`GET` requests:** `GET {FURP_ORIGIN}/api/v1/Affiliate/AdminSettings/Rewards/engine/<path-without-/api/>?<query>`
     - **All non-`GET` mutations (`POST`, `PUT`, `PATCH`, `DELETE`):** `POST {FURP_ORIGIN}/api/v1/Affiliate/AdminSettings/Rewards/engine` with JSON body:
       ```json
       {
         "method": "PUT",
         "path": "/api/referral-rules/4",
         "query": {},
         "body": { "...": "..." }
       }
       ```
     - **`PUT`, `PATCH`, and `DELETE` are never sent on the HTTP wire.**
2. **`Result<T>` Envelope Unwrapping & Structured Validation Errors:**
   - Automatically unwraps `{ succeeded, code, message, data }` from `furp02-core`.
   - When `succeeded === false` or HTTP status is non-2xx, throws an `Error` carrying `.status`, `.code`, `.error`, `.fields`, and `.message` so inline form validation (`DUPLICATE_CURRENCY`, `DUPLICATE_NAME`, `IN_USE`, `fields`), 401/403 Growth Manager token prompts, and toast messages work identically to the prototype.
3. **Authenticated JSON → `Blob` CSV Exports (`src/api/downloadCsv.js`):**
   - Plain `<a href="/api/...csv">` links cannot attach `Authorization: Bearer <admin-jwt>` and hit the `mito-admin` SPA host instead of FURP.
   - `downloadCsv(path, query, fallbackFilename)` calls `furpRewardsAdminClient.call('GET', path, { query: { ...query, format: 'json' } })` with `Accept: application/json` + Bearer JWT, receives `{ csv, filename, content_type }` inside `Result<T>`, constructs a `Blob`, and triggers `<a download={filename}>`.

---

## 3. Package Inventory (`PromoCode/staging-handoff/mito-admin/`)

All 33 network call sites and 5 CSV export actions across the Rewards admin UI have been pre-wired to `furpRewardsAdminClient` and `downloadCsv` (**zero** raw `fetch('/api/...')` calls remain):

```text
PromoCode/staging-handoff/mito-admin/
  src/
    api/
      furpRewardsAdminClient.js       # IIS-405-safe client + Result<T> unwrapper + URL normaliser
      downloadCsv.js                  # Authenticated JSON -> Blob CSV export helper
    assets/
      figma-referrals/*.svg           # 16 Figma SVG icons used across Rewards Hub, Referral & Promo pages
    components/
      Feedback.jsx                    # ToastStack & ConfirmDialog modals
      useToasts.js                    # Toast state hook
    lib/
      adminAuth.js                    # Growth Manager override token helper (ReferralTracking / BonusBlocks)
    styles/
      rewardsShared.css               # Scoped .rewards-hub-root / .rf-page button, panel & table styles
    patches/
      FinancialsRewardsTagSnippet.jsx # Drop-in [PC]/[RF]/[BS] formatter & hook for Financials screens
    pages/
      Configuration/
        RewardsHub.jsx                # Unified /configuration/rewards hub (Sites, Promo, Referrals, Bonus, Ledger, Audit)
        RateAuditLog.jsx              # Read-only Reward$ rate audit log
      Growth/
        ReferralSettings.jsx          # Referral scheme CRUD, country scope modal, 3-tab History & Audit modal
        ReferralTracking.jsx          # Registered Referrals + Link Clicks & App Installs + manual approval
        ReferralPerformance.jsx       # Rule KPIs, date presets, Top Referrers drawer, CSV export
        referralUtils.js              # Currencies, countries, formatting helpers, wired downloadUrl
        referralValidation.js         # Client-side referral rule validation
        ColHead.jsx                   # Column header with tooltip
        StatusPill.jsx                # Status badge component
        referral.css                  # Scoped Referral & Rewards Hub CSS
      Promo/
        PromoCodesPage.jsx            # Promo KPIs, active-first table, Usage & Change History modals, CSV exports
        PromoCodeForm.jsx             # Create/Edit promo modal (Fixed, FixedAmount, Percentage, Waiver; Send/Receive)
        BulkCodesModal.jsx            # Batch promo generation modal
        promo.css                     # Scoped Promo CSS
        lib/
          promoUtils.js               # Promo formatting & live preview sentence, wired downloadUrl
          promoForm.js                # Form state mapper
          promoValidation.js          # Promo validation rules
      Bonus/
        BonusSchemeManager.jsx        # Loyalty, Threshold & Request Money schemes + History modal
        SchemeForm.jsx                # Tiered/flat bonus scheme form
        SegmentManager.jsx            # Audience segment CRUD + live customer count preview
        UserCreditLedger.jsx          # Multi-currency bonus wallet ledger, manual adjustment & CSV export
        BonusBlocks.jsx               # Cancel/refund strike blocks & Growth Manager lift flow
        bonus.css                     # Scoped Bonus CSS
        components/FormBits.jsx       # Shared Bonus form controls
        lib/
          bonusUtils.js               # Bonus display helpers
          schemeForm.js               # Scheme payload mapper
          schemeValidation.js         # Scheme, segment & manual adjustment validators
```

---

## 4. Step-by-Step Integration into `mito-admin` (`staging` Branch)

### Step 4.1 — Copy Drop-In Files into `mito-admin`

Copy `src/` from this handoff folder into your local `mito-admin` checkout (on branch `staging`). None of these paths overwrite existing `mito-admin` screens unless a previous prototype of Rewards was added:

```powershell
$HandoffSrc = "c:\Users\Khan1\OneDrive\Desktop\drive_d_data\PromoCode\staging-handoff\mito-admin\src"
$MitoAdminSrc = "C:\path\to\mito-admin\src"

Copy-Item "$HandoffSrc\api\*" "$MitoAdminSrc\api\" -Force -Recurse
New-Item -ItemType Directory -Force -Path "$MitoAdminSrc\assets\figma-referrals", "$MitoAdminSrc\styles", "$MitoAdminSrc\patches" | Out-Null
Copy-Item "$HandoffSrc\assets\figma-referrals\*" "$MitoAdminSrc\assets\figma-referrals\" -Force
Copy-Item "$HandoffSrc\components\Feedback.jsx" "$MitoAdminSrc\components\" -Force
Copy-Item "$HandoffSrc\components\useToasts.js" "$MitoAdminSrc\components\" -Force
New-Item -ItemType Directory -Force -Path "$MitoAdminSrc\lib" | Out-Null
Copy-Item "$HandoffSrc\lib\adminAuth.js" "$MitoAdminSrc\lib\" -Force
Copy-Item "$HandoffSrc\styles\rewardsShared.css" "$MitoAdminSrc\styles\" -Force
Copy-Item "$HandoffSrc\patches\FinancialsRewardsTagSnippet.jsx" "$MitoAdminSrc\patches\" -Force

New-Item -ItemType Directory -Force -Path "$MitoAdminSrc\pages\Growth", "$MitoAdminSrc\pages\Promo\lib", "$MitoAdminSrc\pages\Bonus\components", "$MitoAdminSrc\pages\Bonus\lib", "$MitoAdminSrc\pages\Configuration" | Out-Null
Copy-Item "$HandoffSrc\pages\Growth\*" "$MitoAdminSrc\pages\Growth\" -Force -Recurse
Copy-Item "$HandoffSrc\pages\Promo\*" "$MitoAdminSrc\pages\Promo\" -Force -Recurse
Copy-Item "$HandoffSrc\pages\Bonus\*" "$MitoAdminSrc\pages\Bonus\" -Force -Recurse
Copy-Item "$HandoffSrc\pages\Configuration\RewardsHub.jsx" "$MitoAdminSrc\pages\Configuration\" -Force
Copy-Item "$HandoffSrc\pages\Configuration\RateAuditLog.jsx" "$MitoAdminSrc\pages\Configuration\" -Force
```

### Step 4.2 — Environment Variables & Optional Auth Hook

`furpRewardsAdminClient.js` resolves the FURP base URL in this order:
1. `configureFurpRewardsClient({ getBaseUrl })` (if configured in `src/index.js` / `src/App.js`)
2. `process.env.REACT_APP_FURP_BASE_URL`
3. `process.env.REACT_APP_API_URL` *(standard Create React App variable already used by `mito-admin`)*
4. `process.env.VITE_FURP_BASE_URL` / `process.env.VITE_API_URL`
5. Default Staging fallback: **`https://furp02-staging.funtechcom.com`**

**Automatic URL Normalisation:** Whether the pipeline variable is set to `https://furp02-staging.funtechcom.com`, `https://furp02-staging.funtechcom.com/`, `https://furp02-staging.funtechcom.com/api/v1`, or `https://furp02-staging.funtechcom.com/api/v1/`, `normaliseFurpOrigin()` strips trailing slashes and `/api/v1` so requests **never** produce `//api/v1` or `/api/v1/api/v1`.

**Admin JWT Discovery:** By default, `resolveAdminBearerToken()` reads the active admin JWT from `sessionStorage` / `localStorage` (`token`, `accessToken`, `access_token`, `authToken`, `jwt`, `id_token`, or Redux Persist `persist:root`). If `mito-admin` holds its JWT exclusively in an in-memory Redux store or Axios instance, add this one-time call in `src/index.js` (or `src/App.js`):

```javascript
import { configureFurpRewardsClient } from './api/furpRewardsAdminClient';
import { store } from './store'; // your existing mito-admin Redux store

configureFurpRewardsClient({
  getAccessToken: () => store.getState()?.auth?.token || null,
  // Optional if mito-admin already centralises its API base URL in a config module:
  // getBaseUrl: () => API_BASE_URL,
});
```

### Step 4.3 — Wire Routes & Sidebar Link in `mito-admin`

Add the unified Rewards Hub route (and optional deep-link aliases used by cross-page links) inside `mito-admin`'s authenticated router:

```jsx
import RewardsHub from './pages/Configuration/RewardsHub';
import ReferralSettings from './pages/Growth/ReferralSettings';
import ReferralPerformance from './pages/Growth/ReferralPerformance';
import ReferralTracking from './pages/Growth/ReferralTracking';
import PromoCodesPage from './pages/Promo/PromoCodesPage';
import BonusSchemeManager from './pages/Bonus/BonusSchemeManager';
import UserCreditLedger from './pages/Bonus/UserCreditLedger';
import BonusBlocks from './pages/Bonus/BonusBlocks';

// Inside your authenticated <Routes>:
<Route path="/configuration/rewards" element={<RewardsHub />} />
<Route path="/growth/referral-settings" element={<ReferralSettings />} />
<Route path="/growth/referral-performance" element={<ReferralPerformance />} />
<Route path="/growth/referral-tracking" element={<ReferralTracking />} />
<Route path="/growth/promo-codes" element={<PromoCodesPage />} />
<Route path="/growth/bonus-schemes" element={<BonusSchemeManager />} />
<Route path="/growth/bonus-ledger" element={<UserCreditLedger />} />
<Route path="/growth/bonus-blocks" element={<BonusBlocks />} />
```

In `mito-admin`'s sidebar under **Configurations**, ensure the **Rewards** sub-item points to `/configuration/rewards`.

### Step 4.4 — Wire `[PC]` / `[RF]` / `[BS]` Rewards Display into Financials Screens

Use `src/patches/FinancialsRewardsTagSnippet.jsx` (**diff & merge — do not replace existing Financials pages**) to display `{CUR} {amount} [{PC|RF|BS}] ({Name})` in `mito-admin`'s existing Transactions list and Transaction Detail screens:

```jsx
// 1. In Transactions list screen (/financials/transactions):
import { formatMitoAdminReward } from '../../patches/FinancialsRewardsTagSnippet';

// Render cell in the Transactions table:
<td>{formatMitoAdminReward(tx)}</td>

// 2. In Transaction detail screen (/financials/transactions/:id):
import { useTransactionRewardDisplay } from '../../patches/FinancialsRewardsTagSnippet';

// Inside component:
const rewardDisplay = useTransactionRewardDisplay(tx?.reference || id, tx);
// Pass { label: 'Rewards', value: rewardDisplay } to your Summary / Financial Details card.
```

---

## 5. Important Business & Operational Rules on Staging / Live

1. **One Non-Archived Referral Rule Per Send Currency (HTTP 409 `DUPLICATE_CURRENCY`):**
   - `furp02-core` (`RewardReferralRuleGuards`) enforces at most **one** non-archived referral rule per `base_currency` (e.g., one rule for `GBP`, one for `EUR`, one for `USD`) and unique rule names (`DUPLICATE_NAME`).
   - **Data Warning:** If Staging already has multiple active corridor rules for the same send currency (reported by the final `SELECT` in `UpgradeRewardEngineStagingSync.sql`), archive the extra rules in **Referral Settings** first — otherwise editing either duplicate rule will return `409 DUPLICATE_CURRENCY` until only one remains non-archived.
2. **Country Scope (`send_countries` / `receive_countries`) Is Metadata-Only:**
   - Country scope (`ALL`, `INCLUDE`, `EXCLUDE` + ISO-2 codes) is persisted in `ConfigJson`, audited in `RewardReferralRuleAudit`, and displayed in `ReferralSettings` / `RuleHistoryModal` — it is **not** enforced during customer offer lookup, visit tracking, or reward payout (matching prototype behaviour).
3. **Promo Code `affiliates` Restriction Values:**
   - When restricting a promo code to specific affiliates/sites, entries in `affiliates` must be either the **affiliate GUID** (lowercase `D` format) or **`rhemito`**. Leaving `affiliates` empty allows all affiliates/sites.
4. **Promo `FixedAmount` & Receive Money Flows:**
   - `FixedAmount` (`Fixed amount (off send amount)`) requires `value <= min_threshold` (when a minimum threshold is set) and reduces the customer's send amount while leaving transfer commission unchanged.
   - `Receive Money` promo codes allow `Fixed` or `Percentage` bonus types only; a single promo code cannot cover `both` flows (`Send Money` or `Receive Money` must be chosen).

---

## 6. Staging Deployment & QA Checklist (Azure Pipeline 74)

1. **Deploy `mito-admin` (`staging` branch) via Azure Pipeline 74:**
   - Verify `REACT_APP_API_URL` (or `REACT_APP_FURP_BASE_URL`) in Pipeline 74 points to `https://furp02-staging.funtechcom.com/` (or `https://furp02-staging.funtechcom.com/api/v1/`).
   - Confirm `furp02-staging.funtechcom.com` CORS policy includes the Staging `mito-admin` origin.
2. **Verify Zero HTTP 405 & Authenticated Gateway Calls in Browser DevTools → Network:**
   - Open `/configuration/rewards` while signed into Staging MITO Admin.
   - Confirm all reads hit `GET https://furp02-staging.funtechcom.com/api/v1/Affiliate/AdminSettings/Rewards/engine/...` with `Authorization: Bearer <jwt>`.
   - Toggle a Referral Rule status (`PATCH`), edit a Referral Rule (`PUT`), create/disable/delete an unused Promo Code (`POST`/`PUT`/`DELETE`), and update a Bonus Scheme (`PUT`/`PATCH`): confirm **every mutation** goes out on the wire as `POST .../Rewards/engine` and returns HTTP 200 `{ succeeded: true, ... }` (zero `PUT`/`PATCH`/`DELETE` verbs on the wire, zero HTTP 405).
3. **Verify Referral Rule History & Audit (`GET referral-rules/{id}/audit`):**
   - Edit a rule in **Referral Settings** → click the **History** icon → open the **Audit Log** tab and confirm `created` / field-diff rows (`created_at`, `field`, `old_value`, `new_value`, `admin_user`) render from `dbo.RewardReferralRuleAudit`.
4. **Verify Active-First Sorting:**
   - Confirm **Referral Settings**, **Referral Performance**, **Promo Codes**, and **Bonus Scheme Manager** list `Active` items first, followed by `Scheduled`, `Inactive`/`Disabled`, `Ended`/`Expired`, and `Archived`.
5. **Verify Authenticated CSV Exports (All 5 Export Buttons):**
   - Click **Export CSV** in:
     1. `ReferralSettings` → Rule History modal (`Export Referrals CSV`)
     2. `ReferralTracking` → Filter bar (`Export CSV`)
     3. `ReferralPerformance` → Header (`Export CSV`)
     4. `PromoCodesPage` → Filter bar (`Export CSV`) & Usage modal (`Export CSV`)
     5. `UserCreditLedger` → Header (`Export CSV`)
   - Confirm each request sends `GET .../engine/<route>.csv?...&format=json` with `Authorization: Bearer <jwt>` and downloads a valid `.csv` file.

---

## 7. Zero-Edit Live Promotion (`staging` → `prod` → Azure Pipeline 65)

Once Staging QA is signed off:

1. **SQL Migration & `furp02-core` Live First:**
   - Run `UpgradeRewardEngineStagingSync.sql` via `sqlcmd -b -I` on Live SQL Server, then deploy `furp02-core` (`pri_prod`) via **Azure Pipelines 99 + 49 together**.
2. **Promote `mito-admin` (`staging` → `prod`):**
   - Confirm **Azure Pipeline 65** (`mito-admin` Heroku → `https://admin.mito.money/`) has `REACT_APP_API_URL` (or `REACT_APP_FURP_BASE_URL`) set to Live FURP (`https://furp02-pre-pord.funtechcom.com/` or `https://furp02-pre-pord.funtechcom.com/api/v1/`).
   - Merge `staging` into `prod` (**zero code changes required**) and run **Azure Pipeline 65**.
   - Smoke-test `/configuration/rewards` on `https://admin.mito.money/` (list load + CSV download).
