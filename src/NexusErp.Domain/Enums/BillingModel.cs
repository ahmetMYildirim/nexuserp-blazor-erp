namespace NexusErp.Domain.Enums;

/// <summary>
/// Planın ücretlendirme modeli.
///
/// ⚠️ Sabit ücret PEŞİN, kullanım ücreti GEÇMİŞE DÖNÜK faturalanır — mecburen:
/// bir dönemin kullanımı ancak dönem bittiğinde bilinir. Bu yüzden hibrit bir
/// faturada iki farklı döneme ait iki satır bulunur ve bu bir hata değildir.
/// </summary>
public enum BillingModel
{
    /// <summary>Sabit dönem ücreti. Kullanım kaydı toplanmaz.</summary>
    Flat = 1,

    /// <summary>Yalnızca kullanım. Kullanım yoksa fatura da kesilmez.</summary>
    Metered = 2,

    /// <summary>Taban ücret + dahil birimleri aşan kullanım.</summary>
    Hybrid = 3
}
