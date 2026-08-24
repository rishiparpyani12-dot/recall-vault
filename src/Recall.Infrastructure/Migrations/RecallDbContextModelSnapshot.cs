using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable
namespace Recall.Infrastructure.Migrations;

[DbContext(typeof(RecallDbContext))]
public sealed class RecallDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => RecallDbContext.ConfigureModel(modelBuilder);
}
