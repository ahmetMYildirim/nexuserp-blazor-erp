namespace NexusErp.Domain.Common;

/// <summary>
/// İş kuralı ihlali. Teknik hatalardan (NullReference, DbUpdate) ayırt edilebilsin diye
/// ayrı tip — UI katmanı bunu kullanıcıya gösterilebilir mesaj olarak ele alır.
/// </summary>
public sealed class DomainException(string message) : Exception(message);
