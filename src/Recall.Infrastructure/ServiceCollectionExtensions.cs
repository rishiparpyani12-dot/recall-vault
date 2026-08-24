using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Application;

namespace Recall.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRecallInfrastructure(this IServiceCollection services, string connectionString) => services
        .AddDbContext<RecallDbContext>(o => o.UseSqlite(connectionString))
        .AddScoped<IRecallStore, RecallStore>()
        .AddScoped<IRecallService, RecallService>()
        .AddSingleton<IClock, SystemClock>()
        .AddScoped<DatabaseInitializer>();
}
