using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Runtime;

namespace CountTogether.MacroDeckPlugin.Widgets;

/// <summary>Serves a <see cref="UiView" /> as a session and releases it when the session closes.</summary>
internal class ViewSession : IUiSession
{
	private readonly UiView _view;

	public ViewSession(UiView view)
	{
		_view = view;
		_view.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
		_view.HandlerFaulted += (_, fault)
			=> Faulted?.Invoke(this, new UiSessionFaultedEventArgs(fault.Exception.Message, fault.Exception));
	}

	public event EventHandler? Changed;

	public event EventHandler<UiSessionFaultedEventArgs>? Faulted;

	public UiTree BuildTree() => _view.Tree;

	public IReadOnlyList<UiPatch> DrainPatches() => _view.DrainPatches();

	public void Dispatch(UiEvent uiEvent) => _view.Dispatch(uiEvent);

	public virtual ValueTask DisposeAsync()
	{
		_view.Dispose();
		return ValueTask.CompletedTask;
	}
}
