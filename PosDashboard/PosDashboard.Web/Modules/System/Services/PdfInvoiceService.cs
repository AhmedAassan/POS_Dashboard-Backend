// Modules/System/Services/PdfInvoiceService.cs

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;

namespace PosDashboard.Web.Modules.System.Services
{
    public class InvoiceLineData
    {
        public string ItemName { get; set; } = "";
        public string StaffName { get; set; } = "";
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalPrice { get; set; }
        // POS multi-package grouping: lines sharing a PackageGroupId render as
        // one package block (fixed price). Null = standalone extra service.
        public Guid? PackageGroupId { get; set; }
        public string? PackageOfferName { get; set; }
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
        public string? SalonLogoUrl { get; set; }
        public string? Notes { get; set; }
        public string? FooterLine1 { get; set; }
        public string? FooterLine2 { get; set; }
        public string? FooterLine3 { get; set; }

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
                    // ← Logo + Salon info
                    row.RelativeItem().Row(logoRow =>
                    {
                        // Logo لو موجود
                        if (!string.IsNullOrWhiteSpace(data.SalonLogoUrl))
                        {
                            try
                            {
                                byte[]? logoBytes = null;

                                if (data.SalonLogoUrl.StartsWith("data:image"))
                                {
                                    // Base64 image
                                    var base64 = data.SalonLogoUrl
                                        .Substring(data.SalonLogoUrl.IndexOf(',') + 1);
                                    logoBytes = Convert.FromBase64String(base64);
                                }
                                else
                                {
                                    // URL — حمّله sync
                                    using var http = new HttpClient();
                                    http.Timeout = TimeSpan.FromSeconds(5);
                                    logoBytes = http.GetByteArrayAsync(data.SalonLogoUrl)
                                        .GetAwaiter().GetResult();
                                }

                                if (logoBytes != null)
                                {
                                    logoRow.ConstantItem(60)
                                        .PaddingRight(10)
                                        .AlignMiddle()
                                        .Image(logoBytes)
                                        .FitArea();
                                }
                            }
                            catch
                            {
                                // لو فشل تحميل الـ logo، تجاهله وكمّل
                            }
                        }

                        // Salon info
                        logoRow.RelativeItem().Column(col =>
                        {
                            col.Item().Text(data.SalonName)
                                .FontSize(18).Bold().FontColor("#1f2937");
                            if (!string.IsNullOrWhiteSpace(data.SalonNameAr))
                                col.Item().Text(data.SalonNameAr)
                                    .FontSize(14).FontColor("#6b7280");
                            if (!string.IsNullOrWhiteSpace(data.SalonAddress))
                                col.Item().Text(data.SalonAddress)
                                    .FontSize(9).FontColor("#9ca3af");
                            if (!string.IsNullOrWhiteSpace(data.SalonPhone))
                                col.Item().Text($"Tel: {data.SalonPhone}")
                                    .FontSize(9).FontColor("#9ca3af");
                        });
                    });

                    // INVOICE label على اليمين
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

                // Line items — grouped: package blocks first, then standalone extras
                var packageGroups = data.LineItems
                    .Where(x => x.PackageGroupId != null)
                    .GroupBy(x => x.PackageGroupId!.Value)
                    .ToList();
                var standalone = data.LineItems
                    .Where(x => x.PackageGroupId == null)
                    .ToList();

                column.Item().Column(items =>
                {
                    // ----- Package (OFFER) blocks -----
                    foreach (var g in packageGroups)
                    {
                        var glines = g.ToList();
                        decimal pkgPrice = glines.Sum(x => x.TotalPrice);
                        string pkgName = glines[0].PackageOfferName ?? "Package";

                        items.Item().PaddingBottom(6).Border(1).BorderColor("#111827").Column(block =>
                        {
                            // Package header: name + fixed price (black, bold)
                            block.Item().Background("#f3f4f6").Padding(7).Row(row =>
                            {
                                row.RelativeItem().Text(t =>
                                {
                                    t.Span("PACKAGE / باقة  ").FontSize(8).Bold().FontColor("#111827");
                                    t.Span(pkgName).FontSize(12).Bold().FontColor("#111827");
                                });
                                row.ConstantItem(110).AlignRight()
                                    .Text($"{data.Currency} {pkgPrice:F2}")
                                    .FontSize(12).Bold().FontColor("#111827");
                            });

                            // Services inside the package: name + staff only (no price)
                            for (int si = 0; si < glines.Count; si++)
                            {
                                var s = glines[si];
                                block.Item().BorderTop(si == 0 ? 0 : 1).BorderColor("#e5e7eb")
                                    .PaddingHorizontal(8).PaddingVertical(4).Row(row =>
                                    {
                                        row.RelativeItem().Text($"• {s.ItemName}")
                                            .FontSize(10).Bold().FontColor("#111827");
                                        row.ConstantItem(170).AlignRight().Text(s.StaffName)
                                            .FontSize(9).FontColor("#374151");
                                    });
                            }
                        });
                    }

                    // ----- Standalone services (outside any package) -----
                    if (standalone.Count > 0)
                    {
                        items.Item().PaddingTop(packageGroups.Count > 0 ? 2 : 0).Table(table =>
                        {
                            table.ColumnsDefinition(cols =>
                            {
                                cols.RelativeColumn(3);    // Item
                                cols.RelativeColumn(2);    // Staff
                                cols.ConstantColumn(90);   // Price
                            });

                            table.Header(header =>
                            {
                                header.Cell().Background("#f3f4f6").Padding(6)
                                    .Text("Item / الصنف").FontSize(9).Bold();
                                header.Cell().Background("#f3f4f6").Padding(6)
                                    .Text("Staff / المختص").FontSize(9).Bold();
                                header.Cell().Background("#f3f4f6").Padding(6).AlignRight()
                                    .Text("Price / السعر").FontSize(9).Bold();
                            });

                            for (int i = 0; i < standalone.Count; i++)
                            {
                                var item = standalone[i];
                                var bg = i % 2 == 0 ? "#ffffff" : "#f9fafb";

                                table.Cell().Background(bg).Padding(6)
                                    .Text(item.ItemName).FontSize(10).Bold().FontColor("#111827");
                                table.Cell().Background(bg).Padding(6)
                                    .Text(item.StaffName).FontSize(9).FontColor("#374151");
                                table.Cell().Background(bg).Padding(6).AlignRight()
                                    .Text($"{data.Currency} {item.TotalPrice:F2}")
                                    .FontSize(10).Bold().FontColor("#111827");
                            }
                        });
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
                    bool hasFooter = !string.IsNullOrWhiteSpace(data.FooterLine1)
                                  || !string.IsNullOrWhiteSpace(data.FooterLine2)
                                  || !string.IsNullOrWhiteSpace(data.FooterLine3);

                    if (hasFooter)
                    {
                        if (!string.IsNullOrWhiteSpace(data.FooterLine1))
                            footer.Item().Text(data.FooterLine1)
                                .FontSize(10).FontColor("#6b7280");
                        if (!string.IsNullOrWhiteSpace(data.FooterLine2))
                            footer.Item().Text(data.FooterLine2)
                                .FontSize(10).FontColor("#9ca3af");
                        if (!string.IsNullOrWhiteSpace(data.FooterLine3))
                            footer.Item().Text(data.FooterLine3)
                                .FontSize(9).FontColor("#9ca3af");
                    }
                    else
                    {
                        // fallback if the database is empty
                        footer.Item().Text("Thank you for your visit!")
                            .FontSize(10).FontColor("#6b7280");
                        footer.Item().Text("شكراً لزيارتكم")
                            .FontSize(10).FontColor("#9ca3af");
                    }
                });
            });
        }
    }
}