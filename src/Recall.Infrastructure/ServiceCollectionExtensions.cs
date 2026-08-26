using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Application;

namespace Recall.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRecallInfrastructure(this IServiceCollection services, string databasePath) => services
        .AddDbContext<RecallDbContext>((provider, options) =>
        {
            var key = provider.GetRequiredService<IRecallDatabaseKeyProvider>()
                .GetOrCreateKeyAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            options.UseSqlite(SqlCipherConnectionFactory.CreateConnectionString(databasePath, key));
        })
        .AddScoped<IRecallStore, RecallStore>()
        .AddScoped<IRecallService, RecallService>()
        .AddSingleton<IClock, SystemClock>()
        .AddScoped<DatabaseInitializer>();
}
