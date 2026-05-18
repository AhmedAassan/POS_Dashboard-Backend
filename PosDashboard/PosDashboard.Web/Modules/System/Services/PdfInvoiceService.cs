// Modules/System/Services/PdfInvoiceService.cs

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;

namespace PosDashboard.Web.Modules.System.Services
{
    public class InvoiceLineData
    {
        public string ItemName { get; set; } = "";
        public string StaffName { get; set; } = "";
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalPrice { get; set; }
    }

    public class InvoicePdfData
    {
        public string InvoiceNumber { get; set; } = "";
        public DateTime InvoiceDate { get; set; }
        public string CustomerName { get; set; } = "";
        public string CustomerPhone { get; set; } = "";
        public string Currency { get; set; } = "KWD";
        public string CurrencyAr { get; set; } = "د.ك";
        public decimal TotalAmount { get; set; }
        public decimal PaidAmount { get; set; }
        public decimal RemainingAmount { get; set; }
        public string PaymentMethod { get; set; } = "";
        public string PaymentStatus { get; set; } = "";
        public string SalonName { get; set; } = "Glamour Beauty Salon";
        public string SalonNameAr { get; set; } = "صالون جلامور للتجميل";
        public string SalonAddress { get; set; } = "Block 5, Street 12, Kuwait City";
        public string SalonPhone { get; set; } = "+965 2222 3333";
        public string? Notes { get; set; }
        public List<InvoiceLineData> LineItems { get; set; } = new();
    }

    public static class PdfInvoiceService
    {
        static PdfInvoiceService()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public static byte[] GenerateInvoicePdf(InvoicePdfData data)
        {
            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(30);
                    page.DefaultTextStyle(x => x.FontSize(11));

                    page.Header().Element(c => ComposeHeader(c, data));
                    page.Content().Element(c => ComposeContent(c, data));
                    page.Footer().Element(c => ComposeFooter(c, data));
                });
            });

            using var stream = new MemoryStream();
            document.GeneratePdf(stream);
            return stream.ToArray();
        }

        private static void ComposeHeader(IContainer container, InvoicePdfData data)
        {
            container.Column(column =>
            {
                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(data.SalonName)
                            .FontSize(18).Bold().FontColor("#1f2937");
                        col.Item().Text(data.SalonNameAr)
                            .FontSize(14).FontColor("#6b7280");
                        col.Item().Text(data.SalonAddress)
                            .FontSize(9).FontColor("#9ca3af");
                        col.Item().Text($"Tel: {data.SalonPhone}")
                            .FontSize(9).FontColor("#9ca3af");
                    });

                    row.ConstantItem(120).Column(col =>
                    {
                        col.Item().AlignRight().Text("INVOICE")
                            .FontSize(22).Bold().FontColor("#3b82f6");
                        col.Item().AlignRight().Text("فاتورة")
                            .FontSize(14).FontColor("#6b7280");
                    });
                });

                column.Item().PaddingVertical(8)
                    .LineHorizontal(1).LineColor("#e5e7eb");
            });
        }

        private static void ComposeContent(IContainer container, InvoicePdfData data)
        {
            container.PaddingVertical(10).Column(column =>
            {
                // Invoice info
                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text($"Invoice # فاتورة رقم").FontSize(9).FontColor("#6b7280");
                        col.Item().Text(data.InvoiceNumber).Bold().FontSize(12);

                        col.Item().PaddingTop(6)
                            .Text("Date / التاريخ").FontSize(9).FontColor("#6b7280");
                        col.Item().Text(data.InvoiceDate.ToString("dd MMM yyyy, hh:mm tt"))
                            .FontSize(10);
                    });

                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text("Customer / العميل").FontSize(9).FontColor("#6b7280");
                        col.Item().Text(data.CustomerName).Bold().FontSize(12);

                        col.Item().PaddingTop(6)
                            .Text("Phone / الهاتف").FontSize(9).FontColor("#6b7280");
                        col.Item().Text(data.CustomerPhone).FontSize(10);
                    });
                });

                column.Item().PaddingVertical(10)
                    .LineHorizontal(1).LineColor("#e5e7eb");

                // Line items table
                column.Item().Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.ConstantColumn(30);   // #
                        cols.RelativeColumn(3);    // Item
                        cols.RelativeColumn(2);    // Staff
                        cols.ConstantColumn(40);   // Qty
                        cols.ConstantColumn(80);   // Unit Price
                        cols.ConstantColumn(80);   // Total
                    });

                    // Header
                    table.Header(header =>
                    {
                        header.Cell().Background("#f3f4f6").Padding(6)
                            .Text("#").FontSize(9).Bold();
                        header.Cell().Background("#f3f4f6").Padding(6)
                            .Text("Item / الصنف").FontSize(9).Bold();
                        header.Cell().Background("#f3f4f6").Padding(6)
                            .Text("Staff / المختص").FontSize(9).Bold();
                        header.Cell().Background("#f3f4f6").Padding(6).AlignCenter()
                            .Text("Qty").FontSize(9).Bold();
                        header.Cell().Background("#f3f4f6").Padding(6).AlignRight()
                            .Text("Price / السعر").FontSize(9).Bold();
                        header.Cell().Background("#f3f4f6").Padding(6).AlignRight()
                            .Text("Total / المجموع").FontSize(9).Bold();
                    });

                    // Rows
                    for (int i = 0; i < data.LineItems.Count; i++)
                    {
                        var item = data.LineItems[i];
                        var bg = i % 2 == 0 ? "#ffffff" : "#f9fafb";

                        table.Cell().Background(bg).Padding(6)
                            .Text($"{i + 1}").FontSize(9);
                        table.Cell().Background(bg).Padding(6)
                            .Text(item.ItemName).FontSize(10);
                        table.Cell().Background(bg).Padding(6)
                            .Text(item.StaffName).FontSize(9).FontColor("#6b7280");
                        table.Cell().Background(bg).Padding(6).AlignCenter()
                            .Text($"{item.Quantity}").FontSize(10);
                        table.Cell().Background(bg).Padding(6).AlignRight()
                            .Text($"{data.Currency} {item.UnitPrice:F2}").FontSize(10);
                        table.Cell().Background(bg).Padding(6).AlignRight()
                            .Text($"{data.Currency} {item.TotalPrice:F2}").FontSize(10);
                    }
                });

                column.Item().PaddingVertical(8)
                    .LineHorizontal(1).LineColor("#e5e7eb");

                // Totals
                column.Item().AlignRight().PaddingRight(10).Column(totals =>
                {
                    totals.Item().Row(row =>
                    {
                        row.ConstantItem(120).AlignRight()
                            .Text("Subtotal / المجموع").FontSize(10).FontColor("#6b7280");
                        row.ConstantItem(100).AlignRight()
                            .Text($"{data.Currency} {data.TotalAmount:F2}").FontSize(10).Bold();
                    });

                    if (data.PaidAmount > 0)
                    {
                        totals.Item().PaddingTop(4).Row(row =>
                        {
                            row.ConstantItem(120).AlignRight()
                                .Text("Paid / المدفوع").FontSize(10).FontColor("#16a34a");
                            row.ConstantItem(100).AlignRight()
                                .Text($"- {data.Currency} {data.PaidAmount:F2}")
                                .FontSize(10).FontColor("#16a34a").Bold();
                        });
                    }

                    totals.Item().PaddingTop(6)
                        .LineHorizontal(1).LineColor("#e5e7eb");

                    totals.Item().PaddingTop(4).Row(row =>
                    {
                        row.ConstantItem(120).AlignRight()
                            .Text("Balance / الرصيد").FontSize(12).Bold();
                        row.ConstantItem(100).AlignRight()
                            .Text($"{data.Currency} {data.RemainingAmount:F2}")
                            .FontSize(14).Bold()
                            .FontColor(data.RemainingAmount <= 0 ? "#16a34a" : "#dc2626");
                    });
                });

                // Payment info
                if (!string.IsNullOrEmpty(data.PaymentMethod))
                {
                    column.Item().PaddingTop(16)
                        .Background("#f0fdf4").Border(1).BorderColor("#bbf7d0")
                        .Padding(10).Row(row =>
                        {
                            row.AutoItem().Text("✅ ").FontSize(14);
                            row.RelativeItem().Column(col =>
                            {
                                col.Item().Text($"Payment via {data.PaymentMethod}")
                                    .FontSize(10).Bold().FontColor("#166534");
                                col.Item().Text($"Status: {data.PaymentStatus}")
                                    .FontSize(9).FontColor("#166534");
                            });
                        });
                }

                // Notes
                if (!string.IsNullOrEmpty(data.Notes))
                {
                    column.Item().PaddingTop(12).Column(notes =>
                    {
                        notes.Item().Text("Notes / ملاحظات")
                            .FontSize(9).Bold().FontColor("#6b7280");
                        notes.Item().PaddingTop(4).Text(data.Notes)
                            .FontSize(9).FontColor("#374151");
                    });
                }
            });
        }

        private static void ComposeFooter(IContainer container, InvoicePdfData data)
        {
            container.Column(column =>
            {
                column.Item().LineHorizontal(1).LineColor("#e5e7eb");
                column.Item().PaddingTop(8).AlignCenter().Column(footer =>
                {
                    footer.Item().Text("Thank you for your visit!")
                        .FontSize(10).FontColor("#6b7280");
                    footer.Item().Text("شكراً لزيارتكم")
                        .FontSize(10).FontColor("#9ca3af");
                });
            });
        }
    }
}