using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;

namespace CountTogether.MacroDeckPlugin.Widgets;

/// <summary>What a press on one of the buttons does. Absent for samples and previews.</summary>
public sealed record CounterWidgetCommands(Func<CancellationToken, Task> Increment, Func<CancellationToken, Task> Decrement);

/// <summary>
/// The counter tile, modelled on the CountTogether iOS home screen widgets: the counter name on top, the value
/// large in the accent colour, a caption for date counters and, for the buttons type, a row of minus and plus.
/// </summary>
public static class CounterWidgetView
{
	private const double Gap = 0.025;
	private const double NameSize = 0.1;
	private const double ValueSize = 0.3;
	private const double CaptionSize = 0.08;
	private const double ButtonRowSize = 0.26;
	private const double ButtonIconSize = 0.13;
	private const double MessageIconSize = 0.15;
	private const double MessageTitleSize = 0.09;
	private const double MessageHintSize = 0.07;

	// The accent at about 15 % opacity, like the iOS button background.
	private const string ButtonTintAlpha = "26";

	public static UiElement Build(UiState<CounterWidgetState> state, CounterWidgetSettings settings, bool withButtons,
		CounterWidgetCommands? commands, UiSize padding)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(settings);

		return new UiStack
		{
			Key = "counter",
			Justify = UiComponentJustify.Center,
			Padding = padding,
			Background = settings.Background is { } background ? background : UiValue.None<string>(),
			Children =
			[
				new UiWhen
				{
					Key = "ready-gate",
					Condition = () => state.Value.Status == CounterWidgetStatus.Ready,
					Content = () => Ready(state, settings, withButtons, commands),
				},
				new UiWhen
				{
					Key = "message-gate",
					Condition = () => state.Value.Status != CounterWidgetStatus.Ready,
					Content = () => Message(state, settings),
				},
			],
		};
	}

	private static UiStack Ready(UiState<CounterWidgetState> state, CounterWidgetSettings settings, bool withButtons,
		CounterWidgetCommands? commands)
		=> new()
		{
			Key = "ready",
			Fill = true,
			Gap = Gap,
			Children =
			[
				new UiTextRun
				{
					Key = "name",
					Text = UiText.Optional(() => state.Value.LocalizedName is { } localized
						? UiText.Of(localized)
						: UiText.Of(state.Value.Name)),
					Size = NameSize,
					MinSize = NameSize * 0.7,
					Weight = UiComponentTextWeights.SemiBold,
					Role = UiComponentTextRoles.Secondary,
					Align = UiComponentAlignments.Center,
				},
				new UiStack
				{
					Key = "reading",
					Fill = true,
					Justify = UiComponentJustify.Center,
					Gap = Gap,
					Children =
					[
						new UiTextRun
						{
							Key = "value",
							Text = UiText.Optional(() => ValueText(state.Value)),
							Size = ValueSize,
							MinSize = ValueSize * 0.33,
							Weight = UiComponentTextWeights.Bold,
							Color = UiValue.From(() => state.Value.Overdue ? CounterWidgetTypes.OverdueColor : settings.Accent),
							Align = UiComponentAlignments.Center,
						},
						new UiWhen
						{
							Key = "caption-gate",
							Condition = () => state.Value.Caption != CounterCaption.None,
							Content = () => new UiTextRun
							{
								Key = "caption",
								Text = UiText.FromLocalized(() => CaptionText(state.Value)),
								Size = CaptionSize,
								MinSize = CaptionSize * 0.7,
								Weight = UiComponentTextWeights.Medium,
								Role = UiComponentTextRoles.Secondary,
								Align = UiComponentAlignments.Center,
							},
						},
						new UiWhen
						{
							Key = "failure-gate",
							Condition = () => state.Value.ChangeFailed,
							Content = () => new UiTextRun
							{
								Key = "failure",
								Text = Strings.Widgets.ChangeFailed(),
								Size = CaptionSize,
								MinSize = CaptionSize * 0.7,
								Color = CounterWidgetTypes.OverdueColor,
								Align = UiComponentAlignments.Center,
							},
						},
					],
				},
				new UiWhen
				{
					Key = "buttons-gate",
					Condition = () => withButtons && state.Value.CanChange,
					Content = () => Buttons(settings, commands),
				},
			],
		};

	private static UiStack Buttons(CounterWidgetSettings settings, CounterWidgetCommands? commands)
		=> new()
		{
			Key = "buttons",
			Direction = UiComponentDirections.Horizontal,
			MainSize = ButtonRowSize,
			Gap = Gap * 2,
			Children =
			[
				Button("decrement", UiIcons.Minus, settings, commands?.Decrement),
				Button("increment", UiIcons.Plus, settings, commands?.Increment),
			],
		};

	private static UiButton Button(string key, string icon, CounterWidgetSettings settings,
		Func<CancellationToken, Task>? command)
		=> new()
		{
			Key = key,
			Fill = true,
			Justify = UiComponentJustify.Center,
			Align = UiComponentAlignments.Center,
			Background = settings.Accent[..7] + ButtonTintAlpha,
			Events = command is null ? [] : [UiEventHandler.OnAsync(UiComponentEvents.Press, command)],
			Children = [new UiIcon { Key = key + "-icon", Icon = icon, Size = ButtonIconSize, Color = settings.Accent }],
		};

	private static UiStack Message(UiState<CounterWidgetState> state, CounterWidgetSettings settings)
		=> new()
		{
			Key = "message",
			Fill = true,
			Justify = UiComponentJustify.Center,
			Align = UiComponentAlignments.Center,
			Gap = Gap,
			Children =
			[
				new UiIcon
				{
					Key = "message-icon",
					Icon = UiValue.From(() => MessageIcon(state.Value.Status)),
					Size = MessageIconSize,
					Color = UiValue.Optional(() => state.Value.Status == CounterWidgetStatus.NotConfigured
						? settings.Accent
						: UiValue.None<string>()),
					Role = UiComponentTextRoles.Secondary,
				},
				new UiTextRun
				{
					Key = "message-title",
					Text = UiText.FromLocalized(() => MessageTitle(state.Value.Status)),
					Size = MessageTitleSize,
					MinSize = MessageTitleSize * 0.7,
					Weight = UiComponentTextWeights.SemiBold,
					Role = UiComponentTextRoles.Secondary,
					Align = UiComponentAlignments.Center,
					Wrap = true,
					MaxLines = 2,
				},
				new UiWhen
				{
					Key = "message-hint-gate",
					Condition = () => state.Value.Status != CounterWidgetStatus.Connecting,
					Content = () => new UiTextRun
					{
						Key = "message-hint",
						Text = UiText.FromLocalized(() => MessageHint(state.Value.Status)),
						Size = MessageHintSize,
						MinSize = MessageHintSize * 0.75,
						Role = UiComponentTextRoles.Muted,
						Align = UiComponentAlignments.Center,
						Wrap = true,
						MaxLines = 3,
					},
				},
			],
		};

	private static UiText ValueText(CounterWidgetState state)
		=> state switch
		{
			{ IsToday: true } => UiText.Of(Strings.Widgets.Value.Today()),
			{ Number: { } number } => UiText.Of(number.ToString(CultureInfo.InvariantCulture)),
			_ => UiText.Of(Strings.Widgets.Value.Missing()),
		};

	private static LocalizedString CaptionText(CounterWidgetState state)
	{
		var count = (int)Math.Clamp(state.Number ?? 0, int.MinValue, int.MaxValue);
		return state.Caption switch
		{
			CounterCaption.DaysAgo => Strings.Widgets.Caption.DaysAgo(count),
			CounterCaption.DaysOverdue => Strings.Widgets.Caption.DaysOverdue(count),
			_ => Strings.Widgets.Caption.DaysLeft(count),
		};
	}

	private static string MessageIcon(CounterWidgetStatus status)
		=> status switch
		{
			CounterWidgetStatus.NotConfigured => UiIcons.Pencil,
			CounterWidgetStatus.NotSetUp => UiIcons.Settings,
			CounterWidgetStatus.Connecting => UiIcons.Refresh,
			CounterWidgetStatus.TokenRejected => UiIcons.Lock,
			CounterWidgetStatus.Unreachable => UiIcons.Wifi,
			_ => UiIcons.AlertTriangle,
		};

	private static LocalizedString MessageTitle(CounterWidgetStatus status)
		=> status switch
		{
			CounterWidgetStatus.NotConfigured => Strings.Widgets.State.NotConfigured.Title(),
			CounterWidgetStatus.NotSetUp => Strings.Widgets.State.NotSetUp.Title(),
			CounterWidgetStatus.Connecting => Strings.Widgets.State.Connecting(),
			CounterWidgetStatus.TokenRejected => Strings.Widgets.State.TokenRejected.Title(),
			CounterWidgetStatus.Unreachable => Strings.Widgets.State.Unreachable.Title(),
			_ => Strings.Widgets.State.Unavailable.Title(),
		};

	private static LocalizedString MessageHint(CounterWidgetStatus status)
		=> status switch
		{
			CounterWidgetStatus.NotConfigured => Strings.Widgets.State.NotConfigured.Hint(),
			CounterWidgetStatus.NotSetUp => Strings.Widgets.State.NotSetUp.Hint(),
			CounterWidgetStatus.TokenRejected => Strings.Widgets.State.TokenRejected.Hint(),
			CounterWidgetStatus.Unreachable => Strings.Widgets.State.Unreachable.Hint(),
			_ => Strings.Widgets.State.Unavailable.Hint(),
		};
}
