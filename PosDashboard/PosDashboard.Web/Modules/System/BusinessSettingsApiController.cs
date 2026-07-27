// Modules/System/BusinessSettingsApiController.cs
//
// CRUD over dbo.BusinessSetting for the /settings page.
//
// The table is intentionally generic: adding a system flag later is an INSERT
// into BusinessSetting, and this controller + the settings UI pick it up with
// no code change (the ValueType drives which editor renders).

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serenity.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using PosDashboard.Web.Modules.System.Services;
using static PosDashboard.Web.Modules.System.Models.DeliveryDtos;
using System.Data;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/business-settings")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class BusinessSettingsApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;

        public BusinessSettingsApiController(ISqlConnections sqlConnections)
        {
            this.sqlConnections = sqlConnections;
        }

        private int? CurrentUserId()
        {
            var claim = User.Claims.FirstOrDefault(c =>
                c.Type == "userId" || c.Type == "sub" || c.Type == ClaimTypes.NameIdentifier);
            return claim != null && int.TryParse(claim.Value, out var id) ? id : (int?)null;
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /api/business-settings?branchId=1
        // Flat list (global rows + this branch's overrides).
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("")]
        public ActionResult<ApiResult<List<BusinessSettingDto>>> GetAll([FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var list = LoadSettings(conn, branchId, null);
            return Ok(new ApiResult<List<BusinessSettingDto>>(true, null, list));
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /api/business-settings/grouped?branchId=1
        // Same rows, bucketed by Category — what the settings page renders.
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("grouped")]
        public ActionResult<ApiResult<List<BusinessSettingGroupDto>>> GetGrouped([FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var list = LoadSettings(conn, branchId, null);

            var groups = list
                .GroupBy(s => s.Category)
                .OrderBy(g => g.Min(s => s.Ordering))
                .Select(g => new BusinessSettingGroupDto(
                    g.Key,
                    g.OrderBy(s => s.Ordering).ThenBy(s => s.SettingKey).ToList()))
                .ToList();

            return Ok(new ApiResult<List<BusinessSettingGroupDto>>(true, null, groups));
        }

        // ─────────────────────────────────────────────────────────────────
        // GET /api/business-settings/key/{key}?branchId=1
        // ─────────────────────────────────────────────────────────────────
        [HttpGet("key/{key}")]
        public ActionResult<ApiResult<BusinessSettingDto>> GetByKey(string key, [FromQuery] int? branchId = null)
        {
            using var conn = sqlConnections.NewByKey("Default");
            var found = LoadSettings(conn, branchId, key).FirstOrDefault();
            if (found == null)
                return Ok(new ApiResult<BusinessSettingDto>(false, $"Setting '{key}' not found", null));
            return Ok(new ApiResult<BusinessSettingDto>(true, null, found));
        }

        // ─────────────────────────────────────────────────────────────────
        // POST /api/business-settings/update
        // Updates one key. Creates the branch override row when BranchId is
        // supplied and only the global row exists.
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("update")]
        public ActionResult<ApiResult<BusinessSettingDto>> Update([FromBody] UpdateBusinessSettingRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.SettingKey))
                return Ok(new ApiResult<BusinessSettingDto>(false, "SettingKey is required", null));

            using var conn = sqlConnections.NewByKey("Default");

            var error = UpsertOne(conn, request);
            if (error != null)
                return Ok(new ApiResult<BusinessSettingDto>(false, error, null));

            var dto = LoadSettings(conn, request.BranchId, request.SettingKey).FirstOrDefault();
            return Ok(new ApiResult<BusinessSettingDto>(true, null, dto));
        }

        // ─────────────────────────────────────────────────────────────────
        // POST /api/business-settings/update-many
        // Save-all from the settings dialog. All-or-nothing.
        // ─────────────────────────────────────────────────────────────────
        [HttpPost("update-many")]
        public ActionResult<ApiResult<List<BusinessSettingDto>>> UpdateMany(
            [FromBody] UpdateBusinessSettingsRequest request)
        {
            if (request?.Settings == null || request.Settings.Count == 0)
                return Ok(new ApiResult<List<BusinessSettingDto>>(false, "No settings supplied", null));

            using var conn = sqlConnections.NewByKey("Default");
            if (conn.State != ConnectionState.Open) conn.Open();

            try
            {
                using var uow = new UnitOfWork(conn);
                foreach (var s in request.Settings)
                {
                    var error = UpsertOne(uow.Connection, s);
                    if (error != null)
                        return Ok(new ApiResult<List<BusinessSettingDto>>(false, error, null));
                }
                uow.Commit();
            }
            catch (Exception ex)
            {
                return Ok(new ApiResult<List<BusinessSettingDto>>(
                    false, $"Failed to save settings: {ex.Message}", null));
            }

            var branchId = request.Settings.Select(s => s.BranchId).FirstOrDefault();
            var list = LoadSettings(conn, branchId, null);
            return Ok(new ApiResult<List<BusinessSettingDto>>(true, null, list));
        }

        #region Helpers

        /// <summary>Writes one setting. Returns an error string, or null on success.</summary>
        private string? UpsertOne(IDbConnection conn, UpdateBusinessSettingRequest req)
        {
            // The template row (global) defines the type/labels — a branch override
            // clones them rather than inventing a new key.
            var template = SqlMapper.Query(conn, @"
                SELECT TOP 1 SettingKey, ValueType, Category, DisplayNameEn, DisplayNameAr,
                       DescriptionEn, DescriptionAr, IsEditable, Ordering
                FROM dbo.BusinessSetting
                WHERE SettingKey = @Key
                ORDER BY CASE WHEN BranchId IS NULL THEN 0 ELSE 1 END",
                new { Key = req.SettingKey }).FirstOrDefault();

            if (template == null)
                return $"Setting '{req.SettingKey}' does not exist";

            if (!(bool)template.IsEditable)
                return $"Setting '{req.SettingKey}' is read-only";

            var typeError = ValidateValue((string)template.ValueType, req.SettingValue);
            if (typeError != null)
                return $"'{req.SettingKey}': {typeError}";

            int affected = SqlMapper.Execute(conn, @"
                UPDATE dbo.BusinessSetting
                SET SettingValue = @Value,
                    UpdatedAt    = SYSUTCDATETIME(),
                    UpdatedBy    = @UserId
                WHERE SettingKey = @Key
                  AND ((@BranchId IS NULL AND BranchId IS NULL) OR BranchId = @BranchId)",
                new
                {
                    Key = req.SettingKey,
                    Value = req.SettingValue,
                    BranchId = req.BranchId,
                    UserId = CurrentUserId()
                });

            if (affected > 0) return null;

            // No row for this scope yet → create the branch override from the template.
            SqlMapper.Execute(conn, @"
                INSERT INTO dbo.BusinessSetting (
                    SettingKey, SettingValue, ValueType, Category,
                    DisplayNameEn, DisplayNameAr, DescriptionEn, DescriptionAr,
                    BranchId, IsEditable, Ordering, CreatedAt, UpdatedAt, UpdatedBy
                )
                VALUES (
                    @Key, @Value, @ValueType, @Category,
                    @DisplayNameEn, @DisplayNameAr, @DescriptionEn, @DescriptionAr,
                    @BranchId, 1, @Ordering, SYSUTCDATETIME(), SYSUTCDATETIME(), @UserId
                )",
                new
                {
                    Key = req.SettingKey,
                    Value = req.SettingValue,
                    ValueType = (string)template.ValueType,
                    Category = (string)template.Category,
                    DisplayNameEn = (string)template.DisplayNameEn,
                    DisplayNameAr = (string)template.DisplayNameAr,
                    DescriptionEn = (string?)template.DescriptionEn,
                    DescriptionAr = (string?)template.DescriptionAr,
                    BranchId = req.BranchId,
                    Ordering = (int)template.Ordering,
                    UserId = CurrentUserId()
                });

            return null;
        }

        private static string? ValidateValue(string valueType, string? value)
        {
            if (value == null) return null;
            switch (valueType?.ToLowerInvariant())
            {
                case "bool":
                    return bool.TryParse(value, out _) || value == "0" || value == "1"
                        ? null : "expected true/false";
                case "int":
                    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                        ? null : "expected a whole number";
                case "decimal":
                    return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)
                        ? null : "expected a number";
                default:
                    return null; // string / json — free-form
            }
        }

        /// <summary>Global rows plus branch overrides, with the override shadowing its global twin.</summary>
        private static List<BusinessSettingDto> LoadSettings(
            IDbConnection conn, int? branchId, string? key)
        {
            var rows = SqlMapper.Query<BusinessSettingDto>(conn, @"
                SELECT
                    Id, SettingKey, SettingValue, ValueType, Category,
                    DisplayNameEn, DisplayNameAr, DescriptionEn, DescriptionAr,
                    BranchId, IsEditable, Ordering, UpdatedAt
                FROM dbo.BusinessSetting
                WHERE (@Key IS NULL OR SettingKey = @Key)
                  AND (BranchId IS NULL OR BranchId = @BranchId)
                ORDER BY Ordering, SettingKey",
                new { BranchId = branchId, Key = key }).ToList();

            return rows
                .GroupBy(r => r.SettingKey)
                .Select(g => g.OrderBy(r => r.BranchId == null ? 1 : 0).First())
                .OrderBy(r => r.Ordering).ThenBy(r => r.SettingKey)
                .ToList();
        }

        #endregion
    }
}
