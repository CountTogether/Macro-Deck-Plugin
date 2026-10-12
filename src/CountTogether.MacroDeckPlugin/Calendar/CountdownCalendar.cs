using CountTogether.MacroDeckPlugin.Api;
using MacroDeck.Sdk.Calendar;

namespace CountTogether.MacroDeckPlugin.Calendar;

/// <summary>
/// Shows to-date counters as calendars: one calendar per counter, holding one all-day event on its target date,
/// so Macro Deck's calendar widget, triggers and filters work per counter.
/// </summary>
public static class CountdownCalendar
{
	// One account, because the integration allows one configuration. Persisted by the host in every calendar
	// widget and trigger filter, so it never changes.
	public const string AccountId = "counttogether";

	internal const string Color = "#0569ED";

	public static IReadOnlyList<CalendarInfo> Calendars(IEnumerable<CounterSnapshot> counters)
		=> [.. Countdowns(counters).Select(counter => new CalendarInfo { Id = IdOf(counter), Name = counter.Name, Color = Color })];

	/// <summary>
	/// The countdowns whose target day overlaps the query. A day either side is included because the host places
	/// all-day events in its own time zone and filters the result again.
	/// </summary>
	public static IReadOnlyList<CalendarEvent> Events(IEnumerable<CounterSnapshot> counters, CalendarEventQuery query)
	{
		ArgumentNullException.ThrowIfNull(query);

		var first = DateOnly.FromDateTime(query.From.Date).AddDays(-1);
		var last = DateOnly.FromDateTime(query.To.Date).AddDays(1);
		var calendars = query.CalendarIds.Count == 0 ? null : query.CalendarIds.ToHashSet(StringComparer.Ordinal);

		return
		[
			.. Countdowns(counters)
				.Where(counter => counter.Date >= first && counter.Date <= last)
				.Where(counter => calendars is null || calendars.Contains(IdOf(counter)))
				.Select(EventOf),
		];
	}

	public static CalendarEvent? Event(IEnumerable<CounterSnapshot> counters, string calendarId, string eventId)
		=> calendarId == eventId
			? Countdowns(counters).Where(counter => IdOf(counter) == eventId).Select(EventOf).FirstOrDefault()
			: null;

	internal static string IdOf(CounterSnapshot counter) => counter.Id.ToString("N");

	private static IEnumerable<CounterSnapshot> Countdowns(IEnumerable<CounterSnapshot> counters)
		=> counters.Where(counter => counter.Kind == CounterKind.ToDate && counter.Date is not null);

	private static CalendarEvent EventOf(CounterSnapshot counter)
	{
		var start = new DateTimeOffset(counter.Date!.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
		return new CalendarEvent
		{
			Id = IdOf(counter),
			CalendarId = IdOf(counter),
			Title = counter.Name,
			Start = start,
			End = start.AddDays(1),
			IsAllDay = true,
		};
	}
}
