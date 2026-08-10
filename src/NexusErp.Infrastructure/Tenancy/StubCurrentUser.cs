using NexusErp.Application.Abstractions;

namespace NexusErp.Infrastructure.Tenancy;

/// <summary>Bölüm 12'de ClaimsCurrentUser ile değiştirilecek.</summary>
internal sealed class StubCurrentUser : ICurrentUser
{
    public string UserName => "demo@nexuserp.com";
}
