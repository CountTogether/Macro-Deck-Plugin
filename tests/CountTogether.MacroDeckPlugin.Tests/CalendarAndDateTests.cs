using System.Net;
using CountTogether.MacroDeckPlugin.Calendar;
using CountTogether.MacroDeckPlugin.Tests.Support;
using CountTogether.MacroDeckPlugin.Variables;
using MacroDeck.Plugin.Protocol.Capabilities.Calendar;
using MacroDeck.Plugin.Protocol.Capabilities.Variables;
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
	public async Task Writing_the_value_variable_sets_the_counter()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee);
		await using var harness = await TestPlugin.ConnectedAsync(client);

		var written = (await harness.Variables.SetAsync(CounterVariables.IdFor(coffee.Id),
			new VariableValueDto { Kind = "number", Number = 8 })).DataAs<VariableSetResult>()!;

		Assert.That(written.Status, Is.EqualTo("Applied"));
		Assert.That(client.Changes, Is.EqualTo(new[] { (coffee.Id, true, (int?)5) }));
	}

	private static async Task<CalendarEventsResult> Events(MacroDeck.Plugin.Testing.PluginTestHarness harness, DateTimeOffset from)
		=> (await harness.Calendar.GetEventsAsync(new CalendarEventsArguments
		{
			AccountId = CountdownCalendar.AccountId,
			From = from,
			To = from.AddDays(1),
		})).DataAs<CalendarEventsResult>()!;
}
