using NexusErp.Application.Payments;

namespace NexusErp.Application.Abstractions;

/// <summary>Fatura PDF'i üretir (QuestPDF implementasyonu Infrastructure'da).</summary>
public interface IInvoicePdfGenerator
{
    Task<byte[]> GenerateAsync(Guid invoiceId, CancellationToken ct = default);
}

/// <summary>Excel çıktıları (ClosedXML implementasyonu Infrastructure'da).</summary>
public interface IExcelExporter
{
    byte[] ExportAging(IReadOnlyList<AgingRow> rows, DateOnly asOf);
}
