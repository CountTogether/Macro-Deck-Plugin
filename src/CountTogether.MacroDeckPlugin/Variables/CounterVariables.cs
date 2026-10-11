using System.Globalization;
using System.Text;
using CountTogether.MacroDeckPlugin.Api;
using MacroDeck.Sdk.Variables;

namespace CountTogether.MacroDeckPlugin.Variables;

/// <summary>
/// One numeric variable per counter. The id is derived from the counter id and never changes; the name keeps
/// the Macro Deck 2 scheme (<c>counttogether_&lt;counter name&gt;</c>) so templates read the same as before.
/// </summary>
public static class CounterVariables
{
	public const string NamePrefix = "counttogether_";

	private const string IdPrefix = "counter-";

	public static IReadOnlyList<VariableDefinition> Build(IReadOnlyList<CounterSnapshot> counters)
	{
		ArgumentNullException.ThrowIfNull(counters);

		var used = new HashSet<string>(StringComparer.Ordinal);
		var definitions = new List<VariableDefinition>(counters.Count);

		foreach (var counter in counters.Take(VariableLimits.MaxEagerVariablesPerProvider))
		{
			var baseName = NamePrefix + Slug(counter.Name);
			var name = baseName;
			for (var suffix = 2; !used.Add(name); suffix++)
			{
				name = $"{baseName}_{suffix.ToString(CultureInfo.InvariantCulture)}";
			}

			definitions.Add(VariableDefinition.Eager(name, VariableType.Numeric, decimalPlaces: 0,
				refreshInterval: TimeSpan.FromSeconds(1)) with
			{
				Id = IdFor(counter.Id),
				DisplayName = counter.Name,
				Description = Strings.Variables.Counter.Description(),
			});
		}

		return definitions;
	}

	public static string IdFor(Guid counterId) => IdPrefix + counterId.ToString("N");

	public static bool TryParseId(string localId, out Guid counterId)
	{
		counterId = default;
		return localId is not null &&
			localId.StartsWith(IdPrefix, StringComparison.Ordinal) &&
			Guid.TryParseExact(localId.AsSpan(IdPrefix.Length), "N", out counterId);
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
