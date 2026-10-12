using CountTogether.PublicApiSdk.Models;

namespace CountTogether.MacroDeckPlugin.Api;

public enum CounterKind
{
	UpDown,
	FromDate,
	ToDate
}

/// <summary>
/// A counter as the plugin shows it. Kept separate from the SDK model so variables, events and widgets do not
/// depend on the CountTogether wire format.
/// </summary>
/// <param name="Value">The count for regular counters, otherwise whole days: elapsed for a from-date counter,
/// remaining for a to-date counter (negative once it is overdue).</param>
/// <param name="Date">The start date of a from-date counter or the target date of a to-date counter.</param>
public sealed record CounterSnapshot(Guid Id, string Name, CounterKind Kind, long? Value, DateTime? Created = null,
	DateOnly? Date = null)
{
	public static CounterSnapshot? From(Counter counter, CounterSnapshot? previous = null)
	{
		ArgumentNullException.ThrowIfNull(counter);

		// Push updates may omit the type, which the SDK reads as Unknown, so the last known kind wins.
		CounterKind? kind = counter.Type switch
		{
			CounterType.UpDown => CounterKind.UpDown,
			CounterType.FromDate => CounterKind.FromDate,
			CounterType.ToDate => CounterKind.ToDate,
			_ => previous?.Kind,
		};

		if (kind is not { } known)
		{
			return null;
		}

		var date = known switch
		{
			CounterKind.FromDate => counter.StartDate,
			CounterKind.ToDate => counter.EndDate,
			_ => null,
		};

		return new CounterSnapshot(counter.Id, counter.Name, known, counter.Value, counter.Created ?? previous?.Created,
			date ?? (previous?.Kind == known ? previous.Date : null));
	}
}
