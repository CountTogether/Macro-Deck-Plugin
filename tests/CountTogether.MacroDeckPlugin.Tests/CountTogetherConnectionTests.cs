using System.Net;
using System.Net.WebSockets;
using CountTogether.MacroDeckPlugin.Api;
using CountTogether.MacroDeckPlugin.Tests.Support;
using CountTogether.PublicApiSdk.Models;
using MacroDeck.Plugin.Testing;
using NUnit.Framework;

namespace CountTogether.MacroDeckPlugin.Tests;

[TestFixture]
public sealed class CountTogetherConnectionTests
{
	private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));

	private CountTogetherConnection Create(FakeClientFactory factory)
		=> new(factory, new StubHttpClientFactory(), _clock, Serilog.Core.Logger.None);

	private async Task<CountTogetherConnection> ConnectedAsync(FakeCountTogetherClient client)
	{
		var connection = Create(new FakeClientFactory(client));
		await connection.StartAsync(TestPlugin.Token);
		await Wait.UntilAsync(() => connection.State == ConnectionState.Connected);
		return connection;
	}

	[Test]
	public async Task Connecting_takes_every_counter_of_a_known_type()
	{
		var client = new FakeCountTogetherClient(
			Counters.UpDown("Coffee", 3),
			Counters.ToDate("Vacation", 12),
			new Counter { Id = Guid.NewGuid(), Name = "From the future", Type = CounterType.Unknown });

		await using var connection = await ConnectedAsync(client);

		Assert.That(client.StartedWithToken, Is.EqualTo(TestPlugin.Token));
		Assert.That(connection.Counters.Select(counter => counter.Name), Is.EqualTo(new[] { "Coffee", "Vacation" }));
	}

	[Test]
	public async Task A_rejected_token_stops_without_retrying()
	{
		var client = new FakeCountTogetherClient
		{
			StartFailure = new WebSocketException(WebSocketError.NotAWebSocket,
				"The server returned status code '401' when status code '101' was expected."),
		};
		var factory = new FakeClientFactory(client);
		await using var connection = Create(factory);

		await connection.StartAsync(TestPlugin.Token);
		await Wait.UntilAsync(() => connection.State == ConnectionState.Unauthorized);
		_clock.Advance(TimeSpan.FromMinutes(5));

		Assert.That(factory.Created, Is.EqualTo(1));
		Assert.That(client.Disposed, Is.True);
	}

	[Test]
	public async Task Confirming_a_rejected_token_again_reconnects()
	{
		var rejecting = new FakeCountTogetherClient { StartFailure = FakeCountTogetherClient.ApiError(HttpStatusCode.Unauthorized) };
		var accepting = new FakeCountTogetherClient(Counters.UpDown("Coffee", 3));
		await using var connection = Create(new FakeClientFactory(rejecting, accepting));
		await connection.StartAsync(TestPlugin.Token);
		await Wait.UntilAsync(() => connection.State == ConnectionState.Unauthorized);

		await connection.StartAsync(TestPlugin.Token);

		await Wait.UntilAsync(() => connection.State == ConnectionState.Connected);
	}

	[Test]
	public async Task A_failing_refresh_marks_the_values_unreachable_until_one_works_again()
	{
		var client = new FakeCountTogetherClient(Counters.UpDown("Coffee", 3));
		await using var connection = await ConnectedAsync(client);

		client.ListFailure = new HttpRequestException("No route to host");
		_clock.Advance(CountTogetherConnection.RefreshInterval);
		await Wait.UntilAsync(() => connection.State == ConnectionState.Unreachable);

		client.ListFailure = null;
		_clock.Advance(CountTogetherConnection.RefreshInterval);
		await Wait.UntilAsync(() => connection.State == ConnectionState.Connected);
	}

	[Test]
	public async Task A_refresh_does_not_overwrite_a_push_that_arrived_while_it_ran()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee);
		await using var connection = await ConnectedAsync(client);
		var refreshed = false;
		client.AfterListTaken = () =>
		{
			client.AfterListTaken = null;
			client.PushUpdate(new Counter { Id = coffee.Id, Name = "Coffee", Type = CounterType.UpDown, Value = 9 });
			refreshed = true;
		};

		_clock.Advance(CountTogetherConnection.RefreshInterval);
		await Wait.UntilAsync(() => refreshed);
		await Wait.UntilAsync(() => connection.State == ConnectionState.Connected);

		Assert.That(connection.Find(coffee.Id)!.Value, Is.EqualTo(9));
	}

	[Test]
	public async Task An_unreachable_service_is_retried_with_a_fresh_client()
	{
		var offline = new FakeCountTogetherClient { StartFailure = new HttpRequestException("No route to host") };
		var online = new FakeCountTogetherClient(Counters.UpDown("Coffee", 3));
		var factory = new FakeClientFactory(offline, online);
		await using var connection = Create(factory);

		await connection.StartAsync(TestPlugin.Token);
		await Wait.UntilAsync(() => connection.State == ConnectionState.Unreachable);
		_clock.Advance(CountTogetherConnection.FirstRetryDelay);
		await Wait.UntilAsync(() => connection.State == ConnectionState.Connected);

		Assert.That(factory.Created, Is.EqualTo(2));
		Assert.That(connection.Counters, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task A_pushed_value_raises_one_change_and_keeps_the_known_kind()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee);
		await using var connection = await ConnectedAsync(client);
		var changes = new List<(long? Previous, long? Current)>();
		connection.ValueChanged += (previous, current) => changes.Add((previous.Value, current.Value));

		// Push updates may omit the type.
		client.PushUpdate(new Counter { Id = coffee.Id, Name = "Coffee", Type = CounterType.Unknown, Value = 4 });
		client.PushUpdate(new Counter { Id = coffee.Id, Name = "Coffee", Type = CounterType.Unknown, Value = 4 });

		Assert.That(changes, Is.EqualTo(new[] { ((long?)3, (long?)4) }));
		Assert.That(connection.Find(coffee.Id)!.Kind, Is.EqualTo(CounterKind.UpDown));
	}

	[Test]
	public async Task A_pushed_delete_removes_the_counter()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee);
		await using var connection = await ConnectedAsync(client);
		CounterSnapshot? removed = null;
		connection.Removed += counter => removed = counter;

		client.PushDelete(coffee.Id);

		Assert.That(removed?.Id, Is.EqualTo(coffee.Id));
		Assert.That(connection.Find(coffee.Id), Is.Null);
	}

	[Test]
	public async Task A_member_list_change_adds_a_newly_shared_counter()
	{
		var client = new FakeCountTogetherClient();
		await using var connection = await ConnectedAsync(client);
		var shared = Counters.UpDown("Team score", 10);

		client.SetSilently(shared);
		client.PushMemberlistChanged(shared.Id);

		await Wait.UntilAsync(() => connection.Find(shared.Id) is not null);
	}

	[Test]
	public async Task A_member_list_change_removes_a_counter_the_api_no_longer_returns()
	{
		var shared = Counters.UpDown("Team score", 10);
		var client = new FakeCountTogetherClient(shared);
		await using var connection = await ConnectedAsync(client);

		client.SetSilently();
		client.PushMemberlistChanged(shared.Id);

		await Wait.UntilAsync(() => connection.Find(shared.Id) is null);
	}

	[Test]
	public async Task The_regular_refresh_picks_up_a_new_day_of_a_date_counter()
	{
		var vacation = Counters.ToDate("Vacation", 12);
		var client = new FakeCountTogetherClient(vacation);
		await using var connection = await ConnectedAsync(client);

		client.SetSilently(new Counter { Id = vacation.Id, Name = "Vacation", Type = CounterType.ToDate, Value = 11 });
		_clock.Advance(CountTogetherConnection.RefreshInterval);

		await Wait.UntilAsync(() => connection.Find(vacation.Id)?.Value == 11);
	}

	[Test]
	public async Task The_response_of_a_press_does_not_overwrite_a_newer_push()
	{
		var coffee = Counters.UpDown("Coffee", 3);
		var client = new FakeCountTogetherClient(coffee) { ChangeResult = 4 };
		await using var connection = await ConnectedAsync(client);
		client.PushUpdate(new Counter { Id = coffee.Id, Name = "Coffee", Type = CounterType.UpDown, Value = 5 });

		var result = await connection.IncrementAsync(coffee.Id, amount: null, CancellationToken.None);

		Assert.That(result.Value, Is.EqualTo(4));
		Assert.That(connection.Find(coffee.Id)!.Value, Is.EqualTo(5));
	}

	[Test]
	public async Task Setting_a_value_changes_the_counter_by_the_difference_and_keeps_its_step()
	{
		var coffee = Counters.UpDown("Coffee", 10);
		var client = new FakeCountTogetherClient(coffee);
		await using var connection = await ConnectedAsync(client);

		var result = await connection.SetValueAsync(coffee.Id, 7, CancellationToken.None);

		Assert.That(result.Succeeded, Is.True);
		Assert.That(client.Changes, Is.EqualTo(new[] { (coffee.Id, false, (int?)3) }));
		Assert.That(client.Updates, Is.Empty);
	}

	[Test]
	public async Task Setting_the_current_value_calls_nothing()
	{
		var coffee = Counters.UpDown("Coffee", 10);
		var client = new FakeCountTogetherClient(coffee);
		await using var connection = await ConnectedAsync(client);

		Assert.That((await connection.SetValueAsync(coffee.Id, 10, CancellationToken.None)).Succeeded, Is.True);
		Assert.That(client.Changes, Is.Empty);
	}

	[Test]
	public async Task A_jump_beyond_the_api_limit_is_refused()
	{
		var coffee = Counters.UpDown("Coffee", 0);
		var client = new FakeCountTogetherClient(coffee);
		await using var connection = await ConnectedAsync(client);

		var result = await connection.SetValueAsync(coffee.Id, CountTogetherConnection.MaxAmount + 1L, CancellationToken.None);

		Assert.That(result.Failure, Is.EqualTo(CountTogetherFailure.InvalidValue));
		Assert.That(client.Changes, Is.Empty);
	}

	[Test]
	public async Task Moving_the_target_date_updates_the_counter()
	{
		var vacation = Counters.ToDate("Vacation", 12, new DateOnly(2026, 10, 23));
		var client = new FakeCountTogetherClient(vacation);
		await using var connection = await ConnectedAsync(client);

		var result = await connection.SetDateAsync(vacation.Id, new DateOnly(2026, 12, 24), CancellationToken.None);

		Assert.That(result.Succeeded, Is.True);
		Assert.That(client.Updates.Single().Request.EndDate, Is.EqualTo(new DateOnly(2026, 12, 24)));
		Assert.That(connection.Find(vacation.Id)!.Date, Is.EqualTo(new DateOnly(2026, 12, 24)));
	}

	[Test]
	public async Task A_regular_counter_has_no_date_to_move()
	{
		var coffee = Counters.UpDown("Coffee", 10);
		var client = new FakeCountTogetherClient(coffee);
		await using var connection = await ConnectedAsync(client);

		var result = await connection.SetDateAsync(coffee.Id, new DateOnly(2026, 12, 24), CancellationToken.None);

		Assert.That(result.Failure, Is.EqualTo(CountTogetherFailure.NotDateCounter));
		Assert.That(client.Updates, Is.Empty);
	}

	[Test]
	public async Task Date_counters_carry_their_date()
	{
		var vacation = Counters.ToDate("Vacation", 12, new DateOnly(2026, 10, 23));
		var sober = Counters.FromDate("Smoke free", 365, new DateOnly(2025, 10, 12));
		await using var connection = await ConnectedAsync(new FakeCountTogetherClient(vacation, sober));

		Assert.That(connection.Find(vacation.Id)!.Date, Is.EqualTo(new DateOnly(2026, 10, 23)));
		Assert.That(connection.Find(sober.Id)!.Date, Is.EqualTo(new DateOnly(2025, 10, 12)));
	}

	[Test]
	public async Task Only_up_down_counters_can_be_changed()
	{
		var vacation = Counters.ToDate("Vacation", 12);
		var client = new FakeCountTogetherClient(vacation);
		await using var connection = await ConnectedAsync(client);

		var result = await connection.IncrementAsync(vacation.Id, amount: null, CancellationToken.None);

		Assert.That(result.Failure, Is.EqualTo(CountTogetherFailure.NotUpDown));
		Assert.That(client.Changes, Is.Empty);
	}

	[Test]
	public async Task Nothing_can_be_changed_before_the_integration_is_set_up()
	{
		await using var connection = Create(new FakeClientFactory(new FakeCountTogetherClient()));

		var result = await connection.DecrementAsync(Guid.NewGuid(), amount: null, CancellationToken.None);

		Assert.That(result.Failure, Is.EqualTo(CountTogetherFailure.NotConfigured));
	}

	[Test]
	public async Task Stopping_drops_the_counters_and_disposes_the_client()
	{
		var client = new FakeCountTogetherClient(Counters.UpDown("Coffee", 3));
		await using var connection = await ConnectedAsync(client);

		await connection.StopAsync();

		Assert.That(connection.State, Is.EqualTo(ConnectionState.NotConfigured));
		Assert.That(connection.Counters, Is.Empty);
		Assert.That(client.Disposed, Is.True);
	}

	[Test]
	public async Task Validating_a_token_reports_a_rejection_separately_from_an_outage()
	{
		var rejected = new FakeCountTogetherClient { StartFailure = FakeCountTogetherClient.ApiError(HttpStatusCode.Unauthorized) };
		var offline = new FakeCountTogetherClient { StartFailure = new HttpRequestException("No route to host") };
		await using var connection = Create(new FakeClientFactory(rejected, offline, new FakeCountTogetherClient()));

		Assert.That(await connection.ValidateTokenAsync("a", CancellationToken.None), Is.EqualTo(TokenCheckResult.Rejected));
		Assert.That(await connection.ValidateTokenAsync("b", CancellationToken.None), Is.EqualTo(TokenCheckResult.Unreachable));
		Assert.That(await connection.ValidateTokenAsync("c", CancellationToken.None), Is.EqualTo(TokenCheckResult.Valid));
	}
}

[TestFixture]
public sealed class CountTogetherFailureTests
{
	private static IEnumerable<TestCaseData> Cases()
	{
		yield return new TestCaseData(FakeCountTogetherClient.ApiError(HttpStatusCode.Unauthorized), CountTogetherFailure.Unauthorized);
		yield return new TestCaseData(FakeCountTogetherClient.ApiError(HttpStatusCode.Forbidden), CountTogetherFailure.PermissionDenied);
		yield return new TestCaseData(FakeCountTogetherClient.ApiError(HttpStatusCode.NotFound), CountTogetherFailure.NotFound);
		yield return new TestCaseData(FakeCountTogetherClient.ApiError(HttpStatusCode.ServiceUnavailable), CountTogetherFailure.RateLimited);
		yield return new TestCaseData(FakeCountTogetherClient.ApiError(HttpStatusCode.BadRequest), CountTogetherFailure.Rejected);
		yield return new TestCaseData(FakeCountTogetherClient.ApiError(HttpStatusCode.BadGateway), CountTogetherFailure.Unreachable);
		yield return new TestCaseData(new WebSocketException(WebSocketError.NotAWebSocket,
			"The server returned status code '401' when status code '101' was expected."), CountTogetherFailure.Unauthorized);
		yield return new TestCaseData(new WebSocketException(WebSocketError.NotAWebSocket,
			"The server returned status code '503' when status code '101' was expected."), CountTogetherFailure.Unreachable);
		yield return new TestCaseData(new HttpRequestException("No route to host"), CountTogetherFailure.Unreachable);
		yield return new TestCaseData(new TaskCanceledException(), CountTogetherFailure.Timeout);
	}

	[TestCaseSource(nameof(Cases))]
	public void Exceptions_map_to_the_failure_the_user_sees(Exception exception, CountTogetherFailure expected)
		=> Assert.That(CountTogetherFailures.Classify(exception), Is.EqualTo(expected));
}
