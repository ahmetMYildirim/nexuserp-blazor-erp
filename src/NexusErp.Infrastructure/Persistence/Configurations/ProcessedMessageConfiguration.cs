using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexusErp.Domain.Entities;

namespace NexusErp.Infrastructure.Persistence.Configurations;

public sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> b)
    {
        b.ToTable("processed_messages");
        b.HasKey(x => x.Id);

        b.Property(x => x.ConsumerName).HasMaxLength(100).IsRequired();
        b.Property(x => x.CreatedBy).HasMaxLength(100).IsRequired();
        b.Property(x => x.UpdatedBy).HasMaxLength(100);

        // ⚠️ İdempotency'nin TEK garantisi bu index. İş mantığındaki "önce bak,
        // sonra yaz" kontrolü yarış koşulunda yanılır; veri tabanı kısıtı yanılmaz.
        // Aynı prensip abonelik faturalandırmasında da kullanılıyor.
        b.HasIndex(x => new { x.ConsumerName, x.MessageId })
         .IsUnique()
         .HasDatabaseName("ix_processed_messages_consumer_message");

        // Temizlik işi eskileri buradan tarar
        b.HasIndex(x => x.ProcessedAt);
    }
}
