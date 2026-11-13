using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Furp.Application.Interfaces.Repositories;
using Newtonsoft.Json.Linq;

namespace Furp.Application.Features.Rewards.Engine
{
    /// <summary>
    /// STAGING-SYNC drop-in (new file, no existing code is edited). Server-side guards for the
    /// <c>referral-rules</c> admin routes of <c>LocalRewardEngine</c>; wiring is in PATCHES-AND-WIRING.md section 8.
    ///
    ///   (a) one NON-ARCHIVED referral rule per send currency (+ unique name)           -> EnsureUniqueAsync
    ///   (b) country scope (send/receive include/exclude): STORED + ECHOED metadata only -> NormaliseCountryScope
    ///   (c) rule audit history  (GET referral-rules/{id}/audit, alias .../history)      -> Record*Async / AuditAsync
    ///
    /// Everything rule related lives in dbo.RewardReferralRule.ConfigJson (snake_case keys); the only new storage is
    /// dbo.RewardReferralRuleAudit (see UpgradeRewardEngineStagingSync.sql section 7).
    /// Country scope is NEVER enforced at offer / visit / award time (prototype parity, user decision 7.3).
    /// </summary>
    public sealed class RewardReferralRuleGuards
    {
        // Same text as the migration (object 6). Idempotent; run once per process as a safety net.
        private const string AuditDdlSql = @"
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
";

        /// <summary>Prototype RULE_FIELDS (referral.js L321-324): the 22 fields that are diffed into the audit trail.</summary>
        public static readonly string[] RuleFields =
        {
            "name", "is_enabled", "reward_type", "base_currency", "receive_currency",
            "send_country", "receive_country", "send_country_mode", "send_countries",
            "receive_country_mode", "receive_countries", "referrer_reward", "referee_reward",
            "min_transaction_threshold", "qualification_window_days", "bonus_validity_days",
            "max_referrals_cap_type", "max_referrals_per_referrer", "max_referrals_amount_per_referrer",
            "max_scheme_budget", "start_date", "end_date"
        };

        private static int _auditReady;

        // In-process serialisation per send currency (the DB applock below covers other nodes for the check itself).
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> CurrencyLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        private readonly IDapperRepository _dapper;

        public RewardReferralRuleGuards(IDapperRepository dapper)
        {
            _dapper = dapper;
        }

        // ------------------------------------------------------------------------------------------------
        // (a) one non-archived rule per send currency
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Serialises create / update of rules for one send currency inside this process. Usage:
        /// <code>using (await _ruleGuards.LockAsync(baseCurrency, ct)) { EnsureUniqueAsync(...); insert/update; audit }</code>
        /// </summary>
        public async Task<IDisposable> LockAsync(string baseCurrency, CancellationToken cancellationToken)
        {
            var key = (baseCurrency ?? string.Empty).Trim().ToUpperInvariant();
            var gate = CurrencyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            return new Releaser(gate);
        }

        /// <summary>
        /// Returns null when the (cleaned) rule may be saved, otherwise a ready-to-return error object
        /// <c>{ error, message, _httpStatus = 409 }</c> (DUPLICATE_CURRENCY first, then DUPLICATE_NAME).
        /// Never retroactive: only called on create and update. <paramref name="excludeId"/> = the rule being edited.
        /// The read happens under <c>sp_getapplock('reward-referral-rule-currency:' + BaseCurrency)</c> (Transaction owner, 10 s).
        /// </summary>
        public async Task<JObject> EnsureUniqueAsync(JObject clean, string excludeId, CancellationToken cancellationToken)
        {
            var baseCurrency = (Text(clean?["base_currency"] ?? clean?["baseCurrency"] ?? clean?["BaseCurrency"]) ?? string.Empty)
                .Trim().ToUpperInvariant();
            var name = (Text(clean?["name"] ?? clean?["Name"]) ?? string.Empty).Trim();

            IEnumerable<RuleLite> rows;
            try
            {
                rows = await _dapper.QueryAsync<RuleLite>(@"
SET XACT_ABORT ON;
BEGIN TRAN;
DECLARE @rc INT;
EXEC @rc = sp_getapplock @Resource = @lockKey, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 10000;
IF @rc < 0
BEGIN
    ROLLBACK TRAN;
    THROW 51010, N'RULE_LOCK_TIMEOUT', 1;
END
SELECT CAST(Id AS NVARCHAR(64)) AS Id, Name, Status, BaseCurrency, ReceiveCurrency, ConfigJson
FROM dbo.RewardReferralRule
WHERE ISNULL(Status, N'') <> N'ARCHIVED';
COMMIT TRAN;",
                    new { lockKey = "reward-referral-rule-currency:" + baseCurrency },
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex.Message != null && ex.Message.Contains("RULE_LOCK_TIMEOUT"))
            {
                return new JObject
                {
                    ["error"] = "BUSY",
                    ["message"] = "Referral rules are being updated. Please try again.",
                    ["_httpStatus"] = 409
                };
            }

            var live = (rows ?? Array.Empty<RuleLite>())
                .Where(r => !IsArchived(r.ConfigJson))
                .Where(r => excludeId == null || !string.Equals(r.Id, excludeId.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (baseCurrency.Length > 0)
            {
                var sameCurrency = live.FirstOrDefault(r =>
                    string.Equals((r.BaseCurrency ?? string.Empty).Trim(), baseCurrency, StringComparison.OrdinalIgnoreCase));
                if (sameCurrency != null)
                {
                    return new JObject
                    {
                        ["error"] = "DUPLICATE_CURRENCY",
                        ["message"] = "A referral rule for " + baseCurrency + " already exists ('" + sameCurrency.Name
                                      + "'). Only one rule per send currency is allowed \u2014 edit or archive it first.",
                        ["_httpStatus"] = 409
                    };
                }
            }

            if (name.Length > 0 && live.Any(r =>
                    string.Equals((r.Name ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                return new JObject
                {
                    ["error"] = "DUPLICATE_NAME",
                    ["message"] = "A rule with this name already exists.",
                    ["_httpStatus"] = 409
                };
            }

            return null;
        }

        /// <summary>Same IsArchived(cfg) logic as RewardReferralService (is_archived / archived = 1 | true).</summary>
        public static bool IsArchived(string configJson)
        {
            JObject cfg;
            try { cfg = string.IsNullOrWhiteSpace(configJson) ? new JObject() : JObject.Parse(configJson); }
            catch { cfg = new JObject(); }
            return IsArchived(cfg);
        }

        public static bool IsArchived(JObject cfg)
        {
            var token = cfg?["is_archived"] ?? cfg?["archived"];
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            var s = token.ToString();
            return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------------------------------------
        // (b) country scope: stored + echoed, never enforced
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Normalises the six country-scope keys IN PLACE on the cleaned rule object (the one that is written to ConfigJson):
        /// <c>send_country</c>, <c>receive_country</c> (legacy single, upper-case or null),
        /// <c>send_country_mode</c> / <c>receive_country_mode</c> (<c>ALL</c> | <c>INCLUDE</c> | <c>EXCLUDE</c>, default ALL),
        /// <c>send_countries</c> / <c>receive_countries</c> (array or CSV in, upper-case ISO-2 CSV string or null out).
        /// Keys that are absent from the request stay untouched on update (so a PATCH never wipes the scope); on create
        /// pass <paramref name="isNew"/> = true to always emit all six keys.
        /// </summary>
        public static JObject NormaliseCountryScope(JObject clean, bool isNew)
        {
            if (clean == null) return null;
            foreach (var key in new[] { "send_country", "receive_country" })
            {
                if (isNew || clean.ContainsKey(key)) clean[key] = SingleOrNull(clean[key]);
            }

            foreach (var key in new[] { "send_country_mode", "receive_country_mode" })
            {
                if (isNew || clean.ContainsKey(key))
                {
                    var mode = (Text(clean[key]) ?? string.Empty).Trim().ToUpperInvariant();
                    clean[key] = mode == "INCLUDE" || mode == "EXCLUDE" ? mode : "ALL";
                }
            }

            foreach (var key in new[] { "send_countries", "receive_countries" })
            {
                if (isNew || clean.ContainsKey(key)) clean[key] = CsvOrNull(clean[key]);
            }

            return clean;
        }

        private static JToken SingleOrNull(JToken token)
        {
            var s = (Text(token) ?? string.Empty).Trim().ToUpperInvariant();
            return s.Length == 0 ? JValue.CreateNull() : new JValue(s);
        }

        private static JToken CsvOrNull(JToken token)
        {
            IEnumerable<string> parts;
            if (token is JArray arr)
            {
                parts = arr.Select(x => (Text(x) ?? string.Empty).Trim().ToUpperInvariant());
            }
            else
            {
                var s = Text(token) ?? string.Empty;
                // The prototype stores a CSV string verbatim (trimmed, upper-cased); splitting also tidies stray spaces.
                parts = s.Split(',').Select(x => x.Trim().ToUpperInvariant());
            }

            var csv = string.Join(",", parts.Where(x => x.Length > 0));
            return csv.Length == 0 ? JValue.CreateNull() : new JValue(csv);
        }

        // ------------------------------------------------------------------------------------------------
        // (c) audit history
        // ------------------------------------------------------------------------------------------------

        /// <summary>Admin display name: authenticated FURP admin name, then body.admin_user, then "Admin".</summary>
        public static string ResolveAdminUser(string furpAdminName, JObject body)
        {
            var name = !string.IsNullOrWhiteSpace(furpAdminName)
                ? furpAdminName
                : Text(body?["admin_user"] ?? body?["adminUser"]);
            name = string.IsNullOrWhiteSpace(name) ? "Admin" : name.Trim();
            return name.Length > 128 ? name.Substring(0, 128) : name;
        }

        /// <summary>
        /// Flattens a stored rule (columns + ConfigJson) into the prototype's RULE_FIELDS shape so before / after
        /// snapshots can be diffed. <c>is_enabled</c> = 1 when Status = ACTIVE. Config keys win for everything else.
        /// </summary>
        public static JObject RuleSnapshot(string name, string status, string baseCurrency, string receiveCurrency, string configJson)
        {
            JObject cfg;
            try { cfg = string.IsNullOrWhiteSpace(configJson) ? new JObject() : JObject.Parse(configJson); }
            catch { cfg = new JObject(); }
            var snap = new JObject();
            foreach (var f in RuleFields) snap[f] = cfg[f];
            snap["name"] = name;
            snap["is_enabled"] = string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            snap["base_currency"] = baseCurrency;
            if (!string.IsNullOrWhiteSpace(receiveCurrency)) snap["receive_currency"] = receiveCurrency;
            return snap;
        }

        public async Task RecordCreatedAsync(string ruleId, string ruleName, string adminUser, CancellationToken cancellationToken)
        {
            await InsertAuditAsync(ruleId, "created", null, ruleName, adminUser, cancellationToken);
        }

        /// <summary>
        /// One row per changed field of <see cref="RuleFields"/>, comparing String(old ?? '') != String(new ?? '') exactly like
        /// the prototype. Returns the number of rows written.
        /// </summary>
        public async Task<int> RecordUpdateAsync(string ruleId, JObject before, JObject after, string adminUser, CancellationToken cancellationToken)
        {
            var written = 0;
            foreach (var field in RuleFields)
            {
                var oldValue = Stringify(field, before?[field]);
                var newValue = Stringify(field, after?[field]);
                if (!string.Equals(oldValue ?? string.Empty, newValue ?? string.Empty, StringComparison.Ordinal))
                {
                    await InsertAuditAsync(ruleId, field, oldValue, newValue, adminUser, cancellationToken);
                    written++;
                }
            }

            return written;
        }

        /// <summary>archived: old '0' -> new '1'. Only when the rule was not already archived.</summary>
        public async Task RecordArchivedAsync(string ruleId, bool wasAlreadyArchived, string adminUser, CancellationToken cancellationToken)
        {
            if (wasAlreadyArchived) return;
            await InsertAuditAsync(ruleId, "archived", "0", "1", adminUser, cancellationToken);
        }

        /// <summary>
        /// Response for GET referral-rules/{id}/audit (alias /history):
        /// <c>{ data: [ { id, rule_id, field, old_value, new_value, admin_user, created_at } ] }</c>, newest first.
        /// </summary>
        public async Task<JObject> AuditAsync(string ruleId, CancellationToken cancellationToken)
        {
            await EnsureAuditTableAsync(cancellationToken);
            var rows = await _dapper.QueryAsync<AuditRow>(@"
SELECT Id, RuleId, Field, OldValue, NewValue, AdminUser, CreatedAt
FROM dbo.RewardReferralRuleAudit
WHERE RuleId = @ruleId
ORDER BY Id DESC;",
                new { ruleId = (ruleId ?? string.Empty).Trim() },
                cancellationToken: cancellationToken);

            var data = new JArray();
            foreach (var r in rows ?? Array.Empty<AuditRow>())
            {
                data.Add(new JObject
                {
                    ["id"] = r.Id,
                    ["rule_id"] = r.RuleId,
                    ["field"] = r.Field,
                    ["old_value"] = r.OldValue,
                    ["new_value"] = r.NewValue,
                    ["admin_user"] = r.AdminUser,
                    ["created_at"] = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)
                        .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
                });
            }

            return new JObject { ["data"] = data };
        }

        private async Task InsertAuditAsync(
            string ruleId, string field, string oldValue, string newValue, string adminUser, CancellationToken cancellationToken)
        {
            await EnsureAuditTableAsync(cancellationToken);
            await _dapper.ExecuteAsync(@"
INSERT INTO dbo.RewardReferralRuleAudit (RuleId, Field, OldValue, NewValue, AdminUser)
VALUES (@ruleId, @field, @oldValue, @newValue, @adminUser);",
                new
                {
                    ruleId = (ruleId ?? string.Empty).Trim(),
                    field,
                    oldValue,
                    newValue,
                    adminUser = string.IsNullOrWhiteSpace(adminUser) ? "Admin" : adminUser
                },
                cancellationToken: cancellationToken);
        }

        private async Task EnsureAuditTableAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _auditReady, 1, 1) == 1) return;
            try
            {
                await _dapper.ExecuteAsync(AuditDdlSql, cancellationToken: cancellationToken);
            }
            catch
            {
                await _dapper.ExecuteAsync(AuditDdlSql, cancellationToken: cancellationToken);
            }

            Interlocked.Exchange(ref _auditReady, 1);
        }

        // JS String(x) parity: null -> null (stored as NULL), booleans 1/0 for is_enabled, numbers without trailing zeros.
        private static string Stringify(string field, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return null;
            switch (token.Type)
            {
                case JTokenType.Boolean:
                    return token.Value<bool>() ? (field == "is_enabled" ? "1" : "true") : (field == "is_enabled" ? "0" : "false");
                case JTokenType.Integer:
                    return token.Value<long>().ToString(CultureInfo.InvariantCulture);
                case JTokenType.Float:
                    return token.Value<double>().ToString("R", CultureInfo.InvariantCulture);
                default:
                    return token.ToString();
            }
        }

        private static string Text(JToken token) =>
            token == null || token.Type == JTokenType.Null ? null : token.ToString();

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim _gate;

            public Releaser(SemaphoreSlim gate) => _gate = gate;

            public void Dispose()
            {
                var gate = Interlocked.Exchange(ref _gate, null);
                gate?.Release();
            }
        }

        private sealed class RuleLite
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Status { get; set; }
            public string BaseCurrency { get; set; }
            public string ReceiveCurrency { get; set; }
            public string ConfigJson { get; set; }
        }

        private sealed class AuditRow
        {
            public long Id { get; set; }
            public string RuleId { get; set; }
            public string Field { get; set; }
            public string OldValue { get; set; }
            public string NewValue { get; set; }
            public string AdminUser { get; set; }
            public DateTime CreatedAt { get; set; }
        }
    }

    /// <summary>
    /// STAGING-SYNC (d): stable "active first" ordering. Changes the ORDER of a list only, never its content.
    /// Each list keeps its existing SQL order as the secondary order because LINQ OrderBy is stable:
    /// load the rows exactly as today, then call the matching method.
    /// </summary>
    public static class RewardSort
    {
        /// <summary>Referral rules + referral performance (referral.js L1198 / L1241): ACTIVE first.</summary>
        public static int RuleRank(string status)
        {
            switch ((status ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "ACTIVE": return 0;
                case "SCHEDULED": return 1;
                case "INACTIVE": return 2;
                case "ENDED": return 3;
                case "ARCHIVED": return 4;
                default: return 5;
            }
        }

        /// <summary>Promo codes (promo/admin.js L52): Active, Scheduled, Disabled, Fully redeemed, Budget spent, Expired.</summary>
        public static int PromoRank(string displayStatus)
        {
            switch ((displayStatus ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "active": return 0;
                case "scheduled": return 1;
                case "disabled": return 2;
                case "fully redeemed": return 3;
                case "budget spent": return 4;
                case "expired": return 5;
                default: return 6;
            }
        }

        /// <summary>Bonus schemes (bonus/schemes.js L194): Active, Scheduled, Inactive, Ended, Archived.</summary>
        public static int SchemeRank(string displayStatus)
        {
            switch ((displayStatus ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "active": return 0;
                case "scheduled": return 1;
                case "inactive": return 2;
                case "ended": return 3;
                case "archived": return 4;
                default: return 5;
            }
        }

        /// <summary>Generic stable sort for typed rows.</summary>
        public static List<T> By<T>(IEnumerable<T> rows, Func<T, string> status, Func<string, int> rank) =>
            (rows ?? Enumerable.Empty<T>()).OrderBy(r => rank(status(r))).ToList();

        /// <summary>JObject rows, status key <c>status</c> (referral rules and performance).</summary>
        public static List<JObject> Rules(IEnumerable<JObject> rows) =>
            By(rows, r => Status(r, "status"), RuleRank);

        /// <summary>JObject rows, status key <c>display_status</c> / <c>displayStatus</c> (promo codes).</summary>
        public static List<JObject> Promos(IEnumerable<JObject> rows) =>
            By(rows, r => Status(r, "display_status", "displayStatus"), PromoRank);

        /// <summary>JObject rows, status key <c>display_status</c> / <c>displayStatus</c> (bonus schemes).</summary>
        public static List<JObject> Schemes(IEnumerable<JObject> rows) =>
            By(rows, r => Status(r, "display_status", "displayStatus"), SchemeRank);

        private static string Status(JObject row, params string[] keys)
        {
            foreach (var key in keys)
            {
                var token = row?[key];
                if (token != null && token.Type != JTokenType.Null) return token.ToString();
            }

            return null;
        }
    }
}
