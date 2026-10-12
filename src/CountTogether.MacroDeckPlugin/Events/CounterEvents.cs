using CountTogether.MacroDeckPlugin.Api;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Events;

namespace CountTogether.MacroDeckPlugin.Events;

public static class CounterEvents
{
	public const string ValueChangedId = "counter-value-changed";
	public const string DeletedId = "counter-deleted";
	public const string CounterParameter = "counterId";
	public const string CounterNameParameter = "counterName";
	public const string ValueParameter = "value";
	public const string PreviousValueParameter = "previousValue";
	public const string ChangeParameter = "change";

	public static IReadOnlyList<EventDefinition> Definitions { get; } =
	[
		new EventDefinition
		{
			Id = ValueChangedId,
			Name = Strings.Events.ValueChanged.Name(),
			Description = Strings.Events.ValueChanged.Description(),
			Category = Strings.Events.Category(),
			ConfigurationParameters = [CounterFilter()],
			PayloadParameters =
			[
				CounterPayload(),
				CounterNamePayload(),
				ActionParameter.Number(ValueParameter, label: Strings.Events.Parameters.Value.Label()),
				ActionParameter.Number(PreviousValueParameter, label: Strings.Events.Parameters.PreviousValue.Label()),
				ActionParameter.Number(ChangeParameter, label: Strings.Events.Parameters.Change.Label()),
			],
		},
		new EventDefinition
		{
			Id = DeletedId,
			Name = Strings.Events.Deleted.Name(),
			Description = Strings.Events.Deleted.Description(),
			Category = Strings.Events.Category(),
			ConfigurationParameters = [CounterFilter()],
			PayloadParameters = [CounterPayload(), CounterNamePayload()],
		},
	];

	/// <summary>The counter id travels in the same format as the picker's option values, so the host's
	/// configuration-to-payload match compares equal strings.</summary>
	public static string FormatCounterId(Guid counterId) => counterId.ToString();

	public static IReadOnlyDictionary<string, object?> ValueChangedPayload(CounterSnapshot previous, CounterSnapshot current)
	{
		ArgumentNullException.ThrowIfNull(previous);
		ArgumentNullException.ThrowIfNull(current);

		return new Dictionary<string, object?>
		{
			[CounterParameter] = FormatCounterId(current.Id),
			[CounterNameParameter] = current.Name,
			[ValueParameter] = current.Value,
			[PreviousValueParameter] = previous.Value,
			[ChangeParameter] = current.Value - previous.Value,
		};
	}

	public static IReadOnlyDictionary<string, object?> DeletedPayload(CounterSnapshot counter)
	{
		ArgumentNullException.ThrowIfNull(counter);

		return new Dictionary<string, object?>
		{
			[CounterParameter] = FormatCounterId(counter.Id),
			[CounterNameParameter] = counter.Name,
		};
	}

	private static ActionParameter CounterFilter()
		=> ActionParameter.DynamicChoice(CounterParameter,
			label: Strings.Events.Parameters.Counter.Label(),
			description: Strings.Events.Parameters.Counter.Description());

	private static ActionParameter CounterPayload()
		=> ActionParameter.DynamicChoice(CounterParameter, label: Strings.Events.Parameters.Counter.Label());

	private static ActionParameter CounterNamePayload()
		=> ActionParameter.Text(CounterNameParameter, label: Strings.Events.Parameters.CounterName.Label());
}
