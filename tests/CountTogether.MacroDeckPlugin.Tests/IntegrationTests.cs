using System.Net;
using CountTogether.MacroDeckPlugin.Actions;
using CountTogether.MacroDeckPlugin.Api;
using CountTogether.MacroDeckPlugin.Events;
using CountTogether.MacroDeckPlugin.Tests.Support;
using CountTogether.MacroDeckPlugin.Variables;
using CountTogether.PublicApiSdk.Models;
using MacroDeck.Plugin.Protocol.Capabilities.Variables;
using MacroDeck.Plugin.Protocol.Handshake;
using MacroDeck.Plugin.Testing;
using MacroDeck.Sdk.Actions;
using NUnit.Framework;

namespace CountTogether.MacroDeckPlugin.Tests;

/// <summary>The plugin as the host drives it, through <see cref="PluginTestHarness" />.</summary>
[TestFixture]
public sealed class IntegrationTests
{
	[Test]
	public async Task The_plugin_builds_and_stays_idle_without_a_token()
	{
		await using var harness = TestPlugin.Create(new FakeClientFactory(new FakeCountTogetherClient()));

		await harness.InitializeIntegrationsAsync();

		Assert.That(harness.Connection().State, Is.EqualTo(ConnectionState.NotConfigured));
	}

	[Test]
	public async Task The_variables_capability_is_declared_before_an_account_is_connected()
	{
		// Macro Deck only routes variable writes to a plugin that declared the capability when its session
		// started, which is before CountTogether has answered.
		await using var harness = TestPlugin.Create(new FakeClientFactory(new FakeCountTogetherClient()));

		Assert.That(harness.Declared.Select(capability => capability.Kind), Does.Contain(CapabilityKinds.Variables));
	}

	[Test]
	public async Task The_connection_variable_reports_the_connection()
	{
		await using var harness = await TestPlugin.ConnectedAsync(new FakeCountTogetherClient(Counters.UpDown("Coffee", 3)));

		var reading = (await harness.Variables.GetAsync(CounterVariables.ConnectedId)).DataAs<VariableReadingDto>();

		Assert.That(reading!.Value.Boolean, Is.True);
	}

	[Test]
	public async Task Incrementing_changes_the_counter_by_the_given_amount()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee);
		await using var harness = await TestPlugin.ConnectedAsync(client);

		var outcome = await harness.Actions.ExecuteAsync(ChangeCounterAction.IncrementId,
			new Dictionary<string, object?> { ["counterId"] = coffee.Id.ToString(), ["amount"] = 5 });

		Assert.That(outcome.Succeeded, Is.True);
		Assert.That(client.Changes, Is.EqualTo(new[] { (coffee.Id, true, (int?)5) }));
	}

	[Test]
	public async Task Decrementing_without_an_amount_uses_the_counter_step()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee);
		await using var harness = await TestPlugin.ConnectedAsync(client);

		var outcome = await harness.Actions.ExecuteAsync(ChangeCounterAction.DecrementId,
			new Dictionary<string, object?> { ["counterId"] = coffee.Id.ToString(), ["amount"] = "" });

		Assert.That(outcome.Succeeded, Is.True);
		Assert.That(client.Changes, Is.EqualTo(new[] { (coffee.Id, false, (int?)null) }));
	}

	[TestCase(0)]
	[TestCase(1.5)]
	[TestCase(1_000_001)]
	public async Task An_amount_outside_the_api_range_is_refused(double amount)
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee);
		await using var harness = await TestPlugin.ConnectedAsync(client);

		var outcome = await harness.Actions.ExecuteAsync(ChangeCounterAction.IncrementId,
			new Dictionary<string, object?> { ["counterId"] = coffee.Id.ToString(), ["amount"] = amount });

		Assert.That(outcome.Error?.Code, Is.EqualTo(ActionErrorCodes.InvalidParameter));
		Assert.That(client.Changes, Is.Empty);
	}

	[Test]
	public async Task An_unknown_counter_fails_as_not_found()
	{
		await using var harness = await TestPlugin.ConnectedAsync(new FakeCountTogetherClient(Counters.UpDown("Coffee", 3)));

		var outcome = await harness.Actions.ExecuteAsync(ChangeCounterAction.IncrementId,
			new Dictionary<string, object?> { ["counterId"] = Guid.NewGuid().ToString() });

		Assert.That(outcome.Error?.Code, Is.EqualTo(ActionErrorCodes.NotFound));
	}

	[Test]
	public async Task A_refusal_by_the_api_is_reported_truthfully()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee) { ChangeFailure = FakeCountTogetherClient.ApiError(HttpStatusCode.Forbidden) };
		await using var harness = await TestPlugin.ConnectedAsync(client);

		var outcome = await harness.Actions.ExecuteAsync(ChangeCounterAction.IncrementId,
			new Dictionary<string, object?> { ["counterId"] = coffee.Id.ToString() });

		Assert.That(outcome.Error?.Code, Is.EqualTo(ActionErrorCodes.PermissionDenied));
	}

	[Test]
	public async Task Actions_fail_as_not_configured_before_setup()
	{
		await using var harness = TestPlugin.Create(new FakeClientFactory(new FakeCountTogetherClient()));
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Actions.ExecuteAsync(ChangeCounterAction.IncrementId,
			new Dictionary<string, object?> { ["counterId"] = Guid.NewGuid().ToString() });

		Assert.That(outcome.Error?.Code, Is.EqualTo(ActionErrorCodes.NotConfigured));
	}

	[Test]
	public async Task The_counter_picker_lists_only_up_down_counters()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		await using var harness = await TestPlugin.ConnectedAsync(new FakeCountTogetherClient(coffee, Counters.ToDate("Vacation", 12)));

		var outcome = await harness.Actions.GetOptionsAsync(ChangeCounterAction.IncrementId, ChangeCounterAction.CounterParameter);

		Assert.That(outcome.Succeeded, Is.True);
		Assert.That(outcome.Data!.Value.GetRawText(), Does.Contain(coffee.Id.ToString()).And.Not.Contain("Vacation"));
	}

	[Test]
	public async Task Every_counter_is_a_variable_with_its_current_value()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var vacation = Counters.ToDate("Vacation", 12);
		await using var harness = await TestPlugin.ConnectedAsync(new FakeCountTogetherClient(coffee, vacation));

		var reading = (await harness.Variables.GetAsync(CounterVariables.IdFor(vacation.Id))).DataAs<VariableReadingDto>();

		Assert.That(reading!.Value.Number, Is.EqualTo(12));
	}

	[Test]
	public async Task A_value_change_publishes_the_event_with_a_counter_id_the_picker_can_match()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee);
		await using var harness = await TestPlugin.ConnectedAsync(client);

		client.PushUpdate(new Counter { Id = coffee.Id, Name = "Coffee", Type = CounterType.UpDown, Value = 7 });

		var published = harness.Context.Events.Published.Single(e => e.EventId == CounterEvents.ValueChangedId);
		var payload = published.Parameters!.Value;
		Assert.That(payload.GetProperty("counterId").GetString(), Is.EqualTo(CounterEvents.FormatCounterId(coffee.Id)));
		Assert.That(payload.GetProperty("value").GetInt64(), Is.EqualTo(7));
		Assert.That(payload.GetProperty("previousValue").GetInt64(), Is.EqualTo(3));
		Assert.That(payload.GetProperty("change").GetInt64(), Is.EqualTo(4));

		var options = await harness.Events.GetOptionsAsync(new MacroDeck.Plugin.Protocol.Capabilities.Events.EventOptionsArguments
		{
			EventId = CounterEvents.ValueChangedId,
			ParameterName = CounterEvents.CounterParameter,
		});
		Assert.That(options.Data!.Value.GetRawText(), Does.Contain($"\"{CounterEvents.FormatCounterId(coffee.Id)}\""));
	}

	[Test]
	public async Task A_deleted_counter_publishes_the_deleted_event()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee);
		await using var harness = await TestPlugin.ConnectedAsync(client);

		client.PushDelete(coffee.Id);

		Assert.That(harness.Context.Events.Published.Select(e => e.EventId), Does.Contain(CounterEvents.DeletedId));
	}

	[Test]
	public async Task A_rejected_token_raises_an_issue_that_reopens_setup()
	{
		var client = new FakeCountTogetherClient { StartFailure = FakeCountTogetherClient.ApiError(HttpStatusCode.Unauthorized) };
		await using var harness = TestPlugin.Create(new FakeClientFactory(client));
		var entry = harness.Context.Config.AddEntry("CountTogether");
		harness.Context.Config.SeedSecret(entry, "token", TestPlugin.Token);
		await harness.InitializeIntegrationsAsync();
		await Wait.UntilAsync(() => harness.Connection().State == ConnectionState.Unauthorized);

		var issues = await harness.Issues.GetIssuesAsync();

		Assert.That(issues.Data!.Value.GetRawText(), Does.Contain(CountTogetherIntegration.TokenRejectedIssueId));
	}
}
