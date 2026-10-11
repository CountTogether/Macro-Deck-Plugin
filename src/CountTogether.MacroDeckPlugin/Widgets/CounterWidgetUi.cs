using System.Text.Json;
using CountTogether.MacroDeckPlugin.Api;
using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;

namespace CountTogether.MacroDeckPlugin.Widgets;

/// <summary>Serves the tiles, the picker samples and the configuration views of both widget types.</summary>
public sealed class CounterWidgetUi
{
	private readonly CountTogetherConnection _connection;
	private readonly TimeProvider _time;

	public CounterWidgetUi(CountTogetherConnection connection, TimeProvider time)
	{
		_connection = connection;
		_time = time;
	}

	public static IReadOnlyList<UiSurfaceDeclaration> Surfaces { get; } =
	[
		new() { Kind = UiSurfaceKinds.Widget, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Preview, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Config, SessionMode = UiSessionModes.Exclusive },
	];

	public IUiSession? CreateSession(UiSurface surface)
	{
		ArgumentNullException.ThrowIfNull(surface);

		switch (surface.Kind)
		{
			case UiSurfaceKinds.Widget or UiSurfaceKinds.Preview:
			{
				var type = CounterWidgetTypes.LocalIdOf(ReadString(surface, UiWidgetSurfaceAttributes.WidgetType));
				if (type is null)
				{
					return null;
				}

				var withButtons = type == CounterWidgetTypes.ButtonsTypeId;
				var settings = CounterWidgetSettings.Parse(Read(surface, UiWidgetSurfaceAttributes.Data));

				return IsTrue(surface, UiWidgetSurfaceAttributes.Sample)
					? new ViewSession(new UiView(surface, CounterWidgetView.Build(
						new UiState<CounterWidgetState>(Sample(withButtons)), settings, withButtons, commands: null,
						WidgetLayout.PaddingFor(surface))))
					: CounterWidgetSession.Create(surface, settings, withButtons, _connection, _time);
			}

			case UiSurfaceKinds.Config when ReadString(surface, UiConfigSurfaceAttributes.EntryPoint) == UiConfigEntryPoints.WidgetConfig:
			{
				var type = CounterWidgetTypes.LocalIdOf(ReadString(surface, UiConfigSurfaceAttributes.WidgetType));
				return type is null
					? null
					: new ViewSession(new UiView(surface, CounterWidgetConfigView.Build(
						Read(surface, UiConfigSurfaceAttributes.WidgetData),
						_connection.Counters,
						withButtons: type == CounterWidgetTypes.ButtonsTypeId)));
			}

			default:
				return null;
		}
	}

	/// <summary>What the widget picker shows: a representative tile that reads nothing live.</summary>
	internal static CounterWidgetState Sample(bool withButtons)
		=> new()
		{
			Status = CounterWidgetStatus.Ready,
			LocalizedName = Strings.Widgets.Sample.CounterName(),
			Number = 3,
			CanChange = withButtons,
		};

	private static JsonElement Read(UiSurface surface, string name)
		=> surface.Attributes.TryGetValue(name, out var element) ? element : default;

	private static string? ReadString(UiSurface surface, string name)
		=> Read(surface, name) is { ValueKind: JsonValueKind.String } element ? element.GetString() : null;

	private static bool IsTrue(UiSurface surface, string name) => Read(surface, name).ValueKind == JsonValueKind.True;
}
