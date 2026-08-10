namespace NexusErp.Application.Abstractions;

public interface IInvoiceNumberGenerator
{
    /// <summary>
    /// Sıradaki fatura numarasını ATOMİK olarak üretir.
    /// Format: NEX2026000000001 (3 harf seri + 4 hane yıl + 9 hane sıra)
    /// </summary>
    Task<(string Number, long Sequence)> NextAsync(
        string series, int year, CancellationToken ct = default);
}
