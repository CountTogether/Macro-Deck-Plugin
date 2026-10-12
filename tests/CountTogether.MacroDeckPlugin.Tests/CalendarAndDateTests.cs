using System.Net;
using CountTogether.MacroDeckPlugin.Calendar;
using CountTogether.MacroDeckPlugin.Tests.Support;
using CountTogether.MacroDeckPlugin.Variables;
using MacroDeck.Plugin.Protocol.Capabilities.Calendar;
using MacroDeck.Plugin.Protocol.Capabilities.Variables;
using MacroDeck.Plugin.Testing;
using CountTogether.MacroDeckPlugin.Api;
using NUnit.Framework;

namespace CountTogether.MacroDeckPlugin.Tests;

/// <summary>To-date counters as calendars, and date variables, as the host reads and writes them.</summary>
[TestFixture]
public sealed class CalendarAndDateTests
{
	private static readonly DateOnly TargetDate = new(2026, 10, 23);

	[Test]
	public async Task The_account_lists_one_calendar_per_to_date_counter()
	{
		var vacation = Counters.ToDate("Vacation", 12, TargetDate);
		await using var harness = await TestPlugin.ConnectedAsync(
			new FakeCountTogetherClient(vacation, Counters.UpDown("Coffee", 3), Counters.FromDate("Smoke free", 365, new DateOnly(2025, 10, 12))));

		var accounts = (await harness.Calendar.GetAccountsAsync()).DataAs<CalendarAccountsResult>()!;
		var calendars = (await harness.Calendar.GetCalendarsAsync(new CalendarAccountArguments { AccountId = CountdownCalendar.AccountId }))
			.DataAs<CalendarListResult>()!;

		Assert.That(accounts.Accounts.Select(account => account.Id), Is.EqualTo(new[] { CountdownCalendar.AccountId }));
		Assert.That(calendars.Calendars.Select(calendar => calendar.Name), Is.EqualTo(new[] { "Vacation" }));
	}

	[Test]
	public async Task The_target_date_is_an_all_day_event_in_a_range_that_contains_it()
	{
		var vacation = Counters.ToDate("Vacation", 12, TargetDate);
		await using var harness = await TestPlugin.ConnectedAsync(new FakeCountTogetherClient(vacation));

		var inRange = await Events(harness, new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.FromHours(2)));
		var outOfRange = await Events(harness, new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.FromHours(1)));

		var countdown = inRange.Events.Single();
		Assert.That(countdown.Title, Is.EqualTo("Vacation"));
		Assert.That(countdown.IsAllDay, Is.True);
		Assert.That(DateOnly.FromDateTime(countdown.Start.Date), Is.EqualTo(TargetDate));
		Assert.That(outOfRange.Events, Is.Empty);
	}

	[Test]
	public async Task An_event_is_found_again_by_its_ids()
	{
		var vacation = Counters.ToDate("Vacation", 12, TargetDate);
		await using var harness = await TestPlugin.ConnectedAsync(new FakeCountTogetherClient(vacation));
		var id = vacation.Id.ToString("N");

		var outcome = await harness.Calendar.GetEventAsync(new CalendarEventArguments
		{
			AccountId = CountdownCalendar.AccountId,
			CalendarId = id,
			EventId = id,
		});

		Assert.That(outcome.DataAs<CalendarEventResult>()!.Event!.Title, Is.EqualTo("Vacation"));
	}

	[Test]
	public async Task A_read_while_still_connecting_waits_for_the_connection()
	{
		var vacation = Counters.ToDate("Vacation", 12, TargetDate);
		var client = new FakeCountTogetherClient(vacation) { StartGate = new TaskCompletionSource() };
		await using var harness = TestPlugin.Create(new FakeClientFactory(client));
		var entry = harness.Context.Config.AddEntry("CountTogether");
		harness.Context.Config.SeedSecret(entry, "token", TestPlugin.Token);
		await harness.InitializeIntegrationsAsync();

		var read = harness.Calendar.GetCalendarsAsync(new CalendarAccountArguments { AccountId = CountdownCalendar.AccountId });
		client.StartGate.SetResult();
		var outcome = await read;

		Assert.That(outcome.Succeeded, Is.True, outcome.Error?.Message);
		Assert.That(outcome.DataAs<CalendarListResult>()!.Calendars, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task Reading_events_while_disconnected_fails_instead_of_emptying_the_calendar()
	{
		var client = new FakeCountTogetherClient { StartFailure = new HttpRequestException("No route to host") };
		await using var harness = TestPlugin.Create(new FakeClientFactory(client));
		var entry = harness.Context.Config.AddEntry("CountTogether");
		harness.Context.Config.SeedSecret(entry, "token", TestPlugin.Token);
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.Calendar.GetEventsAsync(new CalendarEventsArguments
		{
			AccountId = CountdownCalendar.AccountId,
			From = DateTimeOffset.UnixEpoch,
			To = DateTimeOffset.UnixEpoch.AddDays(1),
		});

		Assert.That(outcome.Succeeded, Is.False);
	}

	[Test]
	public async Task The_date_variable_reads_and_writes_iso_dates()
	{
		var vacation = Counters.ToDate("Vacation", 12, TargetDate);
		var client = new FakeCountTogetherClient(vacation);
		await using var harness = await TestPlugin.ConnectedAsync(client);
		var id = CounterVariables.DateIdFor(vacation.Id);

		var reading = (await harness.Variables.GetAsync(id)).DataAs<VariableReadingDto>()!;
		var written = (await harness.Variables.SetAsync(id, new VariableValueDto { Kind = "text", Text = "2026-12-24" }))
			.DataAs<VariableSetResult>()!;

		Assert.That(reading.Value.Text, Is.EqualTo("2026-10-23"));
		Assert.That(written.Status, Is.EqualTo("Applied"));
		Assert.That(client.Updates.Single().Request.EndDate, Is.EqualTo(new DateOnly(2026, 12, 24)));
	}

	[Test]
	public async Task A_date_the_api_refuses_is_reported_as_invalid()
	{
		var vacation = Counters.ToDate("Vacation", 12, TargetDate);
		var client = new FakeCountTogetherClient(vacation) { UpdateFailure = FakeCountTogetherClient.ApiError(HttpStatusCode.BadRequest) };
		await using var harness = await TestPlugin.ConnectedAsync(client);

		var written = (await harness.Variables.SetAsync(CounterVariables.DateIdFor(vacation.Id),
			new VariableValueDto { Kind = "text", Text = "2020-01-01" })).DataAs<VariableSetResult>()!;

		Assert.That(written.Status, Is.EqualTo("InvalidValue"));
	}

	[Test]
	public async Task A_dragged_slider_sends_one_change_once_the_value_settles()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee);
		await using var harness = await TestPlugin.ConnectedAsync(client);
		var id = CounterVariables.IdFor(coffee.Id);

		foreach (var step in new[] { 4.0, 5.0, 6.0, 8.0 })
		{
			var written = (await harness.Variables.SetAsync(id, new VariableValueDto { Kind = "number", Number = step }))
				.DataAs<VariableSetResult>()!;
			Assert.That(written.Status, Is.EqualTo("Applied"));
			harness.Clock.Advance(TimeSpan.FromMilliseconds(100));
		}

		var held = (await harness.Variables.GetAsync(id)).DataAs<VariableReadingDto>()!;
		Assert.That(held.Value.Number, Is.EqualTo(8), "the variable shows the slider's value while it is held");
		Assert.That(client.Changes, Is.Empty, "nothing is sent while the value is still moving");

		harness.Clock.Advance(CounterValueWriter.SettleDelay);
		await Wait.UntilAsync(() => client.Changes.Count > 0);

		Assert.That(client.Changes, Is.EqualTo(new[] { (coffee.Id, true, (int?)5) }));
	}

	[Test]
	public async Task A_failed_change_lets_the_variable_fall_back_to_the_real_value()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee) { ChangeFailure = FakeCountTogetherClient.ApiError(HttpStatusCode.Forbidden) };
		await using var harness = await TestPlugin.ConnectedAsync(client);
		var id = CounterVariables.IdFor(coffee.Id);

		await harness.Variables.SetAsync(id, new VariableValueDto { Kind = "number", Number = 9 });
		harness.Clock.Advance(CounterValueWriter.SettleDelay);
		await Wait.UntilAsync(() => client.Changes.Count > 0);

		await Wait.UntilAsync(() => harness.Variables.GetAsync(id).GetAwaiter().GetResult()
			.DataAs<VariableReadingDto>()!.Value.Number == 3);
	}

	[Test]
	public async Task Writing_a_date_counter_value_is_refused()
	{
		var vacation = Counters.ToDate("Vacation", 12, TargetDate);
		await using var harness = await TestPlugin.ConnectedAsync(new FakeCountTogetherClient(vacation));

		var written = (await harness.Variables.SetAsync(CounterVariables.IdFor(vacation.Id),
			new VariableValueDto { Kind = "number", Number = 3 })).DataAs<VariableSetResult>()!;

		Assert.That(written.Status, Is.Not.EqualTo("Applied"));
	}

	private static async Task<CalendarEventsResult> Events(MacroDeck.Plugin.Testing.PluginTestHarness harness, DateTimeOffset from)
		=> (await harness.Calendar.GetEventsAsync(new CalendarEventsArguments
		{
			AccountId = CountdownCalendar.AccountId,
			From = from,
			To = from.AddDays(1),
		})).DataAs<CalendarEventsResult>()!;
}
