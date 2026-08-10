using MudBlazor;
using NexusErp.Domain.Common;

namespace NexusErp.Web.Components.Shared;

public static class UiHelpers
{
    /// <summary>
    /// İş kuralı hatalarını (DomainException) kullanıcıya Snackbar ile gösterir,
    /// beklenmeyen hataları YENİDEN FIRLATIR.
    ///
    /// ⚠️ Anti-pattern uyarısı: catch (Exception) yazıp her şeyi Snackbar'a basmak.
    /// O zaman NullReference gibi gerçek hatalar "bir şeyler ters gitti" mesajının
    /// arkasında kaybolur. Sadece kural ihlali yakalanır.
    /// </summary>
    public static async Task<bool> RunAsync(this ISnackbar snackbar, Func<Task> action,
                                            string? successMessage = null)
    {
        try
        {
            await action();
            if (successMessage is not null)
                snackbar.Add(successMessage, Severity.Success);
            return true;
        }
        catch (DomainException ex)
        {
            snackbar.Add(ex.Message, Severity.Warning);
            return false;
        }
    }

    /// <summary>Para biçimi: 1.234,56 (tr-TR). Sıfırlar tabloda "—" gösterilir.</summary>
    public static string ToMoney(this decimal value, bool dashOnZero = false) =>
        dashOnZero && value == 0m ? "—" : value.ToString("N2");

    /// <summary>Oran biçimi: 0,20 → "%20"</summary>
    public static string ToRate(this decimal rate) => "%" + (rate * 100m).ToString("0.##");

    /// <summary>Tevkifat biçimi: 0,70 → "7/10"</summary>
    public static string ToWithholding(this decimal? rate) =>
        rate is null ? "—" : $"{rate.Value * 10m:0}/10";
}
