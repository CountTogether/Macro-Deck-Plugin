using CountTogether.MacroDeckPlugin.Api;
using CountTogether.MacroDeckPlugin.ConfigFlow;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CountTogether.MacroDeckPlugin.Tests.Support;

internal static class TestPlugin
{
	public const string Token = "test-token";

	public static PluginTestHarness Create(ICountTogetherClientFactory factory)
		=> PluginTestHarness.Create(builder =>
		{
			builder.UseLocalization(Strings.LocalizationCatalog).RegisterIntegration<CountTogetherIntegration>();
			builder.Services.AddCountTogether();
			builder.Services.AddSingleton(factory);
		});

	/// <summary>A configured plugin whose connection has finished starting.</summary>
	public static async Task<PluginTestHarness> ConnectedAsync(FakeCountTogetherClient client)
	{
		var harness = Create(new FakeClientFactory(client));
		var entry = harness.Context.Config.AddEntry(ApiTokenConfigFlow.EntryTitle);
		harness.Context.Config.SeedSecret(entry, ApiTokenConfigFlow.TokenKey, Token);
		await harness.InitializeIntegrationsAsync();

		var connection = harness.Connection();
		await Wait.UntilAsync(() => connection.State == ConnectionState.Connected, because: "the fake connects at once");
		return harness;
	}

	public static CountTogetherConnection Connection(this PluginTestHarness harness)
		=> harness.Services.GetRequiredService<CountTogetherConnection>();
}
