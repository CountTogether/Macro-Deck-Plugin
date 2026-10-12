using System.Text.Json;
using CountTogether.MacroDeckPlugin.Api;
using CountTogether.MacroDeckPlugin.Widgets;
using MacroDeck.Ui.Runtime;
using MacroDeck.Ui.Testing;
using NUnit.Framework;

namespace CountTogether.MacroDeckPlugin.Tests;

[TestFixture]
public sealed class CounterWidgetStateTests
{
	private static readonly Guid CounterId = Guid.NewGuid();

	[Test]
	public void An_unconfigured_integration_wins_over_a_missing_counter_choice()
		=> Assert.That(CounterWidgetState.Resolve(null, ConnectionState.NotConfigured, null, false).Status,
			Is.EqualTo(CounterWidgetStatus.NotSetUp));

	[Test]
	public void A_widget_without_a_counter_asks_for_one()
		=> Assert.That(CounterWidgetState.Resolve(null, ConnectionState.Connected, null, false).Status,
			Is.EqualTo(CounterWidgetStatus.NotConfigured));

	[TestCase(ConnectionState.Connecting, CounterWidgetStatus.Connecting)]
	[TestCase(ConnectionState.Unauthorized, CounterWidgetStatus.TokenRejected)]
	[TestCase(ConnectionState.Unreachable, CounterWidgetStatus.Unreachable)]
	public void Connection_problems_are_shown(ConnectionState connection, CounterWidgetStatus expected)
		=> Assert.That(CounterWidgetState.Resolve(CounterId, connection, null, false).Status, Is.EqualTo(expected));

	[Test]
	public void A_counter_that_is_gone_is_unavailable()
		=> Assert.That(CounterWidgetState.Resolve(CounterId, ConnectionState.Connected, null, false).Status,
			Is.EqualTo(CounterWidgetStatus.Unavailable));

	[Test]
	public void Only_up_down_counters_get_buttons()
	{
		var upDown = CounterWidgetState.For(new CounterSnapshot(CounterId, "Coffee", CounterKind.UpDown, 3), withButtons: true);
		var date = CounterWidgetState.For(new CounterSnapshot(CounterId, "Vacation", CounterKind.ToDate, 3), withButtons: true);

		Assert.That(upDown.CanChange, Is.True);
		Assert.That(date.CanChange, Is.False);
	}

	[TestCase(CounterKind.ToDate, 12L, 12L, CounterCaption.DaysLeft, false)]
	[TestCase(CounterKind.ToDate, -3L, 3L, CounterCaption.DaysOverdue, true)]
	[TestCase(CounterKind.FromDate, 365L, 365L, CounterCaption.DaysAgo, false)]
	public void Date_counters_show_whole_days_with_a_caption(CounterKind kind, long value, long shown, CounterCaption caption,
		bool overdue)
	{
		var state = CounterWidgetState.For(new CounterSnapshot(CounterId, "Date", kind, value), withButtons: false);

		Assert.That(state.Number, Is.EqualTo(shown));
		Assert.That(state.Caption, Is.EqualTo(caption));
		Assert.That(state.Overdue, Is.EqualTo(overdue));
	}

	[TestCase(CounterKind.ToDate)]
	[TestCase(CounterKind.FromDate)]
	public void The_day_itself_reads_today(CounterKind kind)
		=> Assert.That(CounterWidgetState.For(new CounterSnapshot(CounterId, "Date", kind, 0), false).IsToday, Is.True);
}

[TestFixture]
public sealed class CounterWidgetViewTests
{
	private static UiTestHost Render(CounterWidgetState state, bool withButtons, CounterWidgetCommands? commands = null)
		=> UiTestHost.Render(CounterWidgetView.Build(new UiState<CounterWidgetState>(state), CounterWidgetSettings.Default,
			withButtons, commands, WidgetLayout.DefaultPadding));

	[Test]
	public void The_value_is_drawn_in_the_accent_colour()
	{
		var host = Render(new CounterWidgetState { Status = CounterWidgetStatus.Ready, Name = "Coffee", Number = 42 }, false);

		var value = host.ByText("42").Single();
		Assert.That(value.Text("color"), Is.EqualTo(CounterWidgetTypes.DefaultAccent));
		Assert.That(host.ByText("Coffee"), Is.Not.Empty);
		Assert.That(host.ByType("ui.button"), Is.Empty);
	}

	[Test]
	public async Task The_buttons_change_the_counter()
	{
		var pressed = new List<string>();
		var commands = new CounterWidgetCommands(
			_ => { pressed.Add("increment"); return Task.CompletedTask; },
			_ => { pressed.Add("decrement"); return Task.CompletedTask; });
		var host = Render(new CounterWidgetState { Status = CounterWidgetStatus.Ready, Name = "Coffee", Number = 42, CanChange = true },
			withButtons: true, commands);

		var buttons = host.ByType("ui.button");
		Assert.That(buttons, Has.Count.EqualTo(2));
		buttons.Single(button => button.Id.EndsWith("increment", StringComparison.Ordinal)).Raise("press");
		await host.SettleAsync();

		Assert.That(pressed, Is.EqualTo(new[] { "increment" }));
	}

	[Test]
	public void A_state_change_updates_the_tile_in_place()
	{
		var state = new UiState<CounterWidgetState>(new CounterWidgetState { Status = CounterWidgetStatus.Connecting });
		var host = UiTestHost.Render(CounterWidgetView.Build(state, CounterWidgetSettings.Default, false, null,
			WidgetLayout.DefaultPadding));

		state.Value = new CounterWidgetState { Status = CounterWidgetStatus.Ready, Name = "Coffee", Number = 7 };

		Assert.That(host.ByText("7"), Is.Not.Empty);
	}

	[Test]
	public void Settings_read_the_counter_and_a_valid_accent()
	{
		var counterId = Guid.NewGuid();
		using var data = JsonDocument.Parse($$"""{"counterId":"{{counterId}}","accentColor":"#E91E63CC"}""");

		var settings = CounterWidgetSettings.Parse(data.RootElement);

		Assert.That(settings.CounterId, Is.EqualTo(counterId));
		Assert.That(settings.Accent, Is.EqualTo("#e91e63cc"));
	}
}
