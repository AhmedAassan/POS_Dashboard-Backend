using Serenity.Data;
using System;
using System.Data;
using System.Linq;

namespace PosDashboard.Web.Modules.System.Services
{
    /// <summary>
    /// Produces human-readable, gap-free, per-day sequential document numbers in the
    /// ISO-8601-date-based format  <c>{PREFIX}-{yyyyMMdd}-{NNNN}</c>
    /// (e.g. <c>POS-20260702-0001</c>, <c>INV-20260702-0007</c>).
    ///
    /// Design / best practices:
    ///  • The date segment uses the business day (UTC + configured timeZoneOffset),
    ///    so numbering "rolls over" at local midnight, not UTC midnight.
    ///  • Each (Prefix, business-day) pair owns an independent counter that starts at
    ///    0001 and resets automatically on the next business day.
    ///  • The counter is bumped with an atomic MERGE .. WITH (HOLDLOCK) .. OUTPUT so
    ///    concurrent checkouts can never collide or skip a value.
    ///  • Because the increment runs on the SAME connection/transaction as the invoice
    ///    INSERT, a rolled-back checkout also rolls back the counter — keeping the
    ///    sequence gap-free (a core accounting requirement).
    ///
    /// IMPORTANT: always pass the transactional connection (e.g. <c>uow.Connection</c>)
    /// that the invoice row is inserted on.
    /// </summary>
    public static class InvoiceNumberService
    {
        /// <summary>POS (pay-now, off-calendar) sales.</summary>
        public const string PrefixPos = "POS";

        /// <summary>Scheduler booking / New-Sale customer invoices.</summary>
        public const string PrefixInvoice = "INV";

        /// <summary>Package (subscription) assignment invoices.</summary>
        public const string PrefixPackage = "PKG";

        /// <summary>
        /// Atomically reserves and returns the next document number for the given
        /// <paramref name="prefix"/> on the current business day.
        /// </summary>
        public static string Next(IDbConnection conn, string prefix)
            => Next(conn, prefix, BusinessToday(conn));

        /// <summary>
        /// Overload that lets the caller supply an already-resolved business date
        /// (e.g. the <c>saleDate</c> already computed during checkout) to avoid an
        /// extra settings lookup.
        /// </summary>
        public static string Next(IDbConnection conn, string prefix, DateTime businessDate)
        {
            var seqDate = businessDate.Date;

            int next = SqlMapper.Query<int>(conn, @"
                SET NOCOUNT ON;
                MERGE dbo.InvoiceSequences WITH (HOLDLOCK) AS t
                USING (SELECT @Prefix AS Prefix, @SeqDate AS SeqDate) AS s
                    ON  t.Prefix  = s.Prefix
                    AND t.SeqDate = s.SeqDate
                WHEN MATCHED THEN
                    UPDATE SET t.LastNumber = t.LastNumber + 1
                WHEN NOT MATCHED THEN
                    INSERT (Prefix, SeqDate, LastNumber)
                    VALUES (s.Prefix, s.SeqDate, 1)
                OUTPUT INSERTED.LastNumber;",
                new { Prefix = prefix, SeqDate = seqDate }).First();

            // e.g. POS-20260702-0001  (4-digit zero padded; widens gracefully past 9999)
            return $"{prefix}-{seqDate:yyyyMMdd}-{next:D4}";
        }

        /// <summary>
        /// Resolves the current business day = UTC + <c>timeZoneOffset</c> (defaults to +3).
        /// Mirrors the LocalNow(tzOffset) convention used across the checkout controllers.
        /// </summary>
        private static DateTime BusinessToday(IDbConnection conn)
        {
            int tz = SqlMapper.Query<string>(conn,
                "SELECT SETTING_VALUE FROM dbo.SYSTEM_SETTING WHERE SETTING_KEY = 'timeZoneOffset'")
                .Select(v => int.TryParse(v, out var n) ? n : 3)
                .DefaultIfEmpty(3)
                .First();

            return DateTime.UtcNow.AddHours(tz).Date;
        }
    }
}