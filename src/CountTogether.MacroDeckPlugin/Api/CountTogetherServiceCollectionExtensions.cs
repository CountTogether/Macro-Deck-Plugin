using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CountTogether.MacroDeckPlugin.Api;

public static class CountTogetherServiceCollectionExtensions
{
	/// <summary>Registers the connection and what it needs. Shared by the plugin and its tests.</summary>
	public static IServiceCollection AddCountTogether(this IServiceCollection services)
	{
		services.TryAddSingleton(TimeProvider.System);
		services.TryAddSingleton<ICountTogetherClientFactory, CountTogetherClientFactory>();
		services.AddHttpClient(CountTogetherConnection.HttpClientName);
		services.TryAddSingleton(provider => new ApiRateLimiter(provider.GetRequiredService<TimeProvider>()));
		services.TryAddSingleton<CountTogetherConnection>();
		services.TryAddSingleton<CounterValueWriter>();
		return services;
	}
}
