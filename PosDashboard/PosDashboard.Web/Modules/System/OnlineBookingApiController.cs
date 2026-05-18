// Modules/Online/Controllers/OnlineBookingApiController.cs
//
// Single controller, divided into regions:
//   [AUTH]         — OTP, Register, Login, Refresh
//   [CONFIG]       — Config data, Areas
//   [AVAILABILITY] — Staff, Services, Time Slots
//   [BOOKINGS]     — Create, List, Detail, Cancel
//   [PROFILE]      — Get, Update, Change Password
//   [ADDRESSES]    — CRUD

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Serenity.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static PosDashboard.Web.Modules.System.Models.OnlineBookingDtos;

namespace PosDashboard.Web.Modules.System
{
    [ApiController]
    [Route("api/online")]
    [IgnoreAntiforgeryToken]
    public class OnlineBookingApiController : ControllerBase
    {
        private readonly ISqlConnections sqlConnections;
        private readonly IConfiguration configuration;
        private readonly IHttpClientFactory httpClientFactory;

        private const string EnjazatikUrl =
            "https://business.enjazatik.com/api/v1/send-message";

        public OnlineBookingApiController(
            ISqlConnections sqlConnections,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory)
        {
            this.sqlConnections = sqlConnections;
            this.configuration = configuration;
            this.httpClientFactory = httpClientFactory;
        }

        // ──────────────────────────────────────────────────────────────
        // HELPER — get CustomerId from JWT claim (sub)
        // ──────────────────────────────────────────────────────────────
        private int GetCurrentCustomerId()
        {
            var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(claim, out var id) ? id : 0;
        }

        // ──────────────────────────────────────────────────────────────
        // HELPER — parse "HH:mm" → TimeSpan
        // ──────────────────────────────────────────────────────────────
        private static bool TryParseTime(string? t, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(t)) return false;
            return TimeSpan.TryParseExact(t.Trim(), @"hh\:mm", null, out result);
        }

        // ──────────────────────────────────────────────────────────────
        // HELPER — normalize Kuwaiti/Gulf phone numbers
        // ──────────────────────────────────────────────────────────────
        private static string NormalizePhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return "";
            var cleaned = new string(phone.Where(char.IsDigit).ToArray());
            if (cleaned.StartsWith("0")) cleaned = "965" + cleaned.Substring(1);
            if (cleaned.Length == 8) cleaned = "965" + cleaned;
            return cleaned;
        }

        // ──────────────────────────────────────────────────────────────
        // HELPER — BCrypt-style SHA256 hash (no external lib required)
        //          Replace with BCrypt.Net if already referenced
        // ──────────────────────────────────────────────────────────────
        private static string HashPassword(string password)
        {
            var salt = Guid.NewGuid().ToString("N")[..8];
            using var sha = SHA256.Create();
            var hash = Convert.ToBase64String(
                sha.ComputeHash(Encoding.UTF8.GetBytes(salt + password)));
            return $"{salt}:{hash}";
        }

        private static bool VerifyPassword(string password, string stored)
        {
            var parts = stored.Split(':');
            if (parts.Length != 2) return false;
            using var sha = SHA256.Create();
            var hash = Convert.ToBase64String(
                sha.ComputeHash(Encoding.UTF8.GetBytes(parts[0] + password)));
            return hash == parts[1];
        }

        // ──────────────────────────────────────────────────────────────
        // HELPER — issue JWT (same settings as the dashboard JWT)
        // ──────────────────────────────────────────────────────────────
        private (string Token, string RefreshToken, string Expiry) IssueToken(
            int customerId, string customerRef, int branchId)
        {
            var jwtKey = configuration["Jwt:Key"]
                      ?? configuration["JwtSettings:Secret"]
                      ?? throw new InvalidOperationException("JWT key not configured");

            var issuer = configuration["Jwt:Issuer"] ?? "PosDashboard";
            var audience = configuration["Jwt:Audience"] ?? "PosDashboard";

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var expiry = DateTime.UtcNow.AddDays(30);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, customerId.ToString()),
                new Claim("ref",    customerRef),
                new Claim("branch", branchId.ToString()),
                new Claim(ClaimTypes.Role, "OnlineCustomer")
            };

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                expires: expiry,
                signingCredentials: creds);

            var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
            var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
            var expiryString = expiry.ToString("o");

            return (tokenString, refreshToken, expiryString);
        }

        // ──────────────────────────────────────────────────────────────
        // HELPER — check recurring block applies to date
        // (Same logic as StaffTimeBlocksApiController)
        // ──────────────────────────────────────────────────────────────
        private static bool RecurrenceAppliesToDate(string rule, DateTime date)
        {
            var dow = date.DayOfWeek;
            if (rule == "DAILY") return true;
            if (rule == "WEEKDAYS") return dow >= DayOfWeek.Monday && dow <= DayOfWeek.Friday;
            if (rule == "WEEKENDS") return dow == DayOfWeek.Saturday || dow == DayOfWeek.Sunday;
            if (rule.StartsWith("WEEKLY:"))
            {
                var dayMap = new Dictionary<string, DayOfWeek>
                {
                    {"MON",DayOfWeek.Monday},{"TUE",DayOfWeek.Tuesday},
                    {"WED",DayOfWeek.Wednesday},{"THU",DayOfWeek.Thursday},
                    {"FRI",DayOfWeek.Friday},{"SAT",DayOfWeek.Saturday},
                    {"SUN",DayOfWeek.Sunday}
                };
                return rule.Substring(7).Split(',')
                    .Select(d => d.Trim().ToUpper())
                    .Any(d => dayMap.TryGetValue(d, out var mapped) && mapped == dow);
            }
            return false;
        }

        // ══════════════════════════════════════════════════════════════
        // REGION: AUTH
        // ══════════════════════════════════════════════════════════════

        // ──────────────────────────────────────────────────────────────
        // POST /api/online/auth/request-otp
        // Step 1 of register flow — send 6-digit OTP via WhatsApp
        // ──────────────────────────────────────────────────────────────
        [HttpPost("auth/request-otp")]
        [AllowAnonymous]
        public async Task<ActionResult<ApiResultBilingual<object>>> RequestOtp(
        [FromBody] RequestOtpDto? request,
        [FromQuery] string? mobileNo = null)   
        {
            // Use body first, then query fallback
            var mobile = request?.MobileNo ?? mobileNo;

            if (string.IsNullOrWhiteSpace(mobile))
                return Ok(new ApiResultBilingual<object>(
                    false, "MobileNo is required", null,
                    "رقم الهاتف مطلوب", "MobileNo is required"));

            

            using var conn = sqlConnections.NewByKey("Default");

            // Check if mobile already exists as a dashboard (non-external) customer
            var existing = conn.Query<dynamic>(
                @"SELECT CUSTOMER_ID, IsExternalUser
                  FROM dbo.CUSTOMER
                  WHERE CUSTOMER_PHONE1 = @Mobile",
                new { Mobile = mobile }).FirstOrDefault();

            if (existing != null && Convert.ToInt32(existing.IsExternalUser) == 0)
                return Ok(new ApiResultBilingual<object>(
                    false, null, null,
                    "هذا الرقم مسجل في النظام بالفعل",
                    "This number is already registered in the system"));

            // Generate 6-digit OTP
            var otp = new Random().Next(100000, 999999).ToString();
            var expiry = DateTime.UtcNow.AddMinutes(5);

            if (existing != null)
            {
                // Update existing external customer OTP
                conn.Execute(
                    "UPDATE dbo.CUSTOMER SET OtpCode = @Otp, OtpExpiry = @Expiry WHERE CUSTOMER_PHONE1 = @Mobile",
                    new { Otp = otp, Expiry = expiry, Mobile = mobile });
            }
            else
            {
                // Store OTP in a temporary marker — we use a placeholder CUSTOMER row
                // flagged deleted=pending so we don't pollute the main customer list.
                // Alternatively: use MemoryCache. Here we use a small temp table approach
                // by storing in-memory in a static ConcurrentDictionary (simple, no extra table).
                // For production, store in SYSTEM_SETTING or a real temp table.
                OtpCache.Set(mobile, (otp, expiry));
            }

            // Send via WhatsApp (Enjazatik — same as WhatsAppApiController)
            var waConfig = conn.Query<dynamic>(
                "SELECT TOP 1 InstanceId, IsEnabled FROM dbo.WHATSAPP_CONFIG ORDER BY Id")
                .FirstOrDefault();

            if (waConfig != null && (bool)waConfig.IsEnabled)
            {
                try
                {
                    var client = httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer",
                            configuration["WhatsApp:ApiKey"] ?? "");

                    var message = $"🔐 رمز التحقق الخاص بك هو: *{otp}*\n" +
                                  $"Your verification code is: *{otp}*\n" +
                                  $"⏱️ صالح لمدة 5 دقائق / Valid for 5 minutes.";

                    var payload = new
                    {
                        instance_id = (string)waConfig.InstanceId,
                        message,
                        number = NormalizePhone(mobile)
                    };

                    var json = JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await client.PostAsync(EnjazatikUrl, content);
                }
                catch
                {
                    // Silent — OTP still generated; log in production
                }
            }

            return Ok(new ApiResultBilingual<object>(
                true, null, new { Sent = true },
                "تم إرسال رمز التحقق عبر واتساب",
                "Verification code sent via WhatsApp"));
        }

        // ──────────────────────────────────────────────────────────────
        // POST /api/online/auth/verify-otp
        // Validates OTP — must succeed before calling register
        // ──────────────────────────────────────────────────────────────
        [HttpPost("auth/verify-otp")]
        [AllowAnonymous]
        public ActionResult<ApiResultBilingual<object>> VerifyOtp(
            [FromBody] VerifyOtpDto request)
        {
            if (request == null)
                return Ok(new ApiResultBilingual<object>(false, "Request required", null,
                    "البيانات مطلوبة", "Request required"));

            var mobile = request.MobileNo.Trim();
            using var conn = sqlConnections.NewByKey("Default");

            // Check existing external customer
            var existing = conn.Query<dynamic>(
                @"SELECT CUSTOMER_ID, OtpCode, OtpExpiry, IsExternalUser
                  FROM dbo.CUSTOMER
                  WHERE CUSTOMER_PHONE1 = @Mobile AND IsExternalUser = 1",
                new { Mobile = mobile }).FirstOrDefault();

            string? storedOtp = null;
            DateTime? storedExp = null;

            if (existing != null)
            {
                storedOtp = (string?)existing.OtpCode;
                storedExp = (DateTime?)existing.OtpExpiry;
            }
            else if (OtpCache.TryGet(mobile, out var cached))
            {
                storedOtp = cached.Otp;
                storedExp = cached.Expiry;
            }

            if (storedOtp == null || storedExp == null)
                return Ok(new ApiResultBilingual<object>(false, null, null,
                    "لم يتم طلب رمز التحقق أو انتهت صلاحيته",
                    "OTP not requested or expired"));

            if (DateTime.UtcNow > storedExp)
                return Ok(new ApiResultBilingual<object>(false, null, null,
                    "انتهت صلاحية رمز التحقق",
                    "OTP has expired"));

            if (storedOtp != request.OtpCode.Trim())
                return Ok(new ApiResultBilingual<object>(false, null, null,
                    "رمز التحقق غير صحيح",
                    "Invalid OTP code"));

            // Mark OTP as verified (clear it so it can't be reused)
            if (existing != null)
                conn.Execute(
                    "UPDATE dbo.CUSTOMER SET OtpCode = NULL, OtpExpiry = NULL WHERE CUSTOMER_PHONE1 = @Mobile",
                    new { Mobile = mobile });
            else
                OtpCache.SetVerified(mobile);

            return Ok(new ApiResultBilingual<object>(true, null, new { Verified = true },
                "تم التحقق بنجاح",
                "OTP verified successfully"));
        }

        // ──────────────────────────────────────────────────────────────
        // POST /api/online/auth/register
        // Step 2 — create CUSTOMER after OTP verified
        // Follows same structure as POST /api/lookups/customers
        // ──────────────────────────────────────────────────────────────
        [HttpPost("auth/register")]
        [AllowAnonymous]
        public ActionResult<ApiResultBilingual<LoginResponseDto>> Register(
            [FromBody] RegisterDto request)
        {
            if (request == null)
                return Ok(new ApiResultBilingual<LoginResponseDto>(
                    false, "Request required", null,
                    "البيانات مطلوبة", "Request required"));

            if (string.IsNullOrWhiteSpace(request.FullName))
                return Ok(new ApiResultBilingual<LoginResponseDto>(
                    false, null, null, "الاسم مطلوب", "Full name is required"));

            if (string.IsNullOrWhiteSpace(request.MobileNo))
                return Ok(new ApiResultBilingual<LoginResponseDto>(
                    false, null, null, "رقم الهاتف مطلوب", "Mobile is required"));

            if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains("@"))
                return Ok(new ApiResultBilingual<LoginResponseDto>(
                    false, null, null, "البريد الإلكتروني غير صحيح", "Invalid email"));

            if (request.Password != request.ConfirmPassword)
                return Ok(new ApiResultBilingual<LoginResponseDto>(
                    false, null, null, "كلمات المرور غير متطابقة", "Passwords do not match"));

            if (request.Password.Length < 6)
                return Ok(new ApiResultBilingual<LoginResponseDto>(
                    false, null, null,
                    "كلمة المرور يجب أن تكون 6 أحرف على الأقل",
                    "Password must be at least 6 characters"));

            var mobile = request.MobileNo.Trim();

            // OTP must have been verified first
            if (!OtpCache.IsVerified(mobile))
            {
                using var connCheck = sqlConnections.NewByKey("Default");
                var otpRow = connCheck.Query<dynamic>(
                    "SELECT OtpCode FROM dbo.CUSTOMER WHERE CUSTOMER_PHONE1 = @M AND IsExternalUser = 1",
                    new { M = mobile }).FirstOrDefault();
                // If OTP still present (not cleared) → not yet verified
                if (otpRow != null && otpRow.OtpCode != null)
                    return Ok(new ApiResultBilingual<LoginResponseDto>(
                        false, null, null,
                        "يجب التحقق من رقم الهاتف أولاً",
                        "Please verify your mobile number first"));
            }

            using var conn = sqlConnections.NewByKey("Default");

            // Validate branch
            var branch = conn.Query<dynamic>(
                "SELECT BRANCH_ID FROM dbo.BRANCH WHERE BRANCH_ID = @Id AND (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)",
                new { Id = request.BranchId }).FirstOrDefault();
            if (branch == null)
                return Ok(new ApiResultBilingual<LoginResponseDto>(
                    false, null, null, "الفرع غير موجود", "Branch not found"));

            // Duplicate phone check
            var dupPhone = conn.Query<dynamic>(
                "SELECT CUSTOMER_ID FROM dbo.CUSTOMER WHERE CUSTOMER_PHONE1 = @Mobile",
                new { Mobile = mobile }).FirstOrDefault();
            if (dupPhone != null)
                return Ok(new ApiResultBilingual<LoginResponseDto>(
                    false, null, null,
                    "رقم الهاتف مسجل بالفعل",
                    "Mobile number already registered"));

            // Duplicate email check
            var dupEmail = conn.Query<dynamic>(
                "SELECT CUSTOMER_ID FROM dbo.CUSTOMER WHERE ExternalEmail = @Email",
                new { Email = request.Email.Trim() }).FirstOrDefault();
            if (dupEmail != null)
                return Ok(new ApiResultBilingual<LoginResponseDto>(
                    false, null, null,
                    "البريد الإلكتروني مسجل بالفعل",
                    "Email already registered"));

            // Generate next CUSTOMER_ID (same pattern as LookupsApiController)
            var maxId = conn.Query<int?>("SELECT MAX(CUSTOMER_ID) FROM dbo.CUSTOMER")
                .FirstOrDefault();
            int newCustomerId = (maxId ?? 0) + 1;

            var refGuide = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var lang = (request.NotificationLang ?? "ar").ToLower() == "en" ? "en" : "ar";
            var gender = request.Gender == "Female" ? "Female" : "Male";
            var hashedPw = HashPassword(request.Password);

            conn.Execute(@"
                INSERT INTO dbo.CUSTOMER (
                    CUSTOMER_ID, CUSTOMER_NAME, CUSTOMER_PHONE1,
                    CUSTOMER_CREATED_DATE, BRANCH_ID, CUSTOMER_REF_GUIDE,
                    LoyaltyBalance, MembershipBalance, UnpaidSales,
                    NotificationLang, CUSTOMER_IS_BLOCK,
                    IsExternalUser, ExternalEmail, ExternalGender, ExternalPassword,
                    OtpCode, OtpExpiry
                )
                VALUES (
                    @CustomerId, @CustomerName, @CustomerPhone,
                    @CreatedDate, @BranchId, @RefGuide,
                    0, 0, 0,
                    @Lang, 0,
                    1, @Email, @Gender, @Password,
                    NULL, NULL
                )",
                new
                {
                    CustomerId = newCustomerId,
                    CustomerName = request.FullName.Trim(),
                    CustomerPhone = mobile,
                    CreatedDate = now,
                    BranchId = request.BranchId,
                    RefGuide = refGuide,
                    Lang = lang,
                    Email = request.Email.Trim(),
                    Gender = gender,
                    Password = hashedPw
                });

            OtpCache.Clear(mobile);

            var (token, refresh, expiry) = IssueToken(
                newCustomerId, refGuide.ToString(), request.BranchId);

            var user = new OnlineUserDto(
                CustomerId: newCustomerId,
                FullName: request.FullName.Trim(),
                Email: request.Email.Trim(),
                CustomerPhone: mobile,
                Gender: gender,
                BranchId: request.BranchId,
                CustomerRef: refGuide.ToString(),
                NotificationLang: lang);

            return Ok(new ApiResultBilingual<LoginResponseDto>(
                true, null,
                new LoginResponseDto(true, token, refresh, expiry, user),
                "تم إنشاء الحساب بنجاح",
                "Account created successfully"));
        }

        // ──────────────────────────────────────────────────────────────
        // POST /api/online/auth/login
        // ──────────────────────────────────────────────────────────────
        [HttpPost("auth/login")]
        [AllowAnonymous]
        public ActionResult<ApiResultBilingual<LoginResponseDto>> Login(
            [FromBody] LoginDto request)
        {
            if (request == null)
                return Ok(new ApiResultBilingual<LoginResponseDto>(
                    false, "Request required", null,
                    "البيانات مطلوبة", "Request required"));

            using var conn = sqlConnections.NewByKey("Default");

            var customer = conn.Query<dynamic>(@"
                SELECT
                    CUSTOMER_ID       AS CustomerId,
                    CUSTOMER_NAME     AS FullName,
                    CUSTOMER_PHONE1   AS CustomerPhone,
                    ExternalEmail     AS Email,
                    ExternalGender    AS Gender,
                    BRANCH_ID         AS BranchId,
                    CUSTOMER_REF_GUIDE AS CustomerRef,
                    NotificationLang,
                    ExternalPassword  AS PasswordHash,
                    ISNULL(CUSTOMER_IS_BLOCK, 0) AS IsBlock
                FROM dbo.CUSTOMER
                WHERE CUSTOMER_PHONE1 = @Mobile
                  AND IsExternalUser  = 1",
                new { Mobile = request.Mobile.Trim() }).FirstOrDefault();

            if (customer == null)
                return Ok(new ApiResultBilingual<LoginResponseDto>(
                    false, null, null,
                    "رقم الهاتف أو كلمة المرور غير صحيحة",
                    "Invalid mobile or password"));

            if (Convert.ToInt32(customer.IsBlock) == 1)
                return Ok(new ApiResultBilingual<LoginResponseDto>(
                    false, null, null,
                    "تم إيقاف هذا الحساب، يرجى التواصل مع الإدارة",
                    "Account is blocked, please contact support"));

            string storedHash = (string?)customer.PasswordHash ?? "";
            if (!VerifyPassword(request.Password, storedHash))
                return Ok(new ApiResultBilingual<LoginResponseDto>(
                    false, null, null,
                    "رقم الهاتف أو كلمة المرور غير صحيحة",
                    "Invalid mobile or password"));

            int customerId = (int)customer.CustomerId;
            int branchId = (int)customer.BranchId;
            string customerRef = customer.CustomerRef.ToString();

            var (token, refresh, expiry) = IssueToken(customerId, customerRef, branchId);

            var user = new OnlineUserDto(
                CustomerId: customerId,
                FullName: (string)(customer.FullName ?? ""),
                Email: (string?)customer.Email,
                CustomerPhone: (string)(customer.CustomerPhone ?? ""),
                Gender: (string?)customer.Gender,
                BranchId: branchId,
                CustomerRef: customerRef,
                NotificationLang: (string?)customer.NotificationLang ?? "ar");

            return Ok(new ApiResultBilingual<LoginResponseDto>(
                true, null,
                new LoginResponseDto(true, token, refresh, expiry, user),
                "تم تسجيل الدخول بنجاح",
                "Login successful"));
        }

        // ──────────────────────────────────────────────────────────────
        // POST /api/online/auth/refresh-token
        // ──────────────────────────────────────────────────────────────
        [HttpPost("auth/refresh-token")]
        [AllowAnonymous]
        public ActionResult<ApiResult<LoginResponseDto>> RefreshToken(
            [FromBody] RefreshTokenDto request)
        {
            // Lightweight: re-read user from token claims and re-issue
            // (stateless — no refresh token table needed for MVP)
            if (request == null || string.IsNullOrWhiteSpace(request.RefreshToken))
                return Ok(new ApiResult<LoginResponseDto>(false, "Refresh token required", null));

            // In production: validate stored refresh token.
            // For MVP: require Authorization header with expired token to extract claims.
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            if (authHeader == null || !authHeader.StartsWith("Bearer "))
                return Ok(new ApiResult<LoginResponseDto>(false, "Bearer token required", null));

            var oldToken = authHeader.Substring(7).Trim();
            var handler = new JwtSecurityTokenHandler();

            JwtSecurityToken? jwt;
            try { jwt = handler.ReadJwtToken(oldToken); }
            catch { return Ok(new ApiResult<LoginResponseDto>(false, "Invalid token", null)); }

            var subClaim = jwt.Claims.FirstOrDefault(c => c.Type == "sub"
                || c.Type == ClaimTypes.NameIdentifier)?.Value;
            var refClaim = jwt.Claims.FirstOrDefault(c => c.Type == "ref")?.Value;
            var branchClaim = jwt.Claims.FirstOrDefault(c => c.Type == "branch")?.Value;

            if (!int.TryParse(subClaim, out var custId) ||
                !int.TryParse(branchClaim, out var branchId))
                return Ok(new ApiResult<LoginResponseDto>(false, "Invalid token claims", null));

            using var conn = sqlConnections.NewByKey("Default");
            var exists = conn.Query<dynamic>(
                "SELECT CUSTOMER_ID FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id AND IsExternalUser = 1",
                new { Id = custId }).FirstOrDefault();

            if (exists == null)
                return Ok(new ApiResult<LoginResponseDto>(false, "Customer not found", null));

            var (token, refresh, expiry) = IssueToken(custId, refClaim ?? "", branchId);

            return Ok(new ApiResult<LoginResponseDto>(true, null,
                new LoginResponseDto(true, token, refresh, expiry, null)));
        }

        // ══════════════════════════════════════════════════════════════
        // REGION: CONFIG
        // ══════════════════════════════════════════════════════════════

        // ──────────────────────────────────────────────────────────────
        // GET /api/online/config/data
        // Returns everything the website needs on load (branches,
        // categories, image base URLs, shop info)
        // ──────────────────────────────────────────────────────────────
        [HttpGet("config/data")]
        [AllowAnonymous]
        public ActionResult<ApiResult<OnlineConfigDto>> GetConfigData()
        {
            using var conn = sqlConnections.NewByKey("Default");

            var branches = conn.Query<OnlineBranchDto>(@"
                SELECT
                    BRANCH_ID    AS Id,
                    BRANCH_NAME2 AS ArabicName,
                    BRANCH_NAME1 AS EnglishName,
                    BRANCH_PHONE AS Phone,
                    BRANCH_ADRESS AS Address
                FROM dbo.BRANCH
                WHERE (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)
                ORDER BY BRANCH_ID").ToList();

            var categories = conn.Query<OnlineAppointmentCategoryDto>(@"
                SELECT
                    Id,
                    ArabicName,
                    EnglishName,
                    DocumentName,
                    CAST(IsMakeup  AS BIT) AS IsMakeup,
                    CAST(IsPackage AS BIT) AS IsPackage
                FROM dbo.AppointmentCategories
                WHERE Deleted = 0
                ORDER BY Id").ToList();

            var settings = conn.Query<(string Key, string Value)>(@"
                SELECT SETTING_KEY AS [Key], SETTING_VALUE AS [Value]
                FROM dbo.SYSTEM_SETTING
                WHERE SETTING_KEY IN (
                    'staff_image_url',
                    'product_image_url',
                    'bookingCategory_image_url',
                    'shop_logo',
                    'shop_nameEN',
                    'shop_nameAR',
                    'currency_symbol'
                )").ToDictionary(x => x.Key, x => x.Value);

            string Get(string key) =>
                settings.TryGetValue(key, out var v) ? v : "";

            var dto = new OnlineConfigDto(
                Branches: branches,
                AppointmentCategories: categories,
                CurrencySymbol: Get("currency_symbol") is { Length: > 0 } cs ? cs : "KWD",
                ShopLogoUrl: Get("shop_logo"),
                ShopNameEN: Get("shop_nameEN"),
                ShopNameAR: Get("shop_nameAR"),
                StaffImageBaseUrl: Get("staff_image_url"),
                CategoryImageBaseUrl: Get("bookingCategory_image_url"),
                ProductImageBaseUrl: Get("product_image_url")
            );

            return Ok(new ApiResult<OnlineConfigDto>(true, null, dto));
        }

        // ──────────────────────────────────────────────────────────────
        // GET /api/online/config/areas
        // ──────────────────────────────────────────────────────────────
        [HttpGet("config/areas")]
        [AllowAnonymous]
        public ActionResult<ApiResult<List<OnlineAreaDto>>> GetAreas()
        {
            using var conn = sqlConnections.NewByKey("Default");

            var areas = conn.Query<OnlineAreaDto>(@"
                SELECT
                    a.AREA_ID           AS AreaId,
                    a.AREA_NAME2        AS ArabicName,
                    a.AREA_NAME1        AS EnglishName,
                    a.GOVERNORATE_ID    AS GovernorateId,
                    g.GOVERNORATE_NAME2 AS GovernorateNameAR,
                    g.GOVERNORATE_NAME1 AS GovernorateNameEN
                FROM dbo.GOVERNORATE_AREA a
                INNER JOIN dbo.GOVERNORATE g
                    ON g.GOVERNORATE_ID = a.GOVERNORATE_ID
                ORDER BY a.GOVERNORATE_ID, a.AREA_ID").ToList();

            return Ok(new ApiResult<List<OnlineAreaDto>>(true, null, areas));
        }

        // ══════════════════════════════════════════════════════════════
        // REGION: AVAILABILITY
        // ══════════════════════════════════════════════════════════════

        // ──────────────────────────────────────────────────────────────
        // GET /api/online/availability/staff?branchId=&date=
        // All active staff (normal flow) with live IsAvailable flag
        // ──────────────────────────────────────────────────────────────
        [HttpGet("availability/staff")]
        [AllowAnonymous]
        public ActionResult<ApiResult<List<OnlineStaffDto>>> GetStaff(
            [FromQuery] int branchId, [FromQuery] string date)
        {
            if (!DateTime.TryParse(date, out var parsedDate))
                return Ok(new ApiResult<List<OnlineStaffDto>>(false, "Invalid date", null));

            using var conn = sqlConnections.NewByKey("Default");
            var staff = GetStaffList(conn, branchId, parsedDate.Date, makeupOnly: false);
            return Ok(new ApiResult<List<OnlineStaffDto>>(true, null, staff));
        }

        // ──────────────────────────────────────────────────────────────
        // GET /api/online/availability/staff-makeup?branchId=&date=
        // Only IsMakeupArtist = 1
        // ──────────────────────────────────────────────────────────────
        [HttpGet("availability/staff-makeup")]
        [AllowAnonymous]
        public ActionResult<ApiResult<List<OnlineStaffDto>>> GetMakeupStaff(
            [FromQuery] int branchId, [FromQuery] string date)
        {
            if (!DateTime.TryParse(date, out var parsedDate))
                return Ok(new ApiResult<List<OnlineStaffDto>>(false, "Invalid date", null));

            using var conn = sqlConnections.NewByKey("Default");
            var staff = GetStaffList(conn, branchId, parsedDate.Date, makeupOnly: true);
            return Ok(new ApiResult<List<OnlineStaffDto>>(true, null, staff));
        }

        private List<OnlineStaffDto> GetStaffList(
    IDbConnection conn, int branchId, DateTime date, bool makeupOnly)
        {
            // ── 1. Load all active staff ──────────────────────────────────────
            var staffSql = @"
        SELECT
            s.Id,
            s.ArabicName,
            s.EnglishName,
            s.DocumentName
        FROM dbo.STAFF s
        WHERE s.Deleted = 0
          AND s.Active  = 1
          AND s.BranchId = @BranchId"
                + (makeupOnly ? "\n  AND s.IsMakeupArtist = 1" : "")
                + "\nORDER BY s.EnglishName";

            var staffRows = conn.Query<dynamic>(staffSql,
                new { BranchId = branchId, Date = date }).ToList();

            if (staffRows.Count == 0) return new List<OnlineStaffDto>();

            // ── 2. Calendar settings ──────────────────────────────────────────
            var settings = conn.Query<(string Key, string Value)>(@"
        SELECT SETTING_KEY AS [Key], SETTING_VALUE AS [Value]
        FROM dbo.SYSTEM_SETTING
        WHERE SETTING_KEY IN (
            'calendarStartHour','calendarEndHour','AppointmentDuration'
        )").ToDictionary(x => x.Key, x => x.Value);

            int startHour = settings.TryGetValue("calendarStartHour", out var sh) && int.TryParse(sh, out var shi) ? shi : 10;
            int endHour = settings.TryGetValue("calendarEndHour", out var eh) && int.TryParse(eh, out var ehi) ? ehi : 22;
            // Step is always 15 min (matches dashboard), duration fallback = 15 min
            const int stepMinutes = 30;
            int durationMin = settings.TryGetValue("AppointmentDuration", out var sd) && int.TryParse(sd, out var sdi) ? sdi : stepMinutes;

            var staffIds = staffRows.Select(s => (int)s.Id).ToList();

            // ── 3. Busy intervals per staff (appointments + one-off blocks) ───
            var appointments = conn.Query<dynamic>(@"
        SELECT StaffId,
               StartTime AS BStart,
               EndTime   AS BEnd
        FROM dbo.AppointmentData
        WHERE StaffId IN @StaffIds
          AND CAST(AppointmentDate AS DATE) = @Date
          AND Status != 'cancelled'",
                new { StaffIds = staffIds, Date = date }).ToList();

            var oneOffBlocks = conn.Query<dynamic>(@"
        SELECT StaffId,
               StartTime AS BStart,
               EndTime   AS BEnd
        FROM dbo.StaffTimeBlocks
        WHERE StaffId IN @StaffIds
          AND CAST(BlockDate AS DATE) = @Date
          AND Deleted = 0
          AND IsRecurring = 0",
                new { StaffIds = staffIds, Date = date }).ToList();

            // ── 4. Recurring blocks ───────────────────────────────────────────
            var recurringBlocks = conn.Query<dynamic>(@"
        SELECT StaffId, RecurrenceRule, RecurringStart, RecurringEnd,
               StartTime AS BStart, EndTime AS BEnd
        FROM dbo.StaffTimeBlocks
        WHERE StaffId IN @StaffIds
          AND IsRecurring = 1
          AND Deleted = 0
          AND (RecurringStart IS NULL OR RecurringStart <= @Date)
          AND (RecurringEnd   IS NULL OR RecurringEnd   >= @Date)",
                new { StaffIds = staffIds, Date = date }).ToList();

            // Build busy intervals per staff
            var busyByStaff = new Dictionary<int, List<(TimeSpan Start, TimeSpan End)>>();
            foreach (var sid in staffIds)
                busyByStaff[sid] = new List<(TimeSpan, TimeSpan)>();

            foreach (var a in appointments)
                busyByStaff[(int)a.StaffId].Add(((TimeSpan)a.BStart, (TimeSpan)a.BEnd));

            foreach (var b in oneOffBlocks)
                busyByStaff[(int)b.StaffId].Add(((TimeSpan)b.BStart, (TimeSpan)b.BEnd));

            foreach (var rb in recurringBlocks)
            {
                if (RecurrenceAppliesToDate((string)rb.RecurrenceRule, date))
                    busyByStaff[(int)rb.StaffId].Add(((TimeSpan)rb.BStart, (TimeSpan)rb.BEnd));
            }

            // ── 5. Check if staff has AT LEAST ONE free slot ──────────────────
            var startSpan = TimeSpan.FromHours(startHour);
            var endSpan = TimeSpan.FromHours(endHour);
            var step = TimeSpan.FromMinutes(stepMinutes);
            var duration = TimeSpan.FromMinutes(durationMin);

            bool HasFreeSlot(int staffId)
            {
                var busy = busyByStaff[staffId];
                var cursor = startSpan;
                while (cursor + duration <= endSpan)
                {
                    var slotEnd = cursor + duration;
                    bool blocked = busy.Any(b => cursor < b.End && slotEnd > b.Start);
                    if (!blocked) return true;
                    cursor += step;
                }
                return false;
            }

            return staffRows.Select(s =>
            {
                int sid = (int)s.Id;
                bool hasSlots = HasFreeSlot(sid);
                return new OnlineStaffDto(
                    Id: sid,
                    ArabicName: (string)(s.ArabicName ?? ""),
                    EnglishName: (string)(s.EnglishName ?? ""),
                    DocumentName: (string?)s.DocumentName,
                    IsAvailable: hasSlots
                );
            }).ToList();
        }

        // ──────────────────────────────────────────────────────────────
        // GET /api/online/availability/services
        //     ?branchId=&appointmentCategoryId=
        // Normal flow services — price from ITEM_UNIT.ITEM_UNIT_PRICE
        // ──────────────────────────────────────────────────────────────
        [HttpGet("availability/services")]
        [AllowAnonymous]
        public ActionResult<ApiResult<List<OnlineServiceItemDto>>> GetServices(
            [FromQuery] int branchId, [FromQuery] int appointmentCategoryId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var items = conn.Query<OnlineServiceItemDto>(@"
                SELECT
                    iu.ITEM_UNIT_ID    AS ItemUnitId,
                    i.ITEM_ID          AS ItemId,
                    i.ITEM_NAME2       AS ArabicName,
                    i.ITEM_NAME1       AS EnglishName,
                    iu.ITEM_UNIT_PRICE AS Price,
                    iu.Deposit         AS Deposit,
                    CAST(iu.ITEM_UNIT_DURATION AS FLOAT) AS Duration,
                    i.DocumentName
                FROM dbo.ITEM_UNIT iu
                INNER JOIN dbo.ITEM i
                    ON i.ITEM_ID = iu.ITEM_ID
                INNER JOIN dbo.AppointmentCategories ac
                    ON ac.Id = i.AppointmentCategoryId
                WHERE iu.Active = 1
                  AND iu.BranchId = @BranchId
                  AND ac.Id = @CategoryId
                  AND (i.ITEM_IS_ACTIVE = 1 OR i.ITEM_IS_ACTIVE IS NULL)
                  AND ac.Deleted = 0
                ORDER BY i.ITEM_NAME1",
                new { BranchId = branchId, CategoryId = appointmentCategoryId }).ToList();

            return Ok(new ApiResult<List<OnlineServiceItemDto>>(true, null, items));
        }

        // ──────────────────────────────────────────────────────────────
        // GET /api/online/availability/services-by-staff
        //     ?staffId=&appointmentCategoryId=
        // Makeup flow — price from StaffItems.Price (staff-specific)
        // ──────────────────────────────────────────────────────────────
        [HttpGet("availability/services-by-staff")]
        [AllowAnonymous]
        public ActionResult<ApiResult<List<OnlineServiceItemDto>>> GetServicesByStaff(
            [FromQuery] int staffId, [FromQuery] int appointmentCategoryId)
        {
            using var conn = sqlConnections.NewByKey("Default");

            var items = conn.Query<OnlineServiceItemDto>(@"
                SELECT
                    iu.ITEM_UNIT_ID  AS ItemUnitId,
                    i.ITEM_ID        AS ItemId,
                    i.ITEM_NAME2     AS ArabicName,
                    i.ITEM_NAME1     AS EnglishName,
                    si.Price         AS Price,
                    iu.Deposit       AS Deposit,
                    CAST(iu.ITEM_UNIT_DURATION AS FLOAT) AS Duration,
                    i.DocumentName
                FROM dbo.StaffItems si
                INNER JOIN dbo.ITEM_UNIT iu
                    ON iu.ITEM_UNIT_ID = si.ItemUnitId
                INNER JOIN dbo.ITEM i
                    ON i.ITEM_ID = iu.ITEM_ID
                INNER JOIN dbo.AppointmentCategories ac
                    ON ac.Id = i.AppointmentCategoryId
                WHERE si.StaffId = @StaffId
                  AND ac.Id = @CategoryId
                  AND si.Deleted = 0
                  AND (i.ITEM_IS_ACTIVE = 1 OR i.ITEM_IS_ACTIVE IS NULL)
                  AND iu.Active = 1
                ORDER BY i.ITEM_NAME1",
                new { StaffId = staffId, CategoryId = appointmentCategoryId }).ToList();

            return Ok(new ApiResult<List<OnlineServiceItemDto>>(true, null, items));
        }

        // ──────────────────────────────────────────────────────────────
        // POST /api/online/availability/time-slots
        // Returns available HH:mm slots for staff on a given date,
        // accounting for existing appointments AND time blocks.
        // Same logic as GET /api/staff-time-blocks/availability
        // ──────────────────────────────────────────────────────────────
        [HttpPost("availability/time-slots")]
        [AllowAnonymous]
        public ActionResult<ApiResult<List<string>>> GetTimeSlots(
            [FromBody] TimeSlotsRequestDto request)
        {
            if (request == null)
                return Ok(new ApiResult<List<string>>(false, "Request required", null));

            if (!DateTime.TryParse(request.Date, out var parsedDate))
                return Ok(new ApiResult<List<string>>(false, "Invalid date", null));

            if (request.ItemUnitIds == null || request.ItemUnitIds.Count == 0)
                return Ok(new ApiResult<List<string>>(false, "ItemUnitIds required", null));

            using var conn = sqlConnections.NewByKey("Default");

            // ── 1. Calendar settings ──────────────────────────────────
            var settings = conn.Query<(string Key, string Value)>(@"
                SELECT SETTING_KEY AS [Key], SETTING_VALUE AS [Value]
                FROM dbo.SYSTEM_SETTING
                WHERE SETTING_KEY IN (
                    'calendarStartHour','calendarEndHour',
                    'AppointmentDuration','timeZoneOffset'
                )").ToDictionary(x => x.Key, x => x.Value);

            int startHour = settings.TryGetValue("calendarStartHour", out var sh) && int.TryParse(sh, out var shi) ? shi : 10;
            int endHour = settings.TryGetValue("calendarEndHour", out var eh) && int.TryParse(eh, out var ehi) ? ehi : 22;
            // ── 2. Total service duration (sum of all selected items) ─
            var durations = conn.Query<double?>(@"
                SELECT CAST(ITEM_UNIT_DURATION AS FLOAT)
                FROM dbo.ITEM_UNIT
                WHERE ITEM_UNIT_ID IN @Ids",
                new { Ids = request.ItemUnitIds }).ToList();

            double totalDuration = durations.Sum(d => (d == null || d <= 0) ? 30.0 : d.Value);
            if (totalDuration <= 0) totalDuration = 30;
            
            // ── 3. Existing appointments on this day for this staff ───
            var existingApts = conn.Query<(TimeSpan Start, TimeSpan End)>(@"
                SELECT
                    StartTime AS [Start],
                    EndTime   AS [End]
                FROM dbo.AppointmentData
                WHERE StaffId = @StaffId
                  AND CAST(AppointmentDate AS DATE) = @Date
                  AND Status != 'cancelled'",
                new { StaffId = request.StaffId, Date = parsedDate.Date }).ToList();

            // ── 4. Non-recurring blocks ───────────────────────────────
            var nonRecurring = conn.Query<(TimeSpan Start, TimeSpan End)>(@"
                SELECT StartTime AS [Start], EndTime AS [End]
                FROM dbo.StaffTimeBlocks
                WHERE StaffId = @StaffId
                  AND CAST(BlockDate AS DATE) = @Date
                  AND Deleted = 0
                  AND IsRecurring = 0",
                new { StaffId = request.StaffId, Date = parsedDate.Date }).ToList();

            // ── 5. Recurring blocks ───────────────────────────────────
            var recurringBlocks = conn.Query<dynamic>(@"
                SELECT RecurrenceRule, RecurringStart, RecurringEnd, StartTime, EndTime
                FROM dbo.StaffTimeBlocks
                WHERE StaffId = @StaffId
                  AND IsRecurring = 1
                  AND Deleted = 0
                  AND (RecurringStart IS NULL OR RecurringStart <= @Date)
                  AND (RecurringEnd   IS NULL OR RecurringEnd   >= @Date)",
                new { StaffId = request.StaffId, Date = parsedDate.Date }).ToList();

            var recurringBusy = new List<(TimeSpan Start, TimeSpan End)>();
            foreach (var rb in recurringBlocks)
            {
                if (RecurrenceAppliesToDate((string)rb.RecurrenceRule, parsedDate.Date))
                    recurringBusy.Add(((TimeSpan)rb.StartTime, (TimeSpan)rb.EndTime));
            }

            // Combine all busy intervals
            var busySlots = existingApts
                .Concat(nonRecurring)
                .Concat(recurringBusy)
                .ToList();

            // ── 6. Generate candidate slots ───────────────────────────
            var available = new List<string>();
            var cursor = TimeSpan.FromHours(startHour);
            var endSpan = TimeSpan.FromHours(endHour);
            var duration = TimeSpan.FromMinutes(totalDuration);

            // الخطوة = الـ duration نفسه أو 30 دقيقة أيهما أكبر
            var stepMin = TimeSpan.FromMinutes(Math.Max(totalDuration, 30));

            while (cursor + duration <= endSpan)
            {
                var slotEnd = cursor + duration;
                bool busy = busySlots.Any(b => cursor < b.End && slotEnd > b.Start);

                if (!busy)
                    available.Add($"{cursor.Hours:D2}:{cursor.Minutes:D2}");

                cursor += stepMin;
            }

            return Ok(new ApiResult<List<string>>(true, null, available));
        }

        // ══════════════════════════════════════════════════════════════
        // REGION: BOOKINGS
        // ══════════════════════════════════════════════════════════════

        // ──────────────────────────────────────────────────────────────
        // POST /api/online/bookings
        // Create online appointment — inserts into AppointmentData with
        // IsOnlineBooking = 1, then initiates MyFatoorah payment.
        // ──────────────────────────────────────────────────────────────
        [HttpPost("bookings")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public async Task<ActionResult<ApiResultBilingual<OnlineBookingResultDto>>> CreateBooking(
            [FromBody] SubmitOnlineBookingDto request)
        {
            if (request == null)
                return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                    false, "Request required", null,
                    "البيانات مطلوبة", "Request required"));

            int customerId = GetCurrentCustomerId();
            if (customerId == 0)
                return Unauthorized();

            if (!TryParseTime(request.StartTime, out var startTs))
                return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                    false, null, null,
                    "وقت البداية غير صحيح (HH:mm)",
                    "StartTime must be HH:mm"));

            var serviceType = (request.ServiceType ?? "SALON").ToUpperInvariant();
            if (serviceType != "SALON" && serviceType != "HOME")
                return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                    false, null, null,
                    "نوع الخدمة يجب أن يكون SALON أو HOME",
                    "ServiceType must be SALON or HOME"));

            if (request.ItemUnitIds == null || request.ItemUnitIds.Count == 0)
                return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                    false, null, null,
                    "يجب اختيار خدمة واحدة على الأقل",
                    "Select at least one service"));

            if (!DateTime.TryParse(request.BookingDate, out var bookingDate))
                return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                    false, null, null, "تاريخ غير صحيح", "Invalid date"));

            using var conn = sqlConnections.NewByKey("Default");

            // ── Validate Branch ───────────────────────────────────────
            var branch = conn.Query<dynamic>(@"
                SELECT BRANCH_ID, EnglishCurrencyName, ArabicCurrencyName
                FROM dbo.BRANCH
                WHERE BRANCH_ID = @Id
                  AND (BRANCH_IS_ACTIVE = 1 OR BRANCH_IS_ACTIVE IS NULL)",
                new { Id = request.BranchId }).FirstOrDefault();
            if (branch == null)
                return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                    false, null, null, "الفرع غير موجود", "Branch not found"));

            // ── Validate Customer ─────────────────────────────────────
            var customer = conn.Query<dynamic>(@"
                SELECT CUSTOMER_ID, CUSTOMER_NAME, CUSTOMER_PHONE1,
                       ISNULL(NotificationLang,'ar') AS Lang,
                       ExternalEmail
                FROM dbo.CUSTOMER
                WHERE CUSTOMER_ID = @Id",
                new { Id = customerId }).FirstOrDefault();
            if (customer == null)
                return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                    false, null, null, "العميل غير موجود", "Customer not found"));

            // ── Validate Staff ────────────────────────────────────────
            var staff = conn.Query<dynamic>(@"
                SELECT Id, ArabicName, EnglishName, IsMakeupArtist
                FROM dbo.STAFF
                WHERE Id = @Id AND Active = 1 AND Deleted = 0",
                new { Id = request.StaffId }).FirstOrDefault();
            if (staff == null)
                return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                    false, null, null,
                    "الموظف غير موجود أو غير نشط",
                    "Staff not found or not active"));

            // ── Validate Category (IsMakeup check) ───────────────────
            var category = conn.Query<dynamic>(@"
                SELECT Id, ArabicName, EnglishName,
                       CAST(IsMakeup AS BIT) AS IsMakeup
                FROM dbo.AppointmentCategories
                WHERE Id = @Id AND Deleted = 0",
                new { Id = request.AppointmentCategoryId }).FirstOrDefault();
            if (category == null)
                return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                    false, null, null,
                    "التصنيف غير موجود", "Category not found"));

            bool isMakeup = (bool)category.IsMakeup;
            if (isMakeup && !(bool)staff.IsMakeupArtist)
                return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                    false, null, null,
                    "هذا الموظف ليس فنان مكياج",
                    "Selected staff is not a makeup artist"));

            // ── Validate & price ItemUnits ────────────────────────────
            List<dynamic> itemUnits;
            decimal totalPrice;
            decimal totalDeposit;

            if (isMakeup)
            {
                // Makeup: price comes from StaffItems
                itemUnits = conn.Query<dynamic>(@"
                    SELECT
                        iu.ITEM_UNIT_ID    AS ItemUnitId,
                        iu.ITEM_ID         AS ItemId,
                        iu.UNIT_ID         AS UnitId,
                        si.Price           AS Price,
                        iu.Deposit         AS Deposit,
                        CAST(iu.ITEM_UNIT_DURATION AS FLOAT) AS Duration,
                        i.ITEM_NAME1       AS NameEN,
                        i.ITEM_NAME2       AS NameAR
                    FROM dbo.StaffItems si
                    INNER JOIN dbo.ITEM_UNIT iu ON iu.ITEM_UNIT_ID = si.ItemUnitId
                    INNER JOIN dbo.ITEM i ON i.ITEM_ID = iu.ITEM_ID
                    WHERE si.StaffId = @StaffId
                      AND si.ItemUnitId IN @Ids
                      AND si.Deleted = 0
                      AND iu.Active = 1",
                    new { StaffId = request.StaffId, Ids = request.ItemUnitIds }).ToList();

                if (itemUnits.Count != request.ItemUnitIds.Distinct().Count())
                    return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                        false, null, null,
                        "بعض الخدمات المختارة غير متاحة لهذا الموظف",
                        "Some selected services are not available for this staff"));

                totalPrice = itemUnits.Sum(x => (decimal)x.Price);
                totalDeposit = itemUnits.Sum(x => (decimal)x.Deposit);
            }
            else
            {
                // Normal: price from ITEM_UNIT (single item)
                if (request.ItemUnitIds.Count != 1)
                    return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                        false, null, null,
                        "يجب اختيار خدمة واحدة فقط",
                        "Select exactly one service for normal booking"));

                itemUnits = conn.Query<dynamic>(@"
                    SELECT
                        iu.ITEM_UNIT_ID    AS ItemUnitId,
                        iu.ITEM_ID         AS ItemId,
                        iu.UNIT_ID         AS UnitId,
                        iu.ITEM_UNIT_PRICE AS Price,
                        iu.Deposit         AS Deposit,
                        CAST(iu.ITEM_UNIT_DURATION AS FLOAT) AS Duration,
                        i.ITEM_NAME1       AS NameEN,
                        i.ITEM_NAME2       AS NameAR
                    FROM dbo.ITEM_UNIT iu
                    INNER JOIN dbo.ITEM i ON i.ITEM_ID = iu.ITEM_ID
                    WHERE iu.ITEM_UNIT_ID = @Id
                      AND iu.Active = 1
                      AND (i.ITEM_IS_ACTIVE = 1 OR i.ITEM_IS_ACTIVE IS NULL)",
                    new { Id = request.ItemUnitIds[0] }).ToList();

                if (itemUnits.Count == 0)
                    return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                        false, null, null,
                        "الخدمة غير موجودة", "Service not found"));

                totalPrice = (decimal)itemUnits[0].Price;
                totalDeposit = (decimal)itemUnits[0].Deposit;
            }

            // ── Calculate EndTime ─────────────────────────────────────
            double totalDurationMin = itemUnits.Sum(x => {
                double d = (double)(x.Duration ?? 0);
                return d <= 0 ? 30.0 : d;
            });
            if (totalDurationMin <= 0) totalDurationMin = 30;
            var endTs = startTs + TimeSpan.FromMinutes(totalDurationMin);

            // ── Conflict check (same as AppointmentsApiController) ────
            var conflict = conn.Query<dynamic>(@"
                SELECT Id FROM dbo.AppointmentData
                WHERE StaffId = @StaffId
                  AND CAST(AppointmentDate AS DATE) = @Date
                  AND Status != 'cancelled'
                  AND StartTime < @EndTime
                  AND EndTime   > @StartTime",
                new
                {
                    StaffId = request.StaffId,
                    Date = bookingDate.Date,
                    StartTime = startTs,
                    EndTime = endTs
                }).FirstOrDefault();

            if (conflict != null)
                return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                    false, null, null,
                    "الوقت المختار محجوز بالفعل",
                    $"Time conflict with appointment #{(int)conflict.Id}"));

            // Non-recurring block conflict
            var blockConflict = conn.Query<dynamic>(@"
                SELECT Id, Title, BlockType
                FROM dbo.StaffTimeBlocks
                WHERE StaffId = @StaffId
                  AND CAST(BlockDate AS DATE) = @Date
                  AND Deleted = 0
                  AND IsRecurring = 0
                  AND StartTime < @EndTime
                  AND EndTime   > @StartTime",
                new
                {
                    StaffId = request.StaffId,
                    Date = bookingDate.Date,
                    StartTime = startTs,
                    EndTime = endTs
                }).FirstOrDefault();

            if (blockConflict != null)
                return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                    false, null, null,
                    "الموظف غير متاح في هذا الوقت",
                    $"Staff is blocked ({(string?)blockConflict.Title ?? (string)blockConflict.BlockType})"));

            // Recurring block conflict
            var recurringBlocks = conn.Query<dynamic>(@"
                SELECT RecurrenceRule, RecurringStart, RecurringEnd,
                       StartTime, EndTime, Title, BlockType
                FROM dbo.StaffTimeBlocks
                WHERE StaffId = @StaffId
                  AND IsRecurring = 1
                  AND Deleted = 0
                  AND (RecurringStart IS NULL OR RecurringStart <= @Date)
                  AND (RecurringEnd   IS NULL OR RecurringEnd   >= @Date)
                  AND StartTime < @EndTime
                  AND EndTime   > @StartTime",
                new
                {
                    StaffId = request.StaffId,
                    Date = bookingDate.Date,
                    StartTime = startTs,
                    EndTime = endTs
                }).ToList();

            foreach (var rb in recurringBlocks)
            {
                if (RecurrenceAppliesToDate((string)rb.RecurrenceRule, bookingDate.Date))
                    return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                        false, null, null,
                        "الموظف غير متاح في هذا الوقت",
                        $"Staff has a recurring block ({(string?)rb.Title ?? (string)rb.BlockType})"));
            }

            // ── First ItemUnit drives ItemId / UnitId on AppointmentData ─
            var firstItem = itemUnits[0];
            int itemId = (int)firstItem.ItemId;
            int unitId = (int)firstItem.UnitId;

            // ── INSERT into AppointmentData ───────────────────────────
            var appointmentId = conn.Query<int>(@"
                INSERT INTO dbo.AppointmentData (
                    BranchId, CustomerId, ItemId, UnitId, StaffId,
                    AppointmentDate, StartTime, EndTime,
                    NumberOfPersons, ServiceType, IsOnlineBooking, Notes,
                    UnitPrice, DiscountPercent, DiscountedUnitPrice, TotalPrice,
                    PaidAmount, PaymentStatus, DepositAmount,
                    Status, CheckoutStatus, CreatedAt
                )
                OUTPUT INSERTED.Id
                VALUES (
                    @BranchId, @CustomerId, @ItemId, @UnitId, @StaffId,
                    @AppointmentDate, @StartTime, @EndTime,
                    @NumberOfPersons, @ServiceType, 1, @Notes,
                    @TotalPrice, 0, @TotalPrice, @TotalPrice,
                    0, 'NONE', @DepositAmount,
                    'scheduled', 'open', SYSUTCDATETIME()
                )",
                new
                {
                    BranchId = request.BranchId,
                    CustomerId = customerId,
                    ItemId = itemId,
                    UnitId = unitId,
                    StaffId = request.StaffId,
                    AppointmentDate = bookingDate.Date,
                    StartTime = startTs,
                    EndTime = endTs,
                    NumberOfPersons = request.NumberOfPersons < 1 ? 1 : request.NumberOfPersons,
                    ServiceType = serviceType,
                    Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
                    TotalPrice = totalPrice,
                    DepositAmount = totalDeposit
                }).FirstOrDefault();

            // ── Initiate MyFatoorah payment ───────────────────────────
            string? paymentUrl = null;
            bool isDeposit = totalDeposit > 0;
            decimal chargeAmt = isDeposit ? totalDeposit : totalPrice;

            var mfConfig = conn.Query<dynamic>(@"
                SELECT TOP 1 ApiKey, ApiBaseUrl, IsTestMode, IsEnabled,
                             CallbackUrl, ErrorUrl
                FROM dbo.MYFATOORAH_CONFIG ORDER BY Id").FirstOrDefault();

            if (mfConfig != null && (bool)mfConfig.IsEnabled && chargeAmt > 0)
            {
                try
                {
                    string baseUrl = $"{Request.Scheme}://{Request.Host}";
                    string callbackUrl = (string?)mfConfig.CallbackUrl
                                      ?? $"{baseUrl}/api/myfatoorah/callback";
                    string errorUrl = (string?)mfConfig.ErrorUrl
                                      ?? $"{baseUrl}/api/myfatoorah/callback";
                    string currency = (string)(branch.EnglishCurrencyName ?? "KWD");
                    string custLang = (string)customer.Lang;
                    string custName = (string)customer.CUSTOMER_NAME;
                    string custPhone = (string)customer.CUSTOMER_PHONE1;
                    string? custEmail = (string?)customer.ExternalEmail;
                    string itemLabel = custLang == "en"
                        ? (string)(firstItem.NameEN ?? firstItem.NameAR ?? "Service")
                        : (string)(firstItem.NameAR ?? firstItem.NameEN ?? "خدمة");

                    var mfPayload = new Dictionary<string, object>
                    {
                        { "NotificationOption",   "LNK" },
                        { "InvoiceValue",          chargeAmt },
                        { "CustomerName",          custName },
                        { "DisplayCurrencyIso",    currency },
                        { "CallBackUrl",           callbackUrl },
                        { "ErrorUrl",              errorUrl },
                        { "Language",              custLang == "en" ? "EN" : "AR" },
                        { "CustomerReference",    $"APT-{appointmentId}" },
                        { "InvoiceItems", new[]
                            {
                                new Dictionary<string, object>
                                {
                                    { "ItemName",  isDeposit ? $"{itemLabel} (Deposit)" : itemLabel },
                                    { "Quantity",  1 },
                                    { "UnitPrice", chargeAmt }
                                }
                            }
                        }
                    };

                    if (!string.IsNullOrWhiteSpace(custEmail) && custEmail.Contains("@"))
                        mfPayload["CustomerEmail"] = custEmail;

                    var (cc, mn) = SplitPhone(custPhone);
                    if (!string.IsNullOrWhiteSpace(mn) && mn.Length >= 8)
                    {
                        mfPayload["CustomerMobile"] = mn;
                        mfPayload["MobileCountryCode"] = cc;
                    }

                    var mfClient = httpClientFactory.CreateClient();
                    mfClient.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", (string)mfConfig.ApiKey);
                    mfClient.DefaultRequestHeaders.Accept.Add(
                        new MediaTypeWithQualityHeaderValue("application/json"));

                    var mfJson = JsonSerializer.Serialize(mfPayload);
                    var mfContent = new StringContent(mfJson, Encoding.UTF8, "application/json");
                    var mfResp = await mfClient.PostAsync(
                        $"{(string)mfConfig.ApiBaseUrl}/v2/SendPayment", mfContent);
                    var mfBody = await mfResp.Content.ReadAsStringAsync();

                    var mfResult = JsonSerializer.Deserialize<JsonElement>(mfBody);
                    bool isSuccess = mfResult.TryGetProperty("IsSuccess", out var s) && s.GetBoolean();

                    if (isSuccess &&
                        mfResult.TryGetProperty("Data", out var data) &&
                        data.TryGetProperty("InvoiceURL", out var urlProp))
                    {
                        paymentUrl = urlProp.GetString();
                        string invoiceId = data.TryGetProperty("InvoiceId", out var inv)
                            ? inv.ToString() : "";

                        conn.Execute(@"
                            INSERT INTO dbo.MYFATOORAH_TRANSACTIONS (
                                AppointmentId, InvoiceId, InvoiceURL, Amount, Currency,
                                Status, CustomerName, CustomerPhone, CustomerEmail,
                                RawResponse, CreatedAt
                            )
                            VALUES (
                                @AppointmentId, @InvoiceId, @InvoiceURL, @Amount, @Currency,
                                'pending', @CustomerName, @CustomerPhone, @CustomerEmail,
                                @RawResponse, SYSUTCDATETIME()
                            )",
                            new
                            {
                                AppointmentId = appointmentId,
                                InvoiceId = invoiceId,
                                InvoiceURL = paymentUrl,
                                Amount = chargeAmt,
                                Currency = currency,
                                CustomerName = custName,
                                CustomerPhone = custPhone,
                                CustomerEmail = custEmail,
                                RawResponse = mfBody
                            });
                    }
                }
                catch
                {
                    // Payment initiation failed — booking still created
                }
            }

            // ── Send WhatsApp notifications (fire-and-forget) ─────────────────────
            // حفظ الـ appointmentId في variable محلي منفصل
            var aptIdForWa = appointmentId;
            _ = Task.Run(async () =>
            {
                try
                {
                    // افتح connection جديدة مستقلة داخل الـ Task
                    using var waConn = sqlConnections.NewByKey("Default");
                    await SendBookingWhatsAppAsync(aptIdForWa, waConn);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsApp Booking Error] {ex.Message}");
                }
            });

            return Ok(new ApiResultBilingual<OnlineBookingResultDto>(
                true, null,
                new OnlineBookingResultDto(
                    AppointmentId: appointmentId,
                    Status: "scheduled",
                    PaymentUrl: paymentUrl,
                    TotalPrice: totalPrice,
                    DepositAmount: totalDeposit,
                    IsDepositPayment: isDeposit),
                "تم إنشاء الحجز بنجاح",
                "Booking created successfully"));
        }

        // ──────────────────────────────────────────────────────────────
        // GET /api/online/bookings
        // List current customer's bookings (IsOnlineBooking = 1)
        // ──────────────────────────────────────────────────────────────
        [HttpGet("bookings")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult<ApiResult<List<OnlineCustomerBookingDto>>> GetMyBookings()
        {
            int customerId = GetCurrentCustomerId();
            if (customerId == 0) return Unauthorized();

            using var conn = sqlConnections.NewByKey("Default");

            var list = conn.Query<OnlineCustomerBookingDto>(@"
                SELECT
                    a.Id,
                    ac.ArabicName   AS CategoryArabicName,
                    ac.EnglishName  AS CategoryEnglishName,
                    i.ITEM_NAME2    AS ItemArabicName,
                    i.ITEM_NAME1    AS ItemEnglishName,
                    s.ArabicName    AS StaffArabicName,
                    s.EnglishName   AS StaffEnglishName,
                    b.BRANCH_NAME2  AS BranchArabicName,
                    b.BRANCH_NAME1  AS BranchEnglishName,
                    CONVERT(VARCHAR(10), a.AppointmentDate, 120) AS BookingDate,
                    LEFT(CONVERT(VARCHAR(8), a.StartTime, 108), 5) AS StartTime,
                    LEFT(CONVERT(VARCHAR(8), a.EndTime,   108), 5) AS EndTime,
                    a.ServiceType,
                    a.Status,
                    a.TotalPrice,
                    a.PaidAmount,
                    a.DepositAmount,
                    a.PaymentStatus,
                    mf.InvoiceURL   AS PaymentUrl,
                    a.NumberOfPersons,
                    a.Notes,
                    a.IsOnlineBooking,
                    a.CreatedAt
                FROM dbo.AppointmentData a
                INNER JOIN dbo.ITEM i
                    ON i.ITEM_ID = a.ItemId
                INNER JOIN dbo.AppointmentCategories ac
                    ON ac.Id = i.AppointmentCategoryId
                INNER JOIN dbo.STAFF s
                    ON s.Id = a.StaffId
                INNER JOIN dbo.BRANCH b
                    ON b.BRANCH_ID = a.BranchId
                LEFT JOIN dbo.MYFATOORAH_TRANSACTIONS mf
                    ON mf.AppointmentId = a.Id
                   AND mf.Status = 'pending'
                WHERE a.CustomerId    = @CustomerId
                  AND a.IsOnlineBooking = 1
                ORDER BY a.CreatedAt DESC",
                new { CustomerId = customerId }).ToList();

            return Ok(new ApiResult<List<OnlineCustomerBookingDto>>(true, null, list));
        }

        // ──────────────────────────────────────────────────────────────
        // GET /api/online/bookings/{id}
        // ──────────────────────────────────────────────────────────────
        [HttpGet("bookings/{id:int}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult<ApiResult<OnlineCustomerBookingDto>> GetBookingById(int id)
        {
            int customerId = GetCurrentCustomerId();
            if (customerId == 0) return Unauthorized();

            using var conn = sqlConnections.NewByKey("Default");

            var booking = conn.Query<OnlineCustomerBookingDto>(@"
                SELECT
                    a.Id,
                    ac.ArabicName   AS CategoryArabicName,
                    ac.EnglishName  AS CategoryEnglishName,
                    i.ITEM_NAME2    AS ItemArabicName,
                    i.ITEM_NAME1    AS ItemEnglishName,
                    s.ArabicName    AS StaffArabicName,
                    s.EnglishName   AS StaffEnglishName,
                    b.BRANCH_NAME2  AS BranchArabicName,
                    b.BRANCH_NAME1  AS BranchEnglishName,
                    CONVERT(VARCHAR(10), a.AppointmentDate, 120) AS BookingDate,
                    LEFT(CONVERT(VARCHAR(8), a.StartTime, 108), 5) AS StartTime,
                    LEFT(CONVERT(VARCHAR(8), a.EndTime,   108), 5) AS EndTime,
                    a.ServiceType,
                    a.Status,
                    a.TotalPrice,
                    a.PaidAmount,
                    a.DepositAmount,
                    a.PaymentStatus,
                    mf.InvoiceURL   AS PaymentUrl,
                    a.NumberOfPersons,
                    a.Notes,
                    a.IsOnlineBooking,
                    a.CreatedAt
                FROM dbo.AppointmentData a
                INNER JOIN dbo.ITEM i  ON i.ITEM_ID  = a.ItemId
                INNER JOIN dbo.AppointmentCategories ac ON ac.Id = i.AppointmentCategoryId
                INNER JOIN dbo.STAFF s ON s.Id = a.StaffId
                INNER JOIN dbo.BRANCH b ON b.BRANCH_ID = a.BranchId
                LEFT JOIN dbo.MYFATOORAH_TRANSACTIONS mf
                    ON mf.AppointmentId = a.Id AND mf.Status = 'pending'
                WHERE a.Id = @Id
                  AND a.CustomerId = @CustomerId
                  AND a.IsOnlineBooking = 1",
                new { Id = id, CustomerId = customerId }).FirstOrDefault();

            if (booking == null)
                return Ok(new ApiResult<OnlineCustomerBookingDto>(false, "Booking not found", null));

            return Ok(new ApiResult<OnlineCustomerBookingDto>(true, null, booking));
        }

        // ──────────────────────────────────────────────────────────────
        // PATCH /api/online/bookings/{id}/cancel
        // ──────────────────────────────────────────────────────────────
        [HttpPatch("bookings/{id:int}/cancel")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult<ApiResultBilingual<object>> CancelBooking(int id)
        {
            int customerId = GetCurrentCustomerId();
            if (customerId == 0) return Unauthorized();

            using var conn = sqlConnections.NewByKey("Default");

            var apt = conn.Query<dynamic>(@"
                SELECT Id, Status, CheckoutStatus
                FROM dbo.AppointmentData
                WHERE Id = @Id AND CustomerId = @CustomerId AND IsOnlineBooking = 1",
                new { Id = id, CustomerId = customerId }).FirstOrDefault();

            if (apt == null)
                return Ok(new ApiResultBilingual<object>(
                    false, null, null, "الحجز غير موجود", "Booking not found"));

            if ((string)apt.Status == "cancelled")
                return Ok(new ApiResultBilingual<object>(
                    false, null, null,
                    "الحجز ملغى بالفعل", "Booking already cancelled"));

            if ((string)apt.CheckoutStatus == "checked_out")
                return Ok(new ApiResultBilingual<object>(
                    false, null, null,
                    "لا يمكن إلغاء حجز تم إنهاؤه",
                    "Cannot cancel a checked-out booking"));

            conn.Execute(@"
                UPDATE dbo.AppointmentData
                SET Status = 'cancelled', UpdatedAt = SYSUTCDATETIME()
                WHERE Id = @Id",
                new { Id = id });

            return Ok(new ApiResultBilingual<object>(
                true, null, new { CancelledId = id },
                "تم إلغاء الحجز", "Booking cancelled"));
        }

        // ══════════════════════════════════════════════════════════════
        // REGION: PROFILE
        // ══════════════════════════════════════════════════════════════

        // ──────────────────────────────────────────────────────────────
        // GET /api/online/profile
        // ──────────────────────────────────────────────────────────────
        [HttpGet("profile")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult<ApiResult<OnlineUserDto>> GetProfile()
        {
            int customerId = GetCurrentCustomerId();
            if (customerId == 0) return Unauthorized();

            using var conn = sqlConnections.NewByKey("Default");

            var c = conn.Query<dynamic>(@"
                SELECT
                    CUSTOMER_ID           AS CustomerId,
                    CUSTOMER_NAME         AS FullName,
                    ExternalEmail         AS Email,
                    CUSTOMER_PHONE1       AS CustomerPhone,
                    ExternalGender        AS Gender,
                    BRANCH_ID             AS BranchId,
                    CAST(CUSTOMER_REF_GUIDE AS VARCHAR(36)) AS CustomerRef,
                    ISNULL(NotificationLang,'ar')            AS NotificationLang
                FROM dbo.CUSTOMER
                WHERE CUSTOMER_ID = @Id AND IsExternalUser = 1",
                new { Id = customerId }).FirstOrDefault();

            if (c == null)
                return Ok(new ApiResult<OnlineUserDto>(false, "Profile not found", null));

            var dto = new OnlineUserDto(
                CustomerId: (int)c.CustomerId,
                FullName: (string)(c.FullName ?? ""),
                Email: (string?)c.Email,
                CustomerPhone: (string)(c.CustomerPhone ?? ""),
                Gender: (string?)c.Gender,
                BranchId: (int)c.BranchId,
                CustomerRef: (string)(c.CustomerRef ?? ""),
                NotificationLang: (string)(c.NotificationLang ?? "ar"));

            return Ok(new ApiResult<OnlineUserDto>(true, null, dto));
        }

        // ──────────────────────────────────────────────────────────────
        // PUT /api/online/profile
        // ──────────────────────────────────────────────────────────────
        [HttpPut("profile")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult<ApiResultBilingual<OnlineUserDto>> UpdateProfile(
            [FromBody] UpdateProfileDto request)
        {
            if (request == null)
                return Ok(new ApiResultBilingual<OnlineUserDto>(
                    false, "Request required", null,
                    "البيانات مطلوبة", "Request required"));

            int customerId = GetCurrentCustomerId();
            if (customerId == 0) return Unauthorized();

            using var conn = sqlConnections.NewByKey("Default");
            var updates = new List<string>();
            var p = new Dapper.DynamicParameters();
            p.Add("Id", customerId);

            if (!string.IsNullOrWhiteSpace(request.FullName))
            {
                updates.Add("CUSTOMER_NAME = @FullName");
                p.Add("FullName", request.FullName.Trim());
            }
            if (!string.IsNullOrWhiteSpace(request.Email))
            {
                // Duplicate check
                var dup = conn.Query<int?>(
                    "SELECT CUSTOMER_ID FROM dbo.CUSTOMER WHERE ExternalEmail = @Email AND CUSTOMER_ID != @Id",
                    new { Email = request.Email.Trim(), Id = customerId }).FirstOrDefault();
                if (dup != null)
                    return Ok(new ApiResultBilingual<OnlineUserDto>(
                        false, null, null,
                        "البريد الإلكتروني مستخدم بالفعل",
                        "Email already in use"));

                updates.Add("ExternalEmail = @Email");
                p.Add("Email", request.Email.Trim());
            }
            if (!string.IsNullOrWhiteSpace(request.Gender))
            {
                updates.Add("ExternalGender = @Gender");
                p.Add("Gender", request.Gender == "Female" ? "Female" : "Male");
            }
            if (!string.IsNullOrWhiteSpace(request.NotificationLang))
            {
                var lang = request.NotificationLang.ToLower() == "en" ? "en" : "ar";
                updates.Add("NotificationLang = @Lang");
                p.Add("Lang", lang);
            }

            if (updates.Count > 0)
                conn.Execute(
                    $"UPDATE dbo.CUSTOMER SET {string.Join(", ", updates)} WHERE CUSTOMER_ID = @Id", p);

            // Return updated profile
            var profileResult = GetProfile();
            if (profileResult.Result is OkObjectResult { Value: ApiResult<OnlineUserDto> r })
            {
                return Ok(new ApiResultBilingual<OnlineUserDto>(
                    r.Success, r.Error, r.Data,
                    "تم تحديث الملف الشخصي", "Profile updated"));
            }

            return Ok(new ApiResultBilingual<OnlineUserDto>(
                true, null, null,
                "تم التحديث", "Updated"));
        }

        // ──────────────────────────────────────────────────────────────
        // POST /api/online/profile/change-password
        // ──────────────────────────────────────────────────────────────
        [HttpPost("profile/change-password")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult<ApiResultBilingual<object>> ChangePassword(
            [FromBody] ChangePasswordDto request)
        {
            if (request == null)
                return Ok(new ApiResultBilingual<object>(
                    false, "Request required", null,
                    "البيانات مطلوبة", "Request required"));

            if (request.NewPassword != request.ConfirmNewPassword)
                return Ok(new ApiResultBilingual<object>(
                    false, null, null,
                    "كلمات المرور الجديدة غير متطابقة",
                    "New passwords do not match"));

            if (request.NewPassword.Length < 6)
                return Ok(new ApiResultBilingual<object>(
                    false, null, null,
                    "كلمة المرور يجب أن تكون 6 أحرف على الأقل",
                    "Password must be at least 6 characters"));

            int customerId = GetCurrentCustomerId();
            if (customerId == 0) return Unauthorized();

            using var conn = sqlConnections.NewByKey("Default");

            var stored = conn.Query<string?>(
                "SELECT ExternalPassword FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id AND IsExternalUser = 1",
                new { Id = customerId }).FirstOrDefault();

            if (stored == null || !VerifyPassword(request.CurrentPassword, stored))
                return Ok(new ApiResultBilingual<object>(
                    false, null, null,
                    "كلمة المرور الحالية غير صحيحة",
                    "Current password is incorrect"));

            var newHash = HashPassword(request.NewPassword);
            conn.Execute(
                "UPDATE dbo.CUSTOMER SET ExternalPassword = @Hash WHERE CUSTOMER_ID = @Id",
                new { Hash = newHash, Id = customerId });

            return Ok(new ApiResultBilingual<object>(
                true, null, new { Changed = true },
                "تم تغيير كلمة المرور بنجاح",
                "Password changed successfully"));
        }

        // ══════════════════════════════════════════════════════════════
        // REGION: ADDRESSES
        // ══════════════════════════════════════════════════════════════

        // ──────────────────────────────────────────────────────────────
        // GET /api/online/addresses
        // ──────────────────────────────────────────────────────────────
        [HttpGet("addresses")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult<ApiResult<List<OnlineAddressDto>>> GetAddresses()
        {
            int customerId = GetCurrentCustomerId();
            if (customerId == 0) return Unauthorized();

            using var conn = sqlConnections.NewByKey("Default");
            var refGuide = GetCustomerRef(conn, customerId);
            if (refGuide == Guid.Empty)
                return Ok(new ApiResult<List<OnlineAddressDto>>(false, "Customer not found", null));

            var list = LoadAddresses(conn, refGuide);
            return Ok(new ApiResult<List<OnlineAddressDto>>(true, null, list));
        }

        // ──────────────────────────────────────────────────────────────
        // POST /api/online/addresses
        // ──────────────────────────────────────────────────────────────
        [HttpPost("addresses")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult<ApiResultBilingual<OnlineAddressDto>> AddAddress(
            [FromBody] UpsertAddressDto request)
        {
            if (request == null || request.AreaId <= 0)
                return Ok(new ApiResultBilingual<OnlineAddressDto>(
                    false, null, null, "المنطقة مطلوبة", "Area is required"));

            int customerId = GetCurrentCustomerId();
            if (customerId == 0) return Unauthorized();

            using var conn = sqlConnections.NewByKey("Default");
            var refGuide = GetCustomerRef(conn, customerId);
            if (refGuide == Guid.Empty)
                return Ok(new ApiResultBilingual<OnlineAddressDto>(
                    false, null, null, "العميل غير موجود", "Customer not found"));

            var area = conn.Query<dynamic>(
                "SELECT AREA_ID FROM dbo.GOVERNORATE_AREA WHERE AREA_ID = @Id",
                new { Id = request.AreaId }).FirstOrDefault();
            if (area == null)
                return Ok(new ApiResultBilingual<OnlineAddressDto>(
                    false, null, null, "المنطقة غير موجودة", "Area not found"));

            if (request.MakeDefault)
                conn.Execute(
                    "UPDATE dbo.CUSTOMER_ADRESS SET DEFAULT_ADDRESS = 0 WHERE CUSTOMER_REF = @Ref",
                    new { Ref = refGuide });

            var existCount = conn.Query<int>(
                "SELECT COUNT(*) FROM dbo.CUSTOMER_ADRESS WHERE CUSTOMER_REF = @Ref",
                new { Ref = refGuide }).FirstOrDefault();
            bool isDefault = request.MakeDefault || existCount == 0;

            var maxId = conn.Query<int?>("SELECT MAX(CUSTOMER_ADRESS_ID) FROM dbo.CUSTOMER_ADRESS")
                .FirstOrDefault();
            int newId = (maxId ?? 0) + 1;
            int systemUserId = GetSystemUserId(conn);
            conn.Execute(@"
                INSERT INTO dbo.CUSTOMER_ADRESS (
                    CUSTOMER_ADRESS_ID, CUSTOMER_REF, CREATED_BY, CREATED_DATE, AREA_ID,
                    BLOCK_NO, STREET, AVENUE, BUILDING_NO, FLAT_NO,
                    Floor, NOTE, Location, DEFAULT_ADDRESS
                )
                VALUES (
                    @Id, @Ref, @CreatedBy, SYSUTCDATETIME(), @AreaId,
                    @Block, @Street, @Avenue, @Building, @Flat,
                    @Floor, @Note, @Link, @IsDefault
                )",
                new
                {
                    Id = newId,
                    Ref = refGuide,
                    CreatedBy = systemUserId,
                    AreaId = request.AreaId,
                    Block = request.BlockNo,
                    Street = request.Street,
                    Avenue = request.Avenue,
                    Building = request.BuildingNo,
                    Flat = request.FlatNo,
                    Floor = request.Floor,
                    Note = request.Note,
                    Link = request.AddressLink,
                    IsDefault = isDefault ? 1 : 0
                });

            var created = LoadAddressById(conn, newId);
            return Ok(new ApiResultBilingual<OnlineAddressDto>(
                true, null, created, "تم إضافة العنوان", "Address added"));
        }

        // ──────────────────────────────────────────────────────────────
        // PUT /api/online/addresses/{id}
        // ──────────────────────────────────────────────────────────────
        [HttpPut("addresses/{id:int}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult<ApiResultBilingual<OnlineAddressDto>> UpdateAddress(
            int id, [FromBody] UpsertAddressDto request)
        {
            if (request == null)
                return Ok(new ApiResultBilingual<OnlineAddressDto>(
                    false, "Request required", null,
                    "البيانات مطلوبة", "Request required"));

            int customerId = GetCurrentCustomerId();
            if (customerId == 0) return Unauthorized();

            using var conn = sqlConnections.NewByKey("Default");
            var refGuide = GetCustomerRef(conn, customerId);
            if (refGuide == Guid.Empty)
                return Ok(new ApiResultBilingual<OnlineAddressDto>(
                    false, null, null, "العميل غير موجود", "Customer not found"));

            // Ensure address belongs to this customer
            var existing = conn.Query<dynamic>(
                "SELECT CUSTOMER_ADRESS_ID FROM dbo.CUSTOMER_ADRESS WHERE CUSTOMER_ADRESS_ID = @Id AND CUSTOMER_REF = @Ref",
                new { Id = id, Ref = refGuide }).FirstOrDefault();
            if (existing == null)
                return Ok(new ApiResultBilingual<OnlineAddressDto>(
                    false, null, null, "العنوان غير موجود", "Address not found"));

            if (request.MakeDefault)
                conn.Execute(
                    "UPDATE dbo.CUSTOMER_ADRESS SET DEFAULT_ADDRESS = 0 WHERE CUSTOMER_REF = @Ref",
                    new { Ref = refGuide });

            conn.Execute(@"
                UPDATE dbo.CUSTOMER_ADRESS SET
                    AREA_ID         = @AreaId,
                    BLOCK_NO        = @Block,
                    STREET          = @Street,
                    AVENUE          = @Avenue,
                    BUILDING_NO     = @Building,
                    FLAT_NO         = @Flat,
                    Floor           = @Floor,
                    NOTE            = @Note,
                    Location        = @Link,
                    DEFAULT_ADDRESS = @IsDefault
                WHERE CUSTOMER_ADRESS_ID = @Id",
                new
                {
                    Id = id,
                    AreaId = request.AreaId,
                    Block = request.BlockNo,
                    Street = request.Street,
                    Avenue = request.Avenue,
                    Building = request.BuildingNo,
                    Flat = request.FlatNo,
                    Floor = request.Floor,
                    Note = request.Note,
                    Link = request.AddressLink,
                    IsDefault = request.MakeDefault ? 1 : 0
                });

            var updated = LoadAddressById(conn, id);
            return Ok(new ApiResultBilingual<OnlineAddressDto>(
                true, null, updated, "تم تحديث العنوان", "Address updated"));
        }

        // ──────────────────────────────────────────────────────────────
        // DELETE /api/online/addresses/{id}
        // ──────────────────────────────────────────────────────────────
        [HttpDelete("addresses/{id:int}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public ActionResult<ApiResultBilingual<object>> DeleteAddress(int id)
        {
            int customerId = GetCurrentCustomerId();
            if (customerId == 0) return Unauthorized();

            using var conn = sqlConnections.NewByKey("Default");
            var refGuide = GetCustomerRef(conn, customerId);
            if (refGuide == Guid.Empty)
                return Ok(new ApiResultBilingual<object>(
                    false, null, null, "العميل غير موجود", "Customer not found"));

            var rows = conn.Execute(
                "DELETE FROM dbo.CUSTOMER_ADRESS WHERE CUSTOMER_ADRESS_ID = @Id AND CUSTOMER_REF = @Ref",
                new { Id = id, Ref = refGuide });

            if (rows == 0)
                return Ok(new ApiResultBilingual<object>(
                    false, null, null, "العنوان غير موجود", "Address not found"));

            return Ok(new ApiResultBilingual<object>(
                true, null, new { DeletedId = id },
                "تم حذف العنوان", "Address deleted"));
        }

        // ══════════════════════════════════════════════════════════════
        // PRIVATE HELPERS
        // ══════════════════════════════════════════════════════════════
        private int GetSystemUserId(IDbConnection conn)
        {
            var userId = conn.Query<int?>(@"
                SELECT TOP 1 USER_ID FROM dbo.[USER] 
                WHERE USER_NAME = 'system' OR USER_NAME = 'online' OR USER_NAME = 'admin'
                ORDER BY USER_ID").FirstOrDefault();

            return userId ?? 1;
        }
        private Guid GetCustomerRef(IDbConnection conn, int customerId)
        {
            var row = conn.Query<dynamic>(
                "SELECT CUSTOMER_REF_GUIDE AS Ref FROM dbo.CUSTOMER WHERE CUSTOMER_ID = @Id",
                new { Id = customerId }).FirstOrDefault();
            return row == null ? Guid.Empty : (Guid)row.Ref;
        }

        private List<OnlineAddressDto> LoadAddresses(IDbConnection conn, Guid refGuide)
        {
            return conn.Query<OnlineAddressDto>(@"
                SELECT
                    ca.CUSTOMER_ADRESS_ID   AS AddressId,
                    ca.AREA_ID              AS AreaId,
                    ga.AREA_NAME2           AS AreaNameAR,
                    ga.AREA_NAME1           AS AreaNameEN,
                    ga.GOVERNORATE_ID       AS GovernorateId,
                    g.GOVERNORATE_NAME2     AS GovernorateNameAR,
                    g.GOVERNORATE_NAME1     AS GovernorateNameEN,
                    ca.BLOCK_NO             AS BlockNo,
                    ca.STREET               AS Street,
                    ca.AVENUE               AS Avenue,
                    ca.BUILDING_NO          AS BuildingNo,
                    ca.FLAT_NO              AS FlatNo,
                    ca.Floor                AS Floor,
                    ca.NOTE                 AS Note,
                    ca.Location             AS Location,
                    CAST(CASE WHEN ca.DEFAULT_ADDRESS = 1 THEN 1 ELSE 0 END AS BIT) AS IsDefault
                FROM dbo.CUSTOMER_ADRESS ca
                LEFT JOIN dbo.GOVERNORATE_AREA ga ON ga.AREA_ID = ca.AREA_ID
                LEFT JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = ga.GOVERNORATE_ID
                WHERE ca.CUSTOMER_REF = @Ref
                ORDER BY ca.DEFAULT_ADDRESS DESC, ca.CREATED_DATE DESC",
                new { Ref = refGuide }).ToList();
        }

        private OnlineAddressDto? LoadAddressById(IDbConnection conn, int id)
        {
            return conn.Query<OnlineAddressDto>(@"
                SELECT
                    ca.CUSTOMER_ADRESS_ID   AS AddressId,
                    ca.AREA_ID              AS AreaId,
                    ga.AREA_NAME2           AS AreaNameAR,
                    ga.AREA_NAME1           AS AreaNameEN,
                    ga.GOVERNORATE_ID       AS GovernorateId,
                    g.GOVERNORATE_NAME2     AS GovernorateNameAR,
                    g.GOVERNORATE_NAME1     AS GovernorateNameEN,
                    ca.BLOCK_NO             AS BlockNo,
                    ca.STREET               AS Street,
                    ca.AVENUE               AS Avenue,
                    ca.BUILDING_NO          AS BuildingNo,
                    ca.FLAT_NO              AS FlatNo,
                    ca.Floor                AS Floor,
                    ca.NOTE                 AS Note,
                    ca.Location             AS Location,
                    CAST(CASE WHEN ca.DEFAULT_ADDRESS = 1 THEN 1 ELSE 0 END AS BIT) AS IsDefault
                FROM dbo.CUSTOMER_ADRESS ca
                LEFT JOIN dbo.GOVERNORATE_AREA ga ON ga.AREA_ID = ca.AREA_ID
                LEFT JOIN dbo.GOVERNORATE g ON g.GOVERNORATE_ID = ga.GOVERNORATE_ID
                WHERE ca.CUSTOMER_ADRESS_ID = @Id",
                new { Id = id }).FirstOrDefault();
        }

        private static (string CountryCode, string MobileNumber) SplitPhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return ("", "");
            var c = new string(phone.Where(char.IsDigit).ToArray());
            if (c.StartsWith("965") && c.Length >= 11) return ("+965", c.Substring(3));
            if (c.StartsWith("20") && c.Length >= 12) return ("+20", c.Substring(2));
            if (c.StartsWith("966") && c.Length >= 12) return ("+966", c.Substring(3));
            if (c.StartsWith("971") && c.Length >= 12) return ("+971", c.Substring(3));
            if (c.StartsWith("973") && c.Length >= 11) return ("+973", c.Substring(3));
            if (c.Length == 8) return ("+965", c);
            return ($"+{c[..3]}", c[3..]);
        }
        // ══════════════════════════════════════════════════════════════
        // WhatsApp: Send a notification to Staff and Admin when a booking is created
        // ══════════════════════════════════════════════════════════════
        private async Task SendBookingWhatsAppAsync(int appointmentId, IDbConnection conn)
        {
            // ── 1. جلب تفاصيل الحجز ──────────────────────────────────
            var apt = conn.Query<dynamic>(@"
        SELECT
            a.Id,
            a.AppointmentDate,
            LEFT(CONVERT(VARCHAR(8), a.StartTime, 108), 5) AS StartTime,
            LEFT(CONVERT(VARCHAR(8), a.EndTime,   108), 5) AS EndTime,
            a.ServiceType,
            a.TotalPrice,
            a.Notes,
            c.CUSTOMER_NAME  AS CustomerName,
            c.CUSTOMER_PHONE1 AS CustomerPhone,
            s.ArabicName     AS StaffArabicName,
            s.EnglishName    AS StaffEnglishName,
            s.Mobile         AS StaffMobile,
            b.BRANCH_NAME1   AS BranchName,
            i.ITEM_NAME1     AS ServiceNameEN,
            i.ITEM_NAME2     AS ServiceNameAR
        FROM dbo.AppointmentData a
        INNER JOIN dbo.CUSTOMER c  ON c.CUSTOMER_ID  = a.CustomerId
        INNER JOIN dbo.STAFF    s  ON s.Id           = a.StaffId
        INNER JOIN dbo.BRANCH   b  ON b.BRANCH_ID    = a.BranchId
        INNER JOIN dbo.ITEM     i  ON i.ITEM_ID      = a.ItemId
        WHERE a.Id = @Id",
                new { Id = appointmentId }).FirstOrDefault();

            if (apt == null) return;

            string waApiKey = configuration["WhatsApp:ApiKey"] ?? "";
            string waApiUrl = configuration["WhatsApp:ApiUrl"] ?? "https://api.ultramsg.com";
            string waInstance = configuration["WhatsApp:InstanceId"] ?? "";

            if (string.IsNullOrWhiteSpace(waApiKey)) return;

            string date = ((DateTime)apt.AppointmentDate).ToString("yyyy-MM-dd");
            string startTime = (string)apt.StartTime;
            string endTime = (string)apt.EndTime;
            string customer = (string)apt.CustomerName;
            string custPhone = (string)apt.CustomerPhone;
            string service = (string)apt.ServiceNameAR;
            string branch = (string)apt.BranchName;
            decimal price = (decimal)apt.TotalPrice;
            string notes = string.IsNullOrWhiteSpace((string?)apt.Notes) ? "-" : (string)apt.Notes;

            // ── 2. رسالة الـ Staff ───────────────────────────────────
            string? staffMobile = (string?)apt.StaffMobile;
            if (!string.IsNullOrWhiteSpace(staffMobile))
            {
                string staffMsg =
                    $"╔══════════════════════╗\n" +
                    $"       📅 حجز جديد\n" +
                    $"╚══════════════════════╝\n\n" +
                    $"👤 العميل : {customer}\n" +
                    $"📞 الهاتف : {custPhone}\n" +
                    $"💆 الخدمة : {service}\n" +
                    $"🗓️ التاريخ : {date}\n" +
                    $"⏰ الوقت  : {startTime} - {endTime}\n" +
                    $"🏠 الفرع  : {branch}\n" +
                    $"💰 السعر  : {price} KWD\n" +
                    (notes == "-" ? "" : $"📝 ملاحظات : {notes}\n");

                await SendWhatsAppMessageAsync(waApiUrl, waInstance, waApiKey, staffMobile, staffMsg);
            }

            // ── 3. رسالة الـ Admin ───────────────────────────────────
            string? adminMobile = configuration["OnlineBooking:AdminWhatsAppMobile"];
            if (!string.IsNullOrWhiteSpace(adminMobile))
            {
                string staffName = (string)apt.StaffArabicName;
                string adminMsg =
                    $"╔══════════════════════╗\n" +
                    $"   🔔 حجز أونلاين جديد\n" +
                    $"╚══════════════════════╝\n\n" +
                    $"👤 العميل  : {customer}\n" +
                    $"📞 الهاتف  : {custPhone}\n" +
                    $"💆 الخدمة  : {service}\n" +
                    $"👩‍💼 الموظفة : {staffName}\n" +
                    $"🗓️ التاريخ  : {date}\n" +
                    $"⏰ الوقت   : {startTime} - {endTime}\n" +
                    $"🏠 الفرع   : {branch}\n" +
                    $"💰 السعر   : {price} KWD\n" +
                    (notes == "-" ? "" : $"📝 ملاحظات : {notes}\n") +
                    $"\n🔗 رقم الحجز: #{appointmentId}";

                await SendWhatsAppMessageAsync(waApiUrl, waInstance, waApiKey, adminMobile, adminMsg);
            }
        }

        private async Task SendWhatsAppMessageAsync(
    string apiUrl, string instanceId, string token, string mobile, string message)
        {
            try
            {
                var digits = new string(mobile.Where(char.IsDigit).ToArray());
                if (string.IsNullOrWhiteSpace(digits)) return;
                if (digits.Length == 8) digits = "965" + digits;

                // ── استخدم Enjazatik نفس طريقة OTP ──────────────────────
                var waConfig = sqlConnections.NewByKey("Default")
                    .Query<dynamic>(
                        "SELECT TOP 1 InstanceId, IsEnabled FROM dbo.WHATSAPP_CONFIG ORDER BY Id")
                    .FirstOrDefault();

                if (waConfig == null || !(bool)waConfig.IsEnabled) return;

                var client = httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);

                var payload = new
                {
                    instance_id = (string)waConfig.InstanceId,
                    message = message,
                    number = digits
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(EnjazatikUrl, content);

                var body = await resp.Content.ReadAsStringAsync();
                Debug.WriteLine($"[WA Booking Send] {mobile}: {resp.StatusCode} — {body}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WA Send Error] {mobile}: {ex.Message}");
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    // IN-MEMORY OTP CACHE (simple, no extra table needed)
    // For production: replace with IMemoryCache or Redis
    // ════════════════════════════════════════════════════════════════
    internal static class OtpCache
    {
        private static readonly ConcurrentDictionary<string, (string Otp, DateTime Expiry, bool Verified)>
            _store = new();

        public static void Set(string mobile, (string Otp, DateTime Expiry) entry)
            => _store[mobile] = (entry.Otp, entry.Expiry, false);

        public static bool TryGet(string mobile, out (string Otp, DateTime Expiry) result)
        {
            if (_store.TryGetValue(mobile, out var v) && !v.Verified)
            {
                result = (v.Otp, v.Expiry);
                return true;
            }
            result = default;
            return false;
        }

        public static void SetVerified(string mobile)
        {
            if (_store.TryGetValue(mobile, out var v))
                _store[mobile] = (v.Otp, v.Expiry, true);
        }

        public static bool IsVerified(string mobile)
            => _store.TryGetValue(mobile, out var v) && v.Verified;

        public static void Clear(string mobile)
            => _store.TryRemove(mobile, out _);

    }

}