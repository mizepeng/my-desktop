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
	/// 一个候选图标和它所在的位置（哪个分区，或桌面上）。
	/// </summary>
	public sealed record Entry(FenceItem Item, string Location);

	readonly Action<FenceItem> _open;
	List<Entry> _entries = [];
	IntPtr _hwnd;

	public SearchWindow(Action<FenceItem> open)
	{
		_open = open;
		InitializeComponent();
		PreviewKeyDown += OnPreviewKeyDown;
		Deactivated += (_, _) => Dismiss();
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
		SetWindowPos(_hwnd, HWND_TOPMOST, work.Left + (work.Width - width) / 2, work.Top + work.Height / 5, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
		Show();
		Activate();
		// 按下全局快捷键时本程序有权切换前台
		SetForegroundWindow(_hwnd);
		QueryBox.Focus();
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
	/// 还没输入时列出最常打开的几个。输入后名称开头对上的排最前，其次是名称里包含的、拼音从头对上的、拼音从中间对上的；
	/// 同一档里常打开的在前，再按名称短的在前。
	/// </summary>
	void UpdateResults()
	{
		Placeholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
		var query = PinyinMatcher.NormalizeQuery(QueryBox.Text);
		List<Entry> results;
		if (query.Length == 0)
		{
			results = _entries
					.Select(entry => (Entry: entry, Score: UsageStats.Score(entry.Item.FullPath)))
					.Where(x => x.Score > 0)
					.OrderByDescending(x => x.Score)
					.Take(MaxResults)
					.Select(x => x.Entry)
					.ToList();
		}
		else
		{
			results = _entries
					.Select(entry => (Entry: entry, Rank: PinyinMatcher.Rank(entry.Item.DisplayName, query)))
					.Where(x => x.Rank != null)
					.OrderBy(x => x.Rank)
					.ThenByDescending(x => UsageStats.Score(x.Entry.Item.FullPath))
					.ThenBy(x => x.Entry.Item.DisplayName.Length)
					.ThenBy(x => x.Entry.Item.DisplayName, StringComparer.CurrentCulture)
					.Take(MaxResults)
					.Select(x => x.Entry)
					.ToList();
		}
		ResultsList.ItemsSource = results;
		ResultsList.SelectedIndex = results.Count > 0 ? 0 : -1;
		ResultsList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		FrequentHeader.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
		NoResultText.Visibility = results.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
		// 没输入、也还没有打开记录时只留搜索框
		ResultsPanel.Visibility = query.Length == 0 && results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
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

	void MoveSelection(int delta)
	{
		int count = ResultsList.Items.Count;
		if (count > 0)
		{
			ResultsList.SelectedIndex = (ResultsList.SelectedIndex + delta + count) % count;
		}
	}

	void OpenSelected()
	{
		if (ResultsList.SelectedItem is Entry entry)
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
