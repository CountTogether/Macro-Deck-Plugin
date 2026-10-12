using System.Text.Json;
using CountTogether.MacroDeckPlugin.Api;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Config.Options;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;

namespace CountTogether.MacroDeckPlugin.Widgets;

public static class CounterWidgetConfigView
{
	public static UiElement Build(JsonElement data, IReadOnlyList<CounterSnapshot> counters, bool withButtons)
	{
		ArgumentNullException.ThrowIfNull(counters);

		var stored = data.ValueKind == JsonValueKind.Object &&
			data.TryGetProperty(CounterWidgetTypes.CounterKey, out var raw) &&
			raw.ValueKind == JsonValueKind.String
				? raw.GetString() ?? string.Empty
				: string.Empty;
		var counter = new UiState<string>(stored);

		// Counter names are the user's own data, so the option labels are literals.
		var options = counters
			.Where(candidate => !withButtons || candidate.Kind == CounterKind.UpDown)
			.Select(candidate => UiOption.Of(candidate.Id.ToString(), candidate.Name))
			.ToList();

		// Keeps a counter that is not listed right now (offline, or deleted) so saving does not drop it.
		if (stored.Length > 0 && options.All(option => !string.Equals(option.Value, stored, StringComparison.OrdinalIgnoreCase)))
		{
			options.Add(UiOption.Of(stored, Strings.Widgets.Config.Counter.Unknown()));
		}

		return new UiWidgetConfiguration
		{
			Key = "root",
			Properties = new UiWidgetProperties
			{
				Key = "properties",
				Children =
				[
					new UiChoiceInput
					{
						Key = CounterWidgetTypes.CounterKey,
						Label = Strings.Widgets.Config.Counter.Label(),
						Description = Strings.Widgets.Config.Counter.Description(),
						Placeholder = Strings.Widgets.Config.Counter.Placeholder(),
						Binding = Bind.To(counter),
						Options = UiValue.Of<IReadOnlyList<UiOption>>(options),
					},
					UiWidgetAppearance.Section(data, UiWidgetAppearanceFields.BackgroundColor | UiWidgetAppearanceFields.AccentColor),
				],
			},
		};
	}
}
