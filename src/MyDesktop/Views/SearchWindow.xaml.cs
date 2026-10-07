using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MyDesktop.Core;
using MyDesktop.Native;
using MyDesktop.Services;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Views;

/// <summary>
/// 全局快捷键弹出的搜索框：在分区里和桌面上的图标中按名称或拼音查找，回车或单击打开（与双击图标相同）。
/// 弹在鼠标所在显示器的上方，失去焦点、按 Esc 或再按一次快捷键时收起。
/// </summary>
internal partial class SearchWindow : Window
{
	const int MaxResults = 8;

	/// <summary>
	/// 还没输入时：常用最多列 4 个，常用和全部合起来最多 10 行，全部只列前面一部分。
	/// </summary>
	const int MaxFrequent = 4;

	const int MaxBrowseRows = 10;

	/// <summary>
	/// 一个候选图标和它所在的位置（哪个分区，或桌面上）。
	/// </summary>
	public sealed record Entry(FenceItem Item, string Location);

	/// <summary>
	/// 结果列表的一行：分类标题（常用、全部）或一个图标，只有图标行能选中。
	/// 用类而不是记录：同一个图标可能同时出现在常用和全部里，两行要是不同的对象，列表才能分别选中。
	/// </summary>
	public sealed class Row
	{
		public string? Header { get; init; }

		public Entry? Entry { get; init; }

		public bool IsSelectable => Entry != null;
	}

	readonly Action<FenceItem> _open;
	List<Entry> _entries = [];
	IntPtr _hwnd;
	// 弹出位置的工作区和窗口上边（物理像素），结果变多时窗口往上挪，不伸出屏幕
	RECT _workArea;
	int _preferredTop;

	public SearchWindow(Action<FenceItem> open)
	{
		_open = open;
		InitializeComponent();
		PreviewKeyDown += OnPreviewKeyDown;
		Deactivated += (_, _) => Dismiss();
		SizeChanged += (_, _) => KeepOnScreen();
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		_hwnd = new WindowInteropHelper(this).Handle;
		// 不出现在任务栏和 Alt+Tab 里
		long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
		SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr((exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
	}

	/// <summary>
	/// 在鼠标所在的显示器上弹出，候选是此刻分区里和桌面上的全部图标。
	/// </summary>
	public void Popup(List<Entry> entries)
	{
		_entries = entries;
		ApplyTheme();
		QueryBox.Text = string.Empty;
		UpdateResults();
		_hwnd = new WindowInteropHelper(this).EnsureHandle();
		var monitor = MonitorFromPoint(NativeMethods.GetCursorPos(), MONITOR_DEFAULTTONEAREST);
		var (work, scale) = GetMonitorWorkArea(monitor);
		int width = (int)Math.Round(Width * scale);
		_workArea = work;
		_preferredTop = work.Top + work.Height / 5;
		SetWindowPos(_hwnd, HWND_TOPMOST, work.Left + (work.Width - width) / 2, _preferredTop, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
		Show();
		KeepOnScreen();
		Activate();
		// 按下全局快捷键时本程序有权切换前台
		SetForegroundWindow(_hwnd);
		QueryBox.Focus();
	}

	/// <summary>
	/// 窗口高度随结果变化：放得下时停在原来的位置，屏幕矮、结果多时往上挪，底边不伸出工作区。
	/// </summary>
	void KeepOnScreen()
	{
		if (!IsVisible || _hwnd == IntPtr.Zero)
		{
			return;
		}
		var rect = GetWindowRect(_hwnd);
		int top = Math.Max(_workArea.Top, Math.Min(_preferredTop, _workArea.Bottom - rect.Height));
		if (top != rect.Top)
		{
			SetWindowPos(_hwnd, IntPtr.Zero, rect.Left, top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
		}
	}

	public void Dismiss()
	{
		if (IsVisible)
		{
			Hide();
		}
		// 不留着图标的引用
		_entries = [];
		ResultsList.ItemsSource = null;
	}

	void ApplyTheme()
	{
		bool dark = SystemTheme.IsDark();
		SetBrush("SearchBackground", dark ? 0xFA2B2B2Bu : 0xFAF9F9F9u);
		SetBrush("SearchBorder", dark ? 0x33FFFFFFu : 0x1F000000u);
		SetBrush("SearchForeground", dark ? 0xFFFFFFFFu : 0xFF1B1B1Bu);
		SetBrush("SearchSecondary", dark ? 0x99FFFFFFu : 0x8A000000u);
		SetBrush("SearchHover", dark ? 0x12FFFFFFu : 0x0A000000u);
		SetBrush("SearchSelected", dark ? 0x24FFFFFFu : 0x14000000u);
	}

	void SetBrush(string key, uint argb)
	{
		var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
		brush.Freeze();
		Resources[key] = brush;
	}

	void QueryBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateResults();

	/// <summary>
	/// 还没输入时分「常用」和「全部」两类列出：常用按打开记录排（还没有记录时不列），全部按分区、桌面的顺序只列前面一部分。
	/// 输入后名称开头对上的排最前，其次是名称里包含的、拼音从头对上的、拼音从中间对上的；同一档里常打开的在前，再按名称短的在前。
	/// </summary>
	void UpdateResults()
	{
		Placeholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
		var query = PinyinMatcher.NormalizeQuery(QueryBox.Text);
		var rows = new List<Row>();
		int more = 0;
		if (query.Length == 0)
		{
			var frequent = _entries
					.Select(entry => (Entry: entry, Score: UsageStats.Score(entry.Item.FullPath)))
					.Where(x => x.Score > 0)
					.OrderByDescending(x => x.Score)
					.Take(MaxFrequent)
					.Select(x => x.Entry)
					.ToList();
			if (frequent.Count > 0)
			{
				rows.Add(new Row { Header = "常用" });
				rows.AddRange(frequent.Select(entry => new Row { Entry = entry }));
			}
			if (_entries.Count > 0)
			{
				int shown = Math.Min(_entries.Count, MaxBrowseRows - frequent.Count);
				rows.Add(new Row { Header = "全部" });
				rows.AddRange(_entries.Take(shown).Select(entry => new Row { Entry = entry }));
				more = _entries.Count - shown;
			}
		}
		else
		{
			rows.AddRange(_entries
					.Select(entry => (Entry: entry, Rank: PinyinMatcher.Rank(entry.Item.DisplayName, query)))
					.Where(x => x.Rank != null)
					.OrderBy(x => x.Rank)
					.ThenByDescending(x => UsageStats.Score(x.Entry.Item.FullPath))
					.ThenBy(x => x.Entry.Item.DisplayName.Length)
					.ThenBy(x => x.Entry.Item.DisplayName, StringComparer.CurrentCulture)
					.Take(MaxResults)
					.Select(x => new Row { Entry = x.Entry }));
		}
		ResultsList.ItemsSource = rows;
		ResultsList.SelectedItem = rows.FirstOrDefault(r => r.IsSelectable);
		ResultsList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		NoResultText.Visibility = query.Length > 0 && rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		MoreText.Text = $"还有 {more} 个图标，输入名称或拼音查找";
		MoreText.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
		// 桌面上一个图标都没有、也没输入时只留搜索框
		ResultsPanel.Visibility = query.Length == 0 && rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
	}

	void OnPreviewKeyDown(object sender, KeyEventArgs e)
	{
		switch (e.Key)
		{
			case Key.Escape:
				Dismiss();
				e.Handled = true;
				break;
			case Key.Down:
				MoveSelection(1);
				e.Handled = true;
				break;
			case Key.Up:
				MoveSelection(-1);
				e.Handled = true;
				break;
			case Key.Enter:
				OpenSelected();
				e.Handled = true;
				break;
		}
	}

	/// <summary>
	/// 上下键在图标行之间移动，跳过分类标题，到头后从另一头接着走。
	/// </summary>
	void MoveSelection(int delta)
	{
		if (ResultsList.ItemsSource is not List<Row> rows || !rows.Any(r => r.IsSelectable))
		{
			return;
		}
		// 没有选中时从头（向下）或从尾（向上）开始
		int index = ResultsList.SelectedIndex >= 0 ? ResultsList.SelectedIndex : delta > 0 ? -1 : rows.Count;
		do
		{
			index = ((index + delta) % rows.Count + rows.Count) % rows.Count;
		}
		while (!rows[index].IsSelectable);
		ResultsList.SelectedIndex = index;
	}

	void OpenSelected()
	{
		if (ResultsList.SelectedItem is Row { Entry: { } entry })
		{
			Dismiss();
			_open(entry.Item);
		}
	}

	void ResultsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		if (ItemsControl.ContainerFromElement(ResultsList, (DependencyObject)e.OriginalSource) is ListBoxItem container)
		{
			ResultsList.SelectedItem = container.DataContext;
			OpenSelected();
		}
	}
}
