using CountTogether.MacroDeckPlugin.Api;
using CountTogether.MacroDeckPlugin.Tests.Support;
using CountTogether.MacroDeckPlugin.Variables;
using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Protocol.Capabilities.Variables;
using MacroDeck.Plugin.Testing;
using MacroDeck.Plugin.Testing.Fakes;
using MacroDeck.Sdk;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace CountTogether.MacroDeckPlugin.Tests;

/// <summary>The plugin over the real plugin protocol, the way Macro Deck itself talks to it.</summary>
[TestFixture]
public sealed class WireTests
{
	[Test]
	public async Task A_value_written_over_the_protocol_reaches_the_counter()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee);
		var context = new FakeIntegrationContext();
		var entry = context.Config.AddEntry("CountTogether");
		context.Config.SeedSecret(entry, "token", TestPlugin.Token);

		await using var host = await MacroDeckTestHost.StartAsync();
		var builder = MacroDeck.Plugin.Hosting.MacroDeckPlugin.CreatePlugin()
			.UseLocalization(Strings.LocalizationCatalog)
			.RegisterIntegration<CountTogetherIntegration>();
		builder.Services.AddCountTogether();
		builder.Services.AddSingleton<ICountTogetherClientFactory>(new FakeClientFactory(client));
		builder.Services.AddSingleton<IIntegrationContext>(context);

		await using var plugin = await host.HostAsync(builder);
		var session = await host.WaitForSessionAsync();
		var connection = plugin.Application.Services.GetRequiredService<CountTogetherConnection>();
		await Wait.UntilAsync(() => connection.State == ConnectionState.Connected);

		var outcome = await session.Variables.SetAsync(CounterVariables.IdFor(coffee.Id),
			new VariableValueDto { Kind = "number", Number = 5 });

		Assert.That(outcome.Succeeded, Is.True, outcome.Error?.Code + ": " + outcome.Error?.Message);
		Assert.That(outcome.DataAs<VariableSetResult>()!.Status, Is.EqualTo("Applied"));
	}
}
