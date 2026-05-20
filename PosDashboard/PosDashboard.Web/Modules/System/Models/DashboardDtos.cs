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
            /// <summary>CHECKOUT | DEPOSIT | WALLET_LOAD | PACKAGE_SALE | REFUND</summary>
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
            string? RefundType  // ← NEW
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
            string Time
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
    }
}
