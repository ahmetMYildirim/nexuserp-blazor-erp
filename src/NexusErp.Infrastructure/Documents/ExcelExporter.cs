using ClosedXML.Excel;
using NexusErp.Application.Abstractions;
using NexusErp.Application.Payments;

namespace NexusErp.Infrastructure.Documents;

public sealed class ExcelExporter : IExcelExporter
{
    public byte[] ExportAging(IReadOnlyList<AgingRow> rows, DateOnly asOf)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Yaşlandırma");

        ws.Cell(1, 1).Value = $"Cari Yaşlandırma Raporu — {asOf:dd.MM.yyyy}";
        ws.Range(1, 1, 1, 7).Merge().Style.Font.SetBold().Font.SetFontSize(14);

        string[] headers =
        [
            "Cari", "Vadesi Gelmemiş", "1–30 Gün", "31–60 Gün", "61–90 Gün", "90+ Gün", "Toplam"
        ];

        for (var c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(3, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.SetBold()
                .Fill.SetBackgroundColor(XLColor.LightGray)
                .Border.SetBottomBorder(XLBorderStyleValues.Thin);
        }

        var row = 4;
        foreach (var r in rows)
        {
            ws.Cell(row, 1).Value = r.PartyTitle;
            ws.Cell(row, 2).Value = r.NotDue;
            ws.Cell(row, 3).Value = r.Days1To30;
            ws.Cell(row, 4).Value = r.Days31To60;
            ws.Cell(row, 5).Value = r.Days61To90;
            ws.Cell(row, 6).Value = r.Over90;
            ws.Cell(row, 7).Value = r.Total;
            row++;
        }

        // Toplam satırı — FORMÜL, sabit değer değil (dosya Excel'de yeniden hesaplansın)
        if (rows.Count > 0)
        {
            ws.Cell(row, 1).Value = "TOPLAM";
            for (var c = 2; c <= 7; c++)
            {
                var col = (char)('A' + c - 1);
                ws.Cell(row, c).FormulaA1 = $"SUM({col}4:{col}{row - 1})";
            }
            ws.Range(row, 1, row, 7).Style.Font.SetBold()
              .Fill.SetBackgroundColor(XLColor.LightGray);
        }

        ws.Range(4, 2, row, 7).Style.NumberFormat.Format = "#,##0.00";
        ws.Range(4, 6, row, 6).Style.Font.SetFontColor(XLColor.Red);   // 90+ gün kırmızı
        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(3);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
