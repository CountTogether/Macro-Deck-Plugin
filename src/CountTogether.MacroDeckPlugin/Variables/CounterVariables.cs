using System.Globalization;
using System.Text;
using CountTogether.MacroDeckPlugin.Api;
using MacroDeck.Sdk.Variables;

namespace CountTogether.MacroDeckPlugin.Variables;

/// <summary>
/// One numeric variable per counter and, for date counters, one text variable with its date. Ids are derived
/// from the counter id and never change; value names keep the Macro Deck 2 scheme
/// (<c>counttogether_&lt;counter name&gt;</c>) so templates read the same as before.
/// </summary>
public static class CounterVariables
{
	public const string NamePrefix = "counttogether_";
	public const string DateSuffix = "_date";
	public const string DateFormat = "yyyy-MM-dd";
	public const string ConnectedId = "connected";

	private const string IdPrefix = "counter-";
	private const string DateIdSuffix = "-date";

	public static IReadOnlyList<VariableDefinition> Build(IReadOnlyList<CounterSnapshot> counters)
	{
		ArgumentNullException.ThrowIfNull(counters);

		var used = new HashSet<string>(StringComparer.Ordinal);
		var definitions = new List<VariableDefinition>(counters.Count);

		// Value names are claimed first, so adding date variables never renames an existing value variable.
		foreach (var counter in counters)
		{
			definitions.Add(VariableDefinition.Eager(Claim(used, NamePrefix + Slug(counter.Name)), VariableType.Numeric,
				decimalPlaces: 0, refreshInterval: TimeSpan.FromSeconds(1)) with
			{
				Id = IdFor(counter.Id),
				DisplayName = counter.Name,
				Description = Strings.Variables.Counter.Description(),
				// Writes are held locally and sent once the value has settled, so a slider can write live.
				Write = counter.Kind == CounterKind.UpDown ? new VariableWriteCapability() : null,
			});
		}

		foreach (var counter in counters.Where(counter => counter.Kind != CounterKind.UpDown))
		{
			definitions.Add(VariableDefinition.Eager(Claim(used, NamePrefix + Slug(counter.Name) + DateSuffix),
				VariableType.Text, refreshInterval: TimeSpan.FromSeconds(5)) with
			{
				Id = DateIdFor(counter.Id),
				DisplayName = Strings.Variables.Date.DisplayName(counter.Name),
				Description = Strings.Variables.Date.Description(),
				Write = new VariableWriteCapability(),
			});
		}

		// Always declared, even before CountTogether answered: Macro Deck only accepts the variables capability
		// (and routes writes to it) when the plugin declares at least one variable while the session starts.
		definitions.Insert(0, VariableDefinition.Eager(Claim(used, NamePrefix + "connected"), VariableType.Boolean) with
		{
			Id = ConnectedId,
			DisplayName = Strings.Variables.Connected.DisplayName(),
			Description = Strings.Variables.Connected.Description(),
		});

		return definitions.Take(VariableLimits.MaxEagerVariablesPerProvider).ToList();
	}

	public static string IdFor(Guid counterId) => IdPrefix + counterId.ToString("N");

	public static string DateIdFor(Guid counterId) => IdFor(counterId) + DateIdSuffix;

	public static bool TryParseId(string localId, out Guid counterId, out bool isDate)
	{
		counterId = default;
		isDate = localId?.EndsWith(DateIdSuffix, StringComparison.Ordinal) == true;
		if (localId is null || !localId.StartsWith(IdPrefix, StringComparison.Ordinal))
		{
			return false;
		}

		var guid = localId.AsSpan(IdPrefix.Length, localId.Length - IdPrefix.Length - (isDate ? DateIdSuffix.Length : 0));
		return Guid.TryParseExact(guid, "N", out counterId);
	}

	public static string FormatDate(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

	public static bool TryParseDate(object? value, out DateOnly date)
	{
		date = default;
		return value is string text &&
			DateOnly.TryParseExact(text.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
	}

	public static bool TryParseValue(object? value, out long number)
	{
		var parsed = value switch
		{
			long l => l,
			int i => i,
			double d => d,
			string text when double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
			_ => double.NaN,
		};

		number = 0;
		if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed != Math.Floor(parsed) || Math.Abs(parsed) > long.MaxValue / 2)
		{
			return false;
		}

		number = (long)parsed;
		return true;
	}

	private static string Claim(HashSet<string> used, string baseName)
	{
		var name = baseName;
		for (var suffix = 2; !used.Add(name); suffix++)
		{
			name = $"{baseName}_{suffix.ToString(CultureInfo.InvariantCulture)}";
		}

		return name;
	}

	/// <summary>
	/// The Macro Deck 2 conversion (lowercase, German umlauts spelled out, every other separator an underscore,
	/// nothing collapsed) so existing templates keep their names; accents are dropped and anything else that a
	/// Macro Deck 3 name cannot hold becomes an underscore too.
	/// </summary>
	public static string Slug(string name)
	{
		var builder = new StringBuilder(name?.Length ?? 0);
		var hasLetterOrDigit = false;

		foreach (var character in (name ?? string.Empty).ToLowerInvariant())
		{
			var piece = character switch
			{
				'ä' => "ae",
				'ö' => "oe",
				'ü' => "ue",
				'ß' => "ss",
				(>= 'a' and <= 'z') or (>= '0' and <= '9') => character.ToString(),
				_ => WithoutAccent(character),
			};

			hasLetterOrDigit |= piece is not null;
			builder.Append(piece ?? "_");
		}

		return hasLetterOrDigit ? builder.ToString() : "counter";
	}

	private static string? WithoutAccent(char character)
	{
		var decomposed = character.ToString().Normalize(NormalizationForm.FormD);
		return decomposed.Length > 1 && decomposed[0] is >= 'a' and <= 'z' &&
			decomposed.Skip(1).All(mark => CharUnicodeInfo.GetUnicodeCategory(mark) == UnicodeCategory.NonSpacingMark)
				? decomposed[0].ToString()
				: null;
	}
}
