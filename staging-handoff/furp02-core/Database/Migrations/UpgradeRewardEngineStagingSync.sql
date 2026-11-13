/* =====================================================================================================
   UpgradeRewardEngineStagingSync.sql
   furp02-core  ::  Database/Migrations/UpgradeRewardEngineStagingSync.sql

   Purpose : Bring the FURP `Reward$` engine schema in line with the Mito Admin referral / bonus / promo
             prototype (PromoCode repo) so the Staging -> pri_prod promotion needs zero manual SQL.
   Safety  : 100% ADDITIVE and IDEMPOTENT. Re-running is a no-op. No column is dropped, renamed or re-typed,
             no row is deleted or rewritten. Safe to run with the application online.
   Target  : SQL Server (dbo schema). Run AFTER AddRewardEngineLocalSchema.sql (the 13 dbo.Reward* tables
             must already exist; RewardSchema.cs creates them at runtime on first use).

   Adds
     1. dbo.RewardReferralVisit                      (link visit / click / install-referrer tracking)
     2. dbo.RewardCreditLedger.Floor                 DECIMAL(18,2) NULL   (minimum transfer for a welcome credit)
     3. dbo.RewardCreditLedger.ReceiveCurrency       CHAR(3) NULL         (corridor lock for a credit)
     4. dbo.RewardTransferFact.PaymentMethod         NVARCHAR(50) NULL    (bonus scheme paymentMethods eligibility)
     5. UX_RewardPromoRedemption_Code_Transfer_Redeemed   (no duplicate REDEEMED row per promo + transfer)
     6. IX_RewardPromoRedemption_CodeStatusUser           (promo cap checks + customer PromoSavings)

   NOTE: RewardSchema.cs must carry the same DDL (see PATCHES-AND-WIRING.md section 1) so that fresh
         environments bootstrapped at runtime end up identical to migrated ones.
   ===================================================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON; -- required for the filtered indexes below (sqlcmd defaults to OFF)

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

    COMMIT TRANSACTION;
    PRINT N'UpgradeRewardEngineStagingSync: completed (idempotent).';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @msg NVARCHAR(2048) = ERROR_MESSAGE();
    RAISERROR(N'UpgradeRewardEngineStagingSync FAILED and was rolled back: %s', 16, 1, @msg);
END CATCH;

/* ---------------------------------------------------------------------------------------------------
   Verification (read-only) - expect every row to show Present = 1
   --------------------------------------------------------------------------------------------------- */
SELECT N'dbo.RewardReferralVisit'                                AS [Object], CASE WHEN OBJECT_ID(N'dbo.RewardReferralVisit', N'U') IS NOT NULL THEN 1 ELSE 0 END AS Present
UNION ALL SELECT N'RewardCreditLedger.Floor',                    CASE WHEN COL_LENGTH(N'dbo.RewardCreditLedger', N'Floor') IS NOT NULL THEN 1 ELSE 0 END
UNION ALL SELECT N'RewardCreditLedger.ReceiveCurrency',          CASE WHEN COL_LENGTH(N'dbo.RewardCreditLedger', N'ReceiveCurrency') IS NOT NULL THEN 1 ELSE 0 END
UNION ALL SELECT N'RewardTransferFact.PaymentMethod',            CASE WHEN COL_LENGTH(N'dbo.RewardTransferFact', N'PaymentMethod') IS NOT NULL THEN 1 ELSE 0 END
UNION ALL SELECT N'UX_RewardPromoRedemption_Code_Transfer_Redeemed', CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RewardPromoRedemption_Code_Transfer_Redeemed' AND object_id = OBJECT_ID(N'dbo.RewardPromoRedemption')) THEN 1 ELSE 0 END
UNION ALL SELECT N'IX_RewardPromoRedemption_CodeStatusUser',     CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RewardPromoRedemption_CodeStatusUser' AND object_id = OBJECT_ID(N'dbo.RewardPromoRedemption')) THEN 1 ELSE 0 END;
