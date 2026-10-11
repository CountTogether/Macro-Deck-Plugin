using System.Globalization;
using CountTogether.MacroDeckPlugin.Api;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;

namespace CountTogether.MacroDeckPlugin.Actions;

/// <summary>Increments or decrements an up/down counter, by its own step size or by a given amount.</summary>
public sealed class ChangeCounterAction : IDynamicOptionsActionDefinition
{
	public const string IncrementId = "increment-counter";
	public const string DecrementId = "decrement-counter";
	public const string CounterParameter = "counterId";
	public const string AmountParameter = "amount";
	internal const int MaxAmount = 1_000_000;

	private readonly CountTogetherConnection _connection;
	private readonly bool _increment;

	private ChangeCounterAction(CountTogetherConnection connection, bool increment)
	{
		_connection = connection;
		_increment = increment;
	}

	public static ChangeCounterAction Increment(CountTogetherConnection connection) => new(connection, increment: true);

	public static ChangeCounterAction Decrement(CountTogetherConnection connection) => new(connection, increment: false);

	public string Id => _increment ? IncrementId : DecrementId;

	public LocalizedText Name => _increment ? Strings.Actions.Increment.Name() : Strings.Actions.Decrement.Name();

	public LocalizedText Description
		=> _increment ? Strings.Actions.Increment.Description() : Strings.Actions.Decrement.Description();

	public IReadOnlyList<ActionParameter> Parameters { get; } =
	[
		ActionParameter.DynamicChoice(CounterParameter,
			label: Strings.Actions.Parameters.Counter.Label(),
			description: Strings.Actions.Parameters.Counter.Description(),
			required: true),
		ActionParameter.Number(AmountParameter,
			label: Strings.Actions.Parameters.Amount.Label(),
			description: Strings.Actions.Parameters.Amount.Description(),
			min: 1,
			max: MaxAmount,
			step: 1),
	];

	public IActionExecutor CreateExecutor() => new Executor(_connection, _increment);

	public Task<DynamicOptionsResult> GetDynamicOptionsAsync(DynamicOptionsContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		// Counter names are the user's own data, so the option labels are literals.
		var options = _connection.Counters
			.Where(counter => counter.Kind == CounterKind.UpDown)
			.Where(counter => string.IsNullOrWhiteSpace(context.Filter) ||
				counter.Name.Contains(context.Filter, StringComparison.CurrentCultureIgnoreCase))
			.Select(counter => new ActionParameterOption { Value = counter.Id.ToString(), Label = counter.Name })
			.ToList();

		return Task.FromResult(new DynamicOptionsResult
		{
			Options = options,
			Error = _connection.State == ConnectionState.Connected ? default : CounterActionResults.Message(
				_connection.State == ConnectionState.NotConfigured
					? CountTogetherFailure.NotConfigured
					: CountTogetherFailure.NotConnected),
		});
	}

	internal static bool TryReadAmount(object? value, out int? amount)
	{
		amount = null;
		var number = value switch
		{
			null => (double?)null,
			string text when string.IsNullOrWhiteSpace(text) => null,
			string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
			double d => d,
			long l => l,
			int i => i,
			_ => double.NaN,
		};

		if (number is null)
		{
			return true;
		}

		if (double.IsNaN(number.Value) || number.Value != Math.Floor(number.Value) || number.Value is < 1 or > MaxAmount)
		{
			return false;
		}

		amount = (int)number.Value;
		return true;
	}

	private sealed class Executor : IActionExecutor
	{
		private readonly CountTogetherConnection _connection;
		private readonly bool _increment;

		public Executor(CountTogetherConnection connection, bool increment)
		{
			_connection = connection;
			_increment = increment;
		}

		public async Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			ArgumentNullException.ThrowIfNull(context);

			if (context.Parameters.GetValueOrDefault(CounterParameter) is not string rawId || string.IsNullOrWhiteSpace(rawId))
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter,
					MacroDeckStrings.Validation.Required(Strings.Actions.Parameters.Counter.Label()));
			}

			if (!Guid.TryParse(rawId, out var counterId))
			{
				return CounterActionResults.Failed(CountTogetherFailure.NotFound);
			}

			if (!TryReadAmount(context.Parameters.GetValueOrDefault(AmountParameter), out var amount))
			{
				return ActionResult.Failed(ActionErrorCodes.InvalidParameter, Strings.Errors.InvalidAmount());
			}

			var result = _increment
				? await _connection.IncrementAsync(counterId, amount, context.CancellationToken).ConfigureAwait(false)
				: await _connection.DecrementAsync(counterId, amount, context.CancellationToken).ConfigureAwait(false);

			return result.Failure is { } failure ? CounterActionResults.Failed(failure) : ActionResult.Success();
		}
	}
}
