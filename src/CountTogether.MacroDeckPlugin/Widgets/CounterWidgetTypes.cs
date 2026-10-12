using System.Text.Json;
using System.Text.RegularExpressions;
using MacroDeck.Sdk.Widgets;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Model.Surfaces;

namespace CountTogether.MacroDeckPlugin.Widgets;

public static partial class CounterWidgetTypes
{
	public const string CounterTypeId = "counter";
	public const string ButtonsTypeId = "counter-buttons";
	public const string CounterKey = "counterId";

	// The CountTogether app's default accent, used until the user picks an accent colour.
	internal const string DefaultAccent = "#0569ed";
	internal const string OverdueColor = "#ff3b30";

	// The appearance keys stay permissive so the standard appearance actions can always write them.
	private const string DataSchema = """
		{"type":"object","properties":{"counterId":{"type":"string"},"backgroundColor":{"type":"string"},"accentColor":{"type":"string"},"border":{"type":"object"}}}
		""";

	public static IReadOnlyList<WidgetTypeDescriptor> Descriptors { get; } =
	[
		Descriptor(CounterTypeId, Strings.Widgets.Counter.Name(), Strings.Widgets.Counter.Description()),
		Descriptor(ButtonsTypeId, Strings.Widgets.CounterButtons.Name(), Strings.Widgets.CounterButtons.Description()),
	];

	/// <summary>Which of the two types a qualified widget type id (<c>pluginId::localId</c>) names, if any.</summary>
	public static string? LocalIdOf(string? qualifiedTypeId)
		=> qualifiedTypeId switch
		{
			_ when qualifiedTypeId?.EndsWith("::" + ButtonsTypeId, StringComparison.Ordinal) == true => ButtonsTypeId,
			_ when qualifiedTypeId?.EndsWith("::" + CounterTypeId, StringComparison.Ordinal) == true => CounterTypeId,
			_ => null,
		};

	private static WidgetTypeDescriptor Descriptor(string id, MacroDeck.Localization.LocalizedText name,
		MacroDeck.Localization.LocalizedText description)
		=> new(id, name, description, DefaultData: "{}", DataSchema: DataSchema, HasConfiguration: true)
		{
			AppearanceProperties = [WidgetAppearanceProperty.BackgroundColor, WidgetAppearanceProperty.AccentColor],
		};

	internal static string NormalizeColor(string? color, string fallback)
		=> color is not null && HexColor().IsMatch(color) ? color.ToLowerInvariant() : fallback;

	[GeneratedRegex("^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")]
	private static partial Regex HexColor();
}

/// <summary>What a widget's stored data asks for.</summary>
public sealed record CounterWidgetSettings(Guid? CounterId, string Accent, string? Background)
{
	public static CounterWidgetSettings Default { get; } = new(null, CounterWidgetTypes.DefaultAccent, null);

	public static CounterWidgetSettings Parse(JsonElement data)
	{
		if (data.ValueKind != JsonValueKind.Object)
		{
			return Default;
		}

		var appearance = UiWidgetAppearance.Read(data);
		Guid? counterId = data.TryGetProperty(CounterWidgetTypes.CounterKey, out var raw) &&
			raw.ValueKind == JsonValueKind.String &&
			Guid.TryParse(raw.GetString(), out var parsed)
				? parsed
				: null;

		return new CounterWidgetSettings(counterId,
			CounterWidgetTypes.NormalizeColor(appearance.AccentColor, CounterWidgetTypes.DefaultAccent),
			string.IsNullOrWhiteSpace(appearance.BackgroundColor) ? null : appearance.BackgroundColor);
	}
}

internal static class WidgetLayout
{
	// Mirrors the official widgets' safe area: never closer to the edge than the gap between tiles, and far
	// enough from a rounded corner that it does not cut into text. Capped in cell units so a large tile keeps
	// the same inset as a small one.
	private const int BreathingRoom = 12;
	private const int DefaultCornerRadius = 22;

	public static UiSize DefaultPadding { get; } = PaddingFor(DefaultCornerRadius);

	public static UiSize PaddingFor(UiSurface surface)
	{
		ArgumentNullException.ThrowIfNull(surface);

		var radius = surface.Attributes.TryGetValue(UiWidgetSurfaceAttributes.CornerRadius, out var value) &&
			value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
				? parsed
				: DefaultCornerRadius;

		return PaddingFor(radius);
	}

	private static UiSize PaddingFor(int cornerRadius)
	{
		var inset = Math.Max(BreathingRoom, (1 - (1 / Math.Sqrt(2))) * Math.Max(0, cornerRadius));
		return UiSize.Capped(inset / UiLength.Cell, inset);
	}
}
