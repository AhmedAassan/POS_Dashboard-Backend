// Modules/System/Services/BusinessSettingsService.cs
//
// Tiny read helper over dbo.BusinessSetting. Static + connection-passed so it
// can run inside an open UnitOfWork (same pattern as InvoiceNumberService).
//
// Resolution order for a key: branch row first, then the global (BranchId NULL)
// row, then the caller's fallback. That means a branch can opt out of delivery
// without touching the other branches.

using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using static PosDashboard.Web.Modules.System.Models.DeliveryDtos;

namespace PosDashboard.Web.Modules.System.Services
{
    public static class BusinessSettingsService
    {
        public const string KeyDeliveryEnabled = "delivery.enabled";
        public const string KeyDeliveryDateEnabled = "delivery.dateEnabled";
        public const string KeyDeliveryDateDefaultOn = "delivery.dateDefaultOn";
        public const string KeyDeliveryDefaultLeadDays = "delivery.defaultLeadDays";

        /// <summary>Raw value for one key (branch override wins over the global row).</summary>
        public static string? GetValue(IDbConnection conn, string key, int? branchId = null)
        {
            return SqlMapper.Query<string>(conn, @"
                SELECT TOP 1 SettingValue
                FROM dbo.BusinessSetting
                WHERE SettingKey = @Key
                  AND (BranchId = @BranchId OR BranchId IS NULL)
                ORDER BY CASE WHEN BranchId IS NULL THEN 1 ELSE 0 END",
                new { Key = key, BranchId = branchId }).FirstOrDefault();
        }

        public static bool GetBool(IDbConnection conn, string key, bool fallback, int? branchId = null)
        {
            var raw = GetValue(conn, key, branchId);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            raw = raw.Trim();
            if (bool.TryParse(raw, out var b)) return b;
            return raw == "1" || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
                             || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        public static int GetInt(IDbConnection conn, string key, int fallback, int? branchId = null)
        {
            var raw = GetValue(conn, key, branchId);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n : fallback;
        }

        public static decimal GetDecimal(IDbConnection conn, string key, decimal fallback, int? branchId = null)
        {
            var raw = GetValue(conn, key, branchId);
            return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                ? d : fallback;
        }

        /// <summary>The four delivery flags in one shot (used by the POS catalog + delivery context).</summary>
        public static DeliverySettingsDto GetDeliverySettings(IDbConnection conn, int? branchId = null)
        {
            var rows = SqlMapper.Query<(string Key, string? Value, int? BranchId)>(conn, @"
                SELECT SettingKey AS [Key], SettingValue AS [Value], BranchId
                FROM dbo.BusinessSetting
                WHERE Category = 'Delivery'
                  AND (BranchId = @BranchId OR BranchId IS NULL)",
                new { BranchId = branchId }).ToList();

            // Branch row wins; fall back to the global one.
            string? Pick(string key) =>
                rows.Where(r => r.Key == key)
                    .OrderBy(r => r.BranchId == null ? 1 : 0)
                    .Select(r => r.Value)
                    .FirstOrDefault();

            static bool AsBool(string? v, bool fallback)
            {
                if (string.IsNullOrWhiteSpace(v)) return fallback;
                v = v.Trim();
                if (bool.TryParse(v, out var b)) return b;
                return v == "1";
            }

            static int AsInt(string? v, int fallback) =>
                int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

            return new DeliverySettingsDto(
                Enabled: AsBool(Pick(KeyDeliveryEnabled), false),
                DateEnabled: AsBool(Pick(KeyDeliveryDateEnabled), true),
                DateDefaultOn: AsBool(Pick(KeyDeliveryDateDefaultOn), false),
                DefaultLeadDays: Math.Max(0, AsInt(Pick(KeyDeliveryDefaultLeadDays), 2)));
        }

        /// <summary>Branch timezone offset (hours) from SYSTEM_SETTING — delivery dates are branch-local.</summary>
        public static int GetTimeZoneOffset(IDbConnection conn)
        {
            return SqlMapper.Query<string>(conn,
                    "SELECT SETTING_VALUE FROM dbo.SYSTEM_SETTING WHERE SETTING_KEY = 'timeZoneOffset'")
                .Select(v => int.TryParse(v, out var n) ? n : 3)
                .DefaultIfEmpty(3).First();
        }
    }
}
