namespace NexusErp.Application.Abstractions;

public interface IAppDbContextFactory
{
    IAppDbContext Create();
}
