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
/// <param name="Value">The count for up/down counters, otherwise whole days: elapsed for a from-date counter,
/// remaining for a to-date counter (negative once it is overdue).</param>
public sealed record CounterSnapshot(Guid Id, string Name, CounterKind Kind, long? Value, DateTime? Created = null)
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

		return kind is { } known
			? new CounterSnapshot(counter.Id, counter.Name, known, counter.Value, counter.Created ?? previous?.Created)
			: null;
	}
}
