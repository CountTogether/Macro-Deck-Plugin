using CountTogether.MacroDeckPlugin.Api;
using MacroDeck.Localization;

namespace CountTogether.MacroDeckPlugin.Widgets;

public enum CounterWidgetStatus
{
	Ready,
	NotConfigured,
	NotSetUp,
	Connecting,
	TokenRejected,
	Unreachable,
	Unavailable
}

public enum CounterCaption
{
	None,
	DaysLeft,
	DaysAgo,
	DaysOverdue
}

/// <summary>Everything a counter tile shows, resolved from the connection and the widget's settings.</summary>
public sealed record CounterWidgetState
{
	public CounterWidgetStatus Status { get; init; }

	public string Name { get; init; } = string.Empty;

	/// <summary>A translated name that replaces <see cref="Name" />, for the picker sample.</summary>
	public LocalizedString? LocalizedName { get; init; }

	/// <summary>The number in the middle of the tile; days for date counters, always shown without a sign.</summary>
	public long? Number { get; init; }

	public bool IsToday { get; init; }

	public CounterCaption Caption { get; init; }

	public bool Overdue { get; init; }

	/// <summary>Whether the buttons are shown: only up/down counters can be changed.</summary>
	public bool CanChange { get; init; }

	public bool ChangeFailed { get; init; }

	public static CounterWidgetState Resolve(Guid? counterId, ConnectionState connection, CounterSnapshot? counter,
		bool withButtons)
		=> connection switch
		{
			ConnectionState.NotConfigured => new() { Status = CounterWidgetStatus.NotSetUp },
			_ when counterId is null => new() { Status = CounterWidgetStatus.NotConfigured },
			ConnectionState.Connecting => new() { Status = CounterWidgetStatus.Connecting },
			ConnectionState.Unauthorized => new() { Status = CounterWidgetStatus.TokenRejected },
			ConnectionState.Unreachable => new() { Status = CounterWidgetStatus.Unreachable },
			_ when counter is null => new() { Status = CounterWidgetStatus.Unavailable },
			_ => For(counter, withButtons),
		};

	public static CounterWidgetState For(CounterSnapshot counter, bool withButtons)
	{
		ArgumentNullException.ThrowIfNull(counter);

		var ready = new CounterWidgetState { Status = CounterWidgetStatus.Ready, Name = counter.Name, Number = counter.Value };
		if (counter.Kind == CounterKind.UpDown)
		{
			return ready with { CanChange = withButtons };
		}

		return counter.Value switch
		{
			null => ready,
			0 => ready with { Number = null, IsToday = true },
			{ } days when counter.Kind == CounterKind.FromDate => ready with
			{
				Number = Math.Abs(days),
				Caption = CounterCaption.DaysAgo,
			},
			> 0 => ready with { Caption = CounterCaption.DaysLeft },
			{ } days => ready with { Number = Math.Abs(days), Caption = CounterCaption.DaysOverdue, Overdue = true },
		};
	}
}
