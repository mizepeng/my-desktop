using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MyDesktop.Models;
using MyDesktop.Native;
using MyDesktop.Services;
using Windows.Foundation;
using DrawRectangle = System.Drawing.Rectangle;

namespace MyDesktop.WinUI.Views;

public sealed partial class FenceView
{
	const float TitleThickness = 36;
	readonly HashSet<MenuFlyout> _openMenus = [];
	DispatcherQueueTimer _collapseTimer = null!;
	bool _temporaryExpanded, _pointerInside, _dropInside;
	double _reveal = 1;
	int _rollVersion;
	Task _rollTask = Task.CompletedTask;
	internal Func<bool>? RollInteractionHeld { get; set; }
	/// <summary>
	/// 临时展开开始时请求提升所在的收起分区浮层，收回动画结束后请求放回桌面层。
	/// </summary>
	internal event Action<bool>? RaiseRequested;
	internal event Action? RolledChanged;
	internal bool IsTemporarilyExpanded => _temporaryExpanded;
	internal bool IsDropInside => _dropInside;
	internal bool IsCollapsed => _frame.Active.RolledUp && !_temporaryExpanded && !IsEditing;
	internal bool IsVerticalRoll => _frame.Active.RollEdge is RollEdge.Left or RollEdge.Right;
	internal Task RollReady => _rollTask;
	internal bool IsRolling { get; private set; }
	internal Rect RevealedBounds
	{
		get
		{
			return RevealAt(_reveal);
		}
	}

	Rect RevealAt(double progress)
	{
		var area = ClampTemporary(new(0, 0, Width, Height));
		var rect = FenceRoll.Reveal(new((float)area.Width, (float)area.Height), _frame.Active.RollEdge, TitleThickness, progress);
		return new(area.X + rect.X, area.Y + rect.Y, rect.Width, rect.Height);
	}
	internal Rect ContentBounds
	{
		get
		{
			var rect = FenceRoll.Content(new((float)Width, (float)Height), _frame.Active.RollEdge, TitleThickness);
			return ClampTemporary(new(rect.X, rect.Y, rect.Width, rect.Height));
		}
	}
	internal Rect HeaderBounds
	{
		get
		{
			var rect = FenceRoll.Reveal(new((float)Width, (float)Height), _frame.Active.RollEdge, TitleThickness, 0);
			return new(rect.X, rect.Y, rect.Width, rect.Height);
		}
	}

	Rect ClampTemporary(Rect rect)
	{
		if (!_temporaryExpanded && _owner == _desktopOwner) { return rect; }
		var window = NativeMethods.GetWindowRect(_owner);
		double scale = NativeMethods.GetDpiForWindow(_owner) / 96.0;
		double left = -Canvas.GetLeft(this) - _visual.TransformMatrix.Translation.X, top = -Canvas.GetTop(this) - _visual.TransformMatrix.Translation.Y;
		double x = Math.Max(rect.Left, left), y = Math.Max(rect.Top, top);
		return new Rect(x, y, Math.Max(0, Math.Min(rect.Right, left + window.Width / scale) - x), Math.Max(0, Math.Min(rect.Bottom, top + window.Height / scale) - y));
	}

	void InitializeRoll()
	{
		_reveal = _frame.Active.RolledUp ? 0 : 1;
		_collapseTimer = DispatcherQueue.CreateTimer();
		_collapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.CollapseDelay);
		_collapseTimer.Tick += (_, _) =>
		{
			if (_pointerInside || _dropInside || _openMenus.Count > 0 || IsEditing || RollInteractionHeld?.Invoke() == true) { return; }
			if (Native.DesktopInput.IsInputDesktop())
			{
				var cursor = NativeMethods.GetCursorPos();
				var window = NativeMethods.GetWindowRect(_owner);
				double scale = NativeMethods.GetDpiForWindow(_owner) / 96.0;
				var point = new Point((cursor.X - window.Left) / scale - Canvas.GetLeft(this) - _visual.TransformMatrix.Translation.X,
					(cursor.Y - window.Top) / scale - Canvas.GetTop(this) - _visual.TransformMatrix.Translation.Y);
				if (RevealedBounds.Contains(point)) { return; }
			}
			SetTemporaryExpanded(false);
		};
		PointerEntered += (_, _) => SetPointerInside(true);
		PointerExited += (_, _) => SetPointerInside(false);
	}

	internal void SetPointerInside(bool inside)
	{
		_pointerInside = inside;
		if (inside)
		{
			_collapseTimer.Stop();
			if (_settings.ExpandOnHover) { SetTemporaryExpanded(true); }
		}
		else if (_temporaryExpanded) { _collapseTimer.Start(); }
	}

	internal void SetDropInside(bool inside)
	{
		_dropInside = inside;
		if (inside) { _collapseTimer.Stop(); SetTemporaryExpanded(true); }
		else if (_temporaryExpanded) { _collapseTimer.Start(); }
	}

	internal void SetTemporaryExpanded(bool expanded)
	{
		if (expanded && (!_frame.Active.RolledUp || IsEditing || _disposed) || expanded == _temporaryExpanded) { return; }
		_temporaryExpanded = expanded;
		_collapseTimer.Stop();
		if (expanded) { RaiseRequested?.Invoke(true); }
		else { ScopeHidden?.Invoke(_frame.Active.Id); }
		UpdateRollLayout();
		_rollTask = AnimateRollAsync();
	}

	internal void ResetTemporaryExpansion()
	{
		_pointerInside = _dropInside = false;
		_collapseTimer.Stop();
		// 收起的分区常驻浮层，不能再按所在窗口判断；没有临时展开且没有动画时无需回收。
		if (!_temporaryExpanded && !IsRolling) { return; }
		CancelEdit();
		foreach (var menu in _openMenus.ToArray()) { menu.Hide(); }
		SetTemporaryExpanded(false);
		FinishRoll();
	}

	internal void SetRollDirection(RollEdge? direction)
	{
		if (_readOnly || RollInteractionHeld?.Invoke() == true) { return; }
		FinishRoll();
		var window = NativeMethods.GetWindowRect(_owner);
		double scale = NativeMethods.GetDpiForWindow(_owner) / 96.0;
		var bounds = new DrawRectangle(window.Left + (int)Math.Round(Canvas.GetLeft(this) * scale), window.Top + (int)Math.Round(Canvas.GetTop(this) * scale),
			(int)Math.Round(Width * scale), (int)Math.Round(Height * scale));
		_frame.SetRollEdge(direction ?? FenceRoll.AutoEdge(bounds, window, (int)Math.Round(24 * scale)), direction);
		UpdateRollLayout();
		SaveRequested?.Invoke();
	}

	internal void HoldExpansion(MenuFlyout menu)
	{
		menu.Opened += (_, _) => { _openMenus.Add(menu); _collapseTimer.Stop(); };
		menu.Closed += (_, _) => { _openMenus.Remove(menu); if (_temporaryExpanded) { _collapseTimer.Start(); } };
	}

	void UpdateRollLayout()
	{
		var content = ContentBounds;
		Canvas.SetLeft(ContentHost, content.X);
		Canvas.SetTop(ContentHost, content.Y);
		ContentHost.Width = content.Width;
		ContentHost.Height = content.Height;
		var header = HeaderBounds;
		Header.Width = IsVerticalRoll ? Height : Width;
		Header.Height = TitleThickness;
		Canvas.SetLeft(Header, header.X);
		Canvas.SetTop(Header, header.Y);
		Header.RenderTransform = IsVerticalRoll ? new CompositeTransform { Rotation = 90, TranslateX = TitleThickness } : null;
		FoldButton.Content = _frame.Active.RolledUp ? "⌄" : "⌃";
		ApplyReveal();
	}

	void ApplyReveal()
	{
		var bounds = RevealedBounds;
		if (!IsRolling)
		{
			_geometry.Offset = new Vector2((float)bounds.X, (float)bounds.Y);
			_geometry.Size = new Vector2((float)bounds.Width, (float)bounds.Height);
		}
		BoundsChanged?.Invoke();
	}

	async Task AnimateRollAsync()
	{
		int version = ++_rollVersion;
		double from = _reveal, to = IsCollapsed ? 0 : 1;
		_geometry.StopAnimation("Size");
		_geometry.StopAnimation("Offset");
		if (Math.Abs(from - to) < 0.0001)
		{
			IsRolling = false;
			ApplyReveal();
			if (!_temporaryExpanded) { RaiseRequested?.Invoke(false); }
			return;
		}
		var first = RevealAt(from);
		var last = RevealAt(to);
		StartAnimation("Size", new((float)first.Width, (float)first.Height), new((float)last.Width, (float)last.Height));
		StartAnimation("Offset", new((float)first.X, (float)first.Y), new((float)last.X, (float)last.Y));
		IsRolling = true;
		var clock = Stopwatch.StartNew();
		while (true)
		{
			double progress = Math.Min(1, clock.Elapsed.TotalMilliseconds / 220);
			_reveal = from + (to - from) * (1 - Math.Pow(1 - progress, 3));
			ApplyReveal();
			if (progress == 1) { break; }
			await Task.Delay(16);
			if (_disposed || version != _rollVersion) { return; }
		}
		_geometry.StopAnimation("Size");
		_geometry.StopAnimation("Offset");
		IsRolling = false;
		ApplyReveal();
		if (!_temporaryExpanded) { RaiseRequested?.Invoke(false); }
	}

	void StartAnimation(string property, Vector2 from, Vector2 to)
	{
		var compositor = _visual.Compositor;
		var animation = compositor.CreateVector2KeyFrameAnimation();
		animation.Duration = TimeSpan.FromMilliseconds(220);
		animation.InsertKeyFrame(0, from);
		animation.InsertKeyFrame(1, to, compositor.CreateCubicBezierEasingFunction(new(1f / 3, 1), new(2f / 3, 1)));
		_geometry.StartAnimation(property, animation);
	}

	internal void FinishRoll()
	{
		_rollVersion++;
		_geometry.StopAnimation("Size");
		_geometry.StopAnimation("Offset");
		IsRolling = false;
		_reveal = IsCollapsed ? 0 : 1;
		ApplyReveal();
		if (!_temporaryExpanded) { RaiseRequested?.Invoke(false); }
	}
}
