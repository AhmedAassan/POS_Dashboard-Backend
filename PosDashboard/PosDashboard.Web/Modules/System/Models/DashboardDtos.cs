// Modules/System/Models/DashboardDtos.cs
// UPDATED — adds RefundSummaryDto to DashboardSummaryDto,
//           adds RefundType field to DashboardTransactionDto,
//           and adds 'REFUND' to TransactionType.
//
// INSTRUCTIONS: Replace the existing DashboardDtos.cs entirely with this file.

using System;
using System.Collections.Generic;

namespace PosDashboard.Web.Modules.System.Models
{
    public class DashboardDtos
    {
        public record ApiResult<T>(bool Success, string? Error, T? Data);

        public record DashboardSummaryDto(
            // 2A — Revenue KPIs
            decimal TotalCheckoutRevenue,
            decimal TodayDepositRevenue,
            decimal PendingFromDeposits,
            decimal WalletRevenue,
            decimal PackagesRevenue,
            decimal OnlineFullRevenue,
            decimal TotalEffectiveRevenue,

            // 2B — Payment Breakdown
            List<PaymentTypeBreakdownDto> PaymentTypeBreakdown,

            // 2C — Transaction Log
            List<DashboardTransactionDto> Transactions,

            // 2D — Staff Performance
            List<StaffPerformanceDto> StaffPerformance,

            // 2E — Appointment Stats
            AppointmentStatsDto AppointmentStats,

            // 2F — Service Category Breakdown
            List<ServiceCategoryBreakdownDto> ServiceCategories,

            // 2G — Client Insights
            ClientInsightsDto ClientInsights,

            // 2H — Refund Summary  ← NEW
            RefundSummaryDto? RefundSummary,

            // Meta
            string Currency,
            int WorkdayMinutes,
            int TzOffset,
            DateTime GeneratedAt
        );

        public record PaymentTypeBreakdownDto(
            int PaymentTypeId,
            string PaymentTypeName,
            decimal Amount,
            string? DocumentName
        );

        public record DashboardTransactionDto(
            string TransactionId,
            /// <summary>CHECKOUT | DEPOSIT | WALLET_LOAD | PACKAGE_SALE | REFUND | WALLET_ADJUST</summary>
            string TransactionType,
            string? InvoiceNumber,
            string CustomerName,
            string? StaffName,
            string? ServiceName,
            decimal Amount,
            string PaymentTypeName,
            string Time,
            string Status,
            List<TransactionPaymentBreakdownDto> PaymentBreakdown,
            int? AppointmentId,
            /// <summary>Only populated for TransactionType='REFUND': 'CASH' | 'LINK' | 'WALLET'</summary>
            string? RefundType,
            int? PackageOfferId,
            string? PackageOfferName,
            decimal? PackageOfferPrice,
            /// <summary>True when all invoice lines have been refunded</summary>
            bool IsFullyRefunded,
            /// <summary>True when the invoice was voided (cancelled without refund)</summary>
            bool IsVoid,
            // ── Delivery (CHECKOUT rows only; null everywhere else) ──
            /// <summary>Localised delivery type name ("Delivery" / "Pickup"), null for non-POS rows.</summary>
            string? DeliveryTypeName = null,
            /// <summary>True = delivery, false = pickup, null = not a delivery-aware sale.</summary>
            bool? IsDelivery = null,
            /// <summary>Branch-local delivery date+time (UTC in DB → converted client-side), null if none.</summary>
            DateTime? DeliveryDate = null,
            decimal DeliveryCharge = 0m,

            // ── Wallet settlement (TransactionType='WALLET_ADJUST' only) ──
            /// <summary>'COLLECT' = money came in from an overdrawn wallet.
            /// 'REFUND' = leftover credit paid back out. Null for every other row.</summary>
            string? WalletAdjustType = null,
            /// <summary>Amount written off during the settlement. 0 when nothing was waived.</summary>
            decimal WalletWaivedAmount = 0m,
            /// <summary>The wallet that was settled — lets the row deep-link to /wallet.</summary>
            int? WalletSubscriptionId = null,
            /// <summary>True when the settlement closed the wallet.</summary>
            bool WalletClosed = false
        );

        public record StaffPerformanceDto(
            int StaffId,
            string StaffName,
            string? StaffColor,
            int AppointmentCount,
            int CompletedCount,
            int CancelledCount,
            int NoShowCount,
            int TotalWorkMinutes,
            decimal TotalRevenue,
            decimal Utilization,
            List<StaffClientDto> Clients
        );

        public record StaffClientDto(
            string CustomerName,
            string ServiceName,
            decimal Amount,
            string Time,
            string? InvoiceNumber = null
        );

        public record AppointmentStatsDto(
            int TotalAppointments,
            int CompletedCount,
            int CancelledCount,
            int NoShowCount,
            int ScheduledCount,
            int OnlineBookingCount,
            ServiceTypeCountDto ByServiceType,
            List<HourlyDistributionDto> HourlyDistribution
        );

        public record ServiceTypeCountDto(int SALON, int HOME);

        public record HourlyDistributionDto(int Hour, int Count, string? TopService);

        public record ServiceCategoryBreakdownDto(
            string CategoryName,
            int AppointmentCount,
            decimal Revenue
        );

        public record ClientInsightsDto(
            int NewCustomersToday,
            int ReturningCustomers,
            int VIPCustomers,
            List<TopClientDto> TopClients
        );

        public record TopClientDto(
            string CustomerName,
            decimal TotalSpent,
            int VisitCount
        );

        public record TransactionPaymentBreakdownDto(
            string PaymentTypeName,
            decimal Amount
        );

        // ── NEW ──────────────────────────────────────────────────
        public record RefundSummaryDto(
            int TotalRefunds,
            decimal TotalRefundAmount,
            int CashRefunds,
            int LinkRefunds,
            int WalletRefunds
        );

        /// <summary>Wallet settlement totals for the selected day/range.</summary>
        public record WalletAdjustSummaryDto(
            int TotalAdjustments,
            decimal TotalCollected,
            decimal TotalRefunded,
            decimal TotalWaived,
            int WalletsClosed
        );
    }
}