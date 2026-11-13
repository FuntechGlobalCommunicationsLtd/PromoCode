/* =====================================================================================================
   UpgradeRewardEngineStagingSync.sql
   furp02-core  ::  Database/Migrations/UpgradeRewardEngineStagingSync.sql

   Purpose : Bring the FURP `Reward$` engine schema in line with the Mito Admin referral / bonus / promo
             prototype (PromoCode repo) so the Staging -> pri_prod promotion needs zero manual SQL.
   Safety  : 100% ADDITIVE and IDEMPOTENT. Re-running is a no-op. No column is dropped, renamed or re-typed,
             no row is deleted, archived or rewritten. Safe to run with the application online.
   Target  : SQL Server (dbo schema). Run AFTER AddRewardEngineLocalSchema.sql (the 13 dbo.Reward* tables
             must already exist; RewardSchema.cs creates them at runtime on first use).

   RUN WITH: sqlcmd -S <server> -d <db> -b -I -i UpgradeRewardEngineStagingSync.sql
             (-b = non-zero exit code on error, -I = QUOTED_IDENTIFIER ON). Run it BEFORE the application deploy.

   Adds
     1. dbo.RewardReferralVisit                      (link visit / click / install-referrer tracking)
     2. dbo.RewardCreditLedger.Floor                 DECIMAL(18,2) NULL   (minimum transfer for a welcome credit)
     3. dbo.RewardCreditLedger.ReceiveCurrency       CHAR(3) NULL         (corridor lock for a credit)
     4. dbo.RewardTransferFact.PaymentMethod         NVARCHAR(50) NULL    (bonus scheme paymentMethods eligibility)
     5. UX_RewardPromoRedemption_Code_Transfer_Redeemed   (no duplicate REDEEMED row per promo + transfer)
     6. IX_RewardPromoRedemption_CodeStatusUser           (promo cap checks + customer PromoSavings)
     7. dbo.RewardReferralRuleAudit                  (referral rule change history: GET referral-rules/{id}/audit)
     8. dbo.RewardPromoRedemption.Flow               NVARCHAR(16) NULL    (NULL = send; 'receive' = Receive Money bonus code)

   REPORT  : after the commit a read-only SELECT lists every send currency that already has MORE THAN ONE
             non-archived referral rule. NOTHING is archived or edited here. The one-rule-per-currency guard
             (RewardReferralRuleGuards) answers HTTP 409 when such a rule is edited, so the product owner / a DBA
             must archive the extras (in Mito Admin) BEFORE the guard ships. The report never fails the script.

   NOTE: RewardSchema.cs must carry the same DDL (see PATCHES-AND-WIRING.md section 1) so that fresh
         environments bootstrapped at runtime end up identical to migrated ones.
   ===================================================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON; -- required for the filtered indexes below (sqlcmd defaults to OFF)
SET ANSI_NULLS ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /* ------------------------------------------------------------------------------------------------
       Guard: the base engine tables must exist (otherwise run AddRewardEngineLocalSchema.sql first).
       ------------------------------------------------------------------------------------------------ */
    IF OBJECT_ID(N'dbo.RewardCreditLedger', N'U') IS NULL
        THROW 51000, N'dbo.RewardCreditLedger is missing. Run AddRewardEngineLocalSchema.sql (or start the API once so RewardSchema.cs bootstraps it) before this migration.', 1;
    IF OBJECT_ID(N'dbo.RewardTransferFact', N'U') IS NULL
        THROW 51001, N'dbo.RewardTransferFact is missing. Run AddRewardEngineLocalSchema.sql before this migration.', 1;
    IF OBJECT_ID(N'dbo.RewardPromoRedemption', N'U') IS NULL
        THROW 51002, N'dbo.RewardPromoRedemption is missing. Run AddRewardEngineLocalSchema.sql before this migration.', 1;

    /* ------------------------------------------------------------------------------------------------
       1. dbo.RewardReferralVisit  (mirrors PromoCode referral_link_visits)
          TrackingId   : unpredictable id handed to the web / Play Install Referrer / App Store hand-off
          RuleId       : referral rule the link was locked to (?rule=)
          DeviceType   : DESKTOP | MOBILE | TABLET | UNKNOWN
          OperatingSystem : ANDROID | IOS | WINDOWS | MACOS | LINUX | UNKNOWN
          SourceChannel   : LINK | QR | SHARE | MANUAL | ANDROID_INSTALL_REFERRER | IOS_UNIVERSAL_LINK
          AttributionStatus : CLICKED -> HANDOFF -> RESOLVED -> LINKED | EXPIRED
          StoreTarget     : PLAY_STORE | APP_STORE | WEB
          LinkedReferralId / LinkedRefereeId : set once the visit converts to dbo.RewardReferral
          ExpiresAt       : tracking id validity (attribution window)
          Each index has its OWN guard (outside CREATE TABLE) so a table created earlier by the old lazy DDL
          still receives the canonical indexes.
       ------------------------------------------------------------------------------------------------ */
    IF OBJECT_ID(N'dbo.RewardReferralVisit', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.RewardReferralVisit
        (
            Id                 NVARCHAR(64)  NOT NULL CONSTRAINT PK_RewardReferralVisit PRIMARY KEY,
            TrackingId         NVARCHAR(64)  NULL,
            Code               NVARCHAR(32)  NULL,
            ReferrerId         NVARCHAR(64)  NULL,
            RuleId             NVARCHAR(64)  NULL,
            Currency           CHAR(3)       NULL,
            ReceiveCurrency    CHAR(3)       NULL,
            VisitorId          NVARCHAR(64)  NULL,
            DeviceType         NVARCHAR(32)  NULL,
            OperatingSystem    NVARCHAR(32)  NULL,
            SourceChannel      NVARCHAR(32)  NOT NULL CONSTRAINT DF_RewardReferralVisit_SourceChannel DEFAULT (N'LINK'),
            AttributionStatus  NVARCHAR(32)  NOT NULL CONSTRAINT DF_RewardReferralVisit_AttributionStatus DEFAULT (N'CLICKED'),
            StoreTarget        NVARCHAR(32)  NULL,
            ResolvedAt         DATETIME2     NULL,
            LinkedReferralId   NVARCHAR(64)  NULL,
            LinkedRefereeId    NVARCHAR(64)  NULL,
            ExpiresAt          DATETIME2     NULL,
            CreatedAt          DATETIME2     NOT NULL CONSTRAINT DF_RewardReferralVisit_CreatedAt DEFAULT (SYSUTCDATETIME())
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RewardReferralVisit_TrackingId' AND object_id = OBJECT_ID(N'dbo.RewardReferralVisit'))
        CREATE UNIQUE INDEX UX_RewardReferralVisit_TrackingId
            ON dbo.RewardReferralVisit (TrackingId)
            WHERE TrackingId IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardReferralVisit_CodeVisitor' AND object_id = OBJECT_ID(N'dbo.RewardReferralVisit'))
        CREATE INDEX IX_RewardReferralVisit_CodeVisitor
            ON dbo.RewardReferralVisit (Code, VisitorId, CreatedAt DESC);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardReferralVisit_Referrer' AND object_id = OBJECT_ID(N'dbo.RewardReferralVisit'))
        CREATE INDEX IX_RewardReferralVisit_Referrer
            ON dbo.RewardReferralVisit (ReferrerId, CreatedAt DESC);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardReferralVisit_LinkedReferee' AND object_id = OBJECT_ID(N'dbo.RewardReferralVisit'))
        CREATE INDEX IX_RewardReferralVisit_LinkedReferee
            ON dbo.RewardReferralVisit (LinkedRefereeId)
            WHERE LinkedRefereeId IS NOT NULL;

    /* NOTE: the OLD lazy DDL (RewardReferralService.StagingDdlSql before this sync) may have created
       UX_RewardReferralVisit_Tracking, IX_RewardReferralVisit_Linked and IX_RewardReferralVisit_Currency on Staging.
       They are harmless and are intentionally LEFT in place (a DBA may drop them; this script never does). */

    /* ------------------------------------------------------------------------------------------------
       2 + 3. dbo.RewardCreditLedger: Floor + ReceiveCurrency
          Existing rows keep NULL = "no floor / any corridor", so current wallet behaviour is unchanged.
       ------------------------------------------------------------------------------------------------ */
    IF COL_LENGTH(N'dbo.RewardCreditLedger', N'Floor') IS NULL
        ALTER TABLE dbo.RewardCreditLedger ADD [Floor] DECIMAL(18,2) NULL;

    IF COL_LENGTH(N'dbo.RewardCreditLedger', N'ReceiveCurrency') IS NULL
        ALTER TABLE dbo.RewardCreditLedger ADD ReceiveCurrency CHAR(3) NULL;

    /* ------------------------------------------------------------------------------------------------
       4. dbo.RewardTransferFact.PaymentMethod
       ------------------------------------------------------------------------------------------------ */
    IF COL_LENGTH(N'dbo.RewardTransferFact', N'PaymentMethod') IS NULL
        ALTER TABLE dbo.RewardTransferFact ADD PaymentMethod NVARCHAR(50) NULL;

    /* ------------------------------------------------------------------------------------------------
       5. Prevent duplicate REDEEMED rows for the same PromoCode + TransferId (retry / concurrency).
          Pre-flight: if duplicates already exist in live data, the unique index cannot be built. We do NOT
          delete anything - we stop with a clear message so a DBA can decide which rows to RELEASE.
       ------------------------------------------------------------------------------------------------ */
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RewardPromoRedemption_Code_Transfer_Redeemed' AND object_id = OBJECT_ID(N'dbo.RewardPromoRedemption'))
    BEGIN
        IF EXISTS (
            SELECT 1
            FROM dbo.RewardPromoRedemption
            WHERE Status = N'REDEEMED' AND TransferId IS NOT NULL
            GROUP BY PromoCode, TransferId
            HAVING COUNT(*) > 1)
        BEGIN
            THROW 51003, N'Duplicate REDEEMED rows exist in dbo.RewardPromoRedemption for the same (PromoCode, TransferId). Review with: SELECT PromoCode, TransferId, COUNT(*) FROM dbo.RewardPromoRedemption WHERE Status=N''REDEEMED'' AND TransferId IS NOT NULL GROUP BY PromoCode, TransferId HAVING COUNT(*)>1; then set the extra rows to Status=N''RELEASED'' and re-run. Nothing was changed.', 1;
        END;

        EXEC (N'CREATE UNIQUE INDEX UX_RewardPromoRedemption_Code_Transfer_Redeemed
                    ON dbo.RewardPromoRedemption (PromoCode, TransferId)
                    WHERE Status = N''REDEEMED'' AND TransferId IS NOT NULL;');
    END;

    /* ------------------------------------------------------------------------------------------------
       6. Speed up global / per-user promo cap checks and customer PromoSavings
       ------------------------------------------------------------------------------------------------ */
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardPromoRedemption_CodeStatusUser' AND object_id = OBJECT_ID(N'dbo.RewardPromoRedemption'))
        CREATE INDEX IX_RewardPromoRedemption_CodeStatusUser
            ON dbo.RewardPromoRedemption (PromoCode, Status, UserId)
            INCLUDE (Discount, Currency, TransferId);

    /* ------------------------------------------------------------------------------------------------
       7. dbo.RewardReferralRuleAudit  (referral rule change history)
          RuleId is NVARCHAR so it works whether dbo.RewardReferralRule.Id is an int or a string.
       ------------------------------------------------------------------------------------------------ */
    IF OBJECT_ID(N'dbo.RewardReferralRuleAudit', N'U') IS NULL
    CREATE TABLE dbo.RewardReferralRuleAudit (
        Id        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RewardReferralRuleAudit PRIMARY KEY,
        RuleId    NVARCHAR(64)  NOT NULL,
        Field     NVARCHAR(64)  NOT NULL,
        OldValue  NVARCHAR(MAX) NULL,
        NewValue  NVARCHAR(MAX) NULL,
        AdminUser NVARCHAR(128) NULL,
        CreatedAt DATETIME2     NOT NULL CONSTRAINT DF_RewardReferralRuleAudit_CreatedAt DEFAULT (SYSUTCDATETIME()));

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardReferralRuleAudit_Rule' AND object_id = OBJECT_ID(N'dbo.RewardReferralRuleAudit'))
        CREATE INDEX IX_RewardReferralRuleAudit_Rule ON dbo.RewardReferralRuleAudit (RuleId, Id DESC);

    /* ------------------------------------------------------------------------------------------------
       8. dbo.RewardPromoRedemption.Flow  (NULL = send; 'receive' = Receive Money bonus code)
       ------------------------------------------------------------------------------------------------ */
    IF COL_LENGTH(N'dbo.RewardPromoRedemption', N'Flow') IS NULL
        ALTER TABLE dbo.RewardPromoRedemption ADD Flow NVARCHAR(16) NULL;

    COMMIT TRANSACTION;
    PRINT N'UpgradeRewardEngineStagingSync: completed (idempotent).';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

/* ---------------------------------------------------------------------------------------------------
   Verification (read-only) - expect every row to show Present = 1
   --------------------------------------------------------------------------------------------------- */
SELECT N'dbo.RewardReferralVisit'                                AS [Object], CASE WHEN OBJECT_ID(N'dbo.RewardReferralVisit', N'U') IS NOT NULL THEN 1 ELSE 0 END AS Present
UNION ALL SELECT N'RewardCreditLedger.Floor',                    CASE WHEN COL_LENGTH(N'dbo.RewardCreditLedger', N'Floor') IS NOT NULL THEN 1 ELSE 0 END
UNION ALL SELECT N'RewardCreditLedger.ReceiveCurrency',          CASE WHEN COL_LENGTH(N'dbo.RewardCreditLedger', N'ReceiveCurrency') IS NOT NULL THEN 1 ELSE 0 END
UNION ALL SELECT N'RewardTransferFact.PaymentMethod',            CASE WHEN COL_LENGTH(N'dbo.RewardTransferFact', N'PaymentMethod') IS NOT NULL THEN 1 ELSE 0 END
UNION ALL SELECT N'UX_RewardPromoRedemption_Code_Transfer_Redeemed', CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RewardPromoRedemption_Code_Transfer_Redeemed' AND object_id = OBJECT_ID(N'dbo.RewardPromoRedemption')) THEN 1 ELSE 0 END
UNION ALL SELECT N'IX_RewardPromoRedemption_CodeStatusUser',     CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardPromoRedemption_CodeStatusUser' AND object_id = OBJECT_ID(N'dbo.RewardPromoRedemption')) THEN 1 ELSE 0 END
UNION ALL SELECT N'dbo.RewardReferralRuleAudit',                 CASE WHEN OBJECT_ID(N'dbo.RewardReferralRuleAudit', N'U') IS NOT NULL THEN 1 ELSE 0 END
UNION ALL SELECT N'RewardPromoRedemption.Flow',                  CASE WHEN COL_LENGTH(N'dbo.RewardPromoRedemption', N'Flow') IS NOT NULL THEN 1 ELSE 0 END;

/* ---------------------------------------------------------------------------------------------------
   Pre-check REPORT (read-only, NEVER fails, NEVER changes data)
   Send currencies with MORE THAN ONE non-archived referral rule. A rule counts as archived when
   Status = 'ARCHIVED' or ConfigJson.is_archived / ConfigJson.archived is 1 / true.
   If this returns rows, archive the extra rules in Mito Admin BEFORE the 409 one-rule-per-currency guard ships
   (otherwise editing either rule returns 409 DUPLICATE_CURRENCY until one is archived).
   Requires SQL Server 2017+ (STRING_AGG) and 2016+ (JSON_VALUE).
   --------------------------------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.RewardReferralRule', N'U') IS NOT NULL
BEGIN
    BEGIN TRY
        -- dynamic SQL so a server without STRING_AGG / JSON_VALUE only prints a note instead of failing the script
        EXEC (N'
        SELECT  r.BaseCurrency                                                         AS SendCurrency,
                COUNT(*)                                                               AS NonArchivedRules,
                STRING_AGG(CONCAT(CAST(r.Id AS NVARCHAR(64)), N'':'', r.Name), N'' | '') AS Rules
        FROM    dbo.RewardReferralRule r
        WHERE   ISNULL(r.Status, N'''') <> N''ARCHIVED''
          AND   ISNULL(CASE WHEN ISJSON(r.ConfigJson) = 1
                            THEN COALESCE(JSON_VALUE(r.ConfigJson, ''$.is_archived''), JSON_VALUE(r.ConfigJson, ''$.archived''))
                       END, N''0'') NOT IN (N''1'', N''true'', N''True'', N''TRUE'')
        GROUP BY r.BaseCurrency
        HAVING  COUNT(*) > 1;');
    END TRY
    BEGIN CATCH
        PRINT N'Duplicate-rule report skipped: ' + ERROR_MESSAGE() + N' (run the stand-alone query from DEVELOPER-BACKEND-STAGING-GUIDE.md section 8.2 manually).';
    END CATCH;
END;