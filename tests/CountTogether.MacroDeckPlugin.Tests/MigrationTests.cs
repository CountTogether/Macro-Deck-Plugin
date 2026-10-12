using CountTogether.MacroDeckPlugin.Actions;
using CountTogether.MacroDeckPlugin.Migration;
using MacroDeck.Sdk.Migration;
using NUnit.Framework;

namespace CountTogether.MacroDeckPlugin.Tests;

[TestFixture]
public sealed class MigrationTests
{
	private const string IntegrationId = "app.counttogether.counttogether-macro-deck-plugin";

	private readonly MacroDeck2Migration _migration = new(IntegrationId);

	[Test]
	public void It_claims_what_the_macro_deck_2_plugin_wrote()
	{
		Assert.That(_migration.ClaimedActionSources, Does.Contain("CountTogether.MacroDeckPlugin"));
		Assert.That(_migration.ClaimedSettingsSources, Does.Contain("counttogether_counttogether.macrodeckplugin"));
	}

	[TestCase("CountTogether.MacroDeckPlugin.Actions.IncrementCounterAction", ChangeCounterAction.IncrementId)]
	[TestCase("CountTogether.MacroDeckPlugin.Actions.DecrementCounterAction", ChangeCounterAction.DecrementId)]
	public async Task A_button_keeps_its_counter(string typeName, string actionId)
	{
		var counterId = Guid.NewGuid();

		var result = await _migration.MigrateActionAsync(
			new ForeignAction(typeName, "CountTogether.MacroDeckPlugin", null, counterId.ToString(), null), CancellationToken.None);

		Assert.That(result, Is.Not.Null);
		Assert.That(result!.IntegrationId, Is.EqualTo(IntegrationId));
		Assert.That(result.ActionId, Is.EqualTo(actionId));
		Assert.That(result.Parameters[ChangeCounterAction.CounterParameter].GetString(), Is.EqualTo(counterId.ToString()));
	}

	[TestCase("CountTogether.MacroDeckPlugin.Actions.SomethingElse", "6f9619ff-8b86-d011-b42d-00c04fc964ff")]
	[TestCase("CountTogether.MacroDeckPlugin.Actions.IncrementCounterAction", null)]
	[TestCase("CountTogether.MacroDeckPlugin.Actions.IncrementCounterAction", "not a counter")]
	public async Task Anything_else_has_no_equivalent(string typeName, string? configuration)
	{
		var result = await _migration.MigrateActionAsync(
			new ForeignAction(typeName, "CountTogether.MacroDeckPlugin", null, configuration, null), CancellationToken.None);

		Assert.That(result, Is.Null);
	}

	[Test]
	public async Task The_api_token_becomes_the_secret_of_the_new_entry()
	{
		var settings = new ForeignPluginSettings("counttogether_counttogether.macrodeckplugin",
			new Dictionary<string, string>(),
			[new Dictionary<string, string> { ["apiToken"] = "secret-token" }],
			[]);

		var result = await _migration.MigrateConfigurationAsync(settings, CancellationToken.None);

		Assert.That(result, Has.Count.EqualTo(1));
		Assert.That(result[0].IntegrationId, Is.EqualTo(IntegrationId));
		Assert.That(result[0].Secrets["token"], Is.EqualTo(new MigratedSecret("secret-token", MigratedSecretKind.Secret)));
	}

	[Test]
	public async Task Without_a_token_nothing_is_migrated()
	{
		var settings = new ForeignPluginSettings("counttogether_counttogether.macrodeckplugin",
			new Dictionary<string, string>(),
			[new Dictionary<string, string> { ["apiToken"] = "" }],
			[]);

		Assert.That(await _migration.MigrateConfigurationAsync(settings, CancellationToken.None), Is.Empty);
	}
}
