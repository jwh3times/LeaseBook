using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LeaseBook.Web.Persistence;

public sealed class OrgConfiguration : IEntityTypeConfiguration<Org>
{
    public void Configure(EntityTypeBuilder<Org> builder)
    {
        // snake_case naming convention maps Org->orgs, Id->id, Name->name, CreatedAt->created_at.
        builder.ToTable("orgs");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Name).IsRequired().HasMaxLength(200);
        builder.Property(o => o.CreatedAt).IsRequired();
    }
}
