using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using MyDesktop.Core;
using MyDesktop.Views;

namespace MyDesktop.Services;

/// <summary>
/// 自动更新：从 GitHub 发行版查询最新版本，提示后下载安装包，核对签名后静默安装。
/// 安装程序会先让正在运行的本程序正常退出，装完再以原来的用户身份重新启动它（安装包参数 /autoupdate=1）。
/// 启动约 1 分钟后检查一次，之后每天一次；也可以在托盘菜单和设置里手动检查。
/// </summary>
internal sealed class Updater
{
	const string LatestReleaseApi = "https://api.github.com/repos/mizepeng/my-desktop/releases/latest";
	const string ReleasesPage = "https://github.com/mizepeng/my-desktop/releases/latest";
	const string InstallerPrefix = "MyDesktop-Setup-";
	const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
	const int ERROR_CANCELLED = 1223;

	static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
	static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
	static readonly HttpClient Http = CreateClient();

	/// <summary>
	/// 发行版安装包签名证书的指纹，取自随程序嵌入的 installer/MyDesktop.cer。
	/// </summary>
	static readonly string PublisherThumbprint = LoadPublisherThumbprint();

	readonly FenceManager _manager;
	readonly DispatcherTimer _timer;
	bool _busy;

	sealed record Release(Version Version, string Tag, string Notes, string AssetName, string AssetUrl, long AssetSize);

	public Updater(FenceManager manager)
	{
		_manager = manager;
		_timer = new DispatcherTimer { Interval = StartupDelay };
		_timer.Tick += (_, _) =>
		{
			// 第一次在启动 1 分钟后，之后每小时看一次距上次检查是否已满一天
			_timer.Interval = TimeSpan.FromHours(1);
			var last = _manager.Settings.LastUpdateCheck;
			if (_manager.Settings.AutoCheckUpdates && (last == null || DateTime.Now - last.Value >= CheckInterval))
			{
				_ = CheckAsync(false);
			}
		};
	}

	/// <summary>
	/// 当前程序的版本（主、次、修订三段）。
	/// </summary>
	public static Version CurrentVersion => Normalize(typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));

	public void Start() => _timer.Start();

	public void Stop() => _timer.Stop();

	/// <summary>
	/// 检查新版本；手动检查时已是最新或查询失败也给出提示，自动检查时只在有新版本、且没被跳过时提示。
	/// </summary>
	public async Task CheckAsync(bool manual)
	{
		if (_busy)
		{
			return;
		}
		_busy = true;
		try
		{
			Release? release;
			try
			{
				release = await FetchLatestAsync();
			}
			catch (Exception ex)
			{
				Log.Warn("检查更新失败", ex);
				if (manual)
				{
					ShowFailure("检查更新", $"无法获取最新版本信息：{ex.Message}");
				}
				return;
			}
			_manager.Settings.LastUpdateCheck = DateTime.Now;
			_manager.SaveSoon();
			if (release == null || release.Version <= CurrentVersion)
			{
				if (manual)
				{
					MessageDialog.Show("检查更新", $"当前已是最新版本（{CurrentVersion.ToString(3)}）。", "确定");
				}
				return;
			}
			if (!manual && string.Equals(release.Tag, _manager.Settings.SkippedVersion, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			var message = $"MyDesktop {release.Version.ToString(3)} 已发布，当前版本 {CurrentVersion.ToString(3)}。";
			if (release.Notes.Length > 0)
			{
				message += $"\n\n{release.Notes}";
			}
			// 最后一个按钮响应 Esc，放「稍后」
			int choice = MessageDialog.Show("发现新版本", message, "立即更新", "跳过此版本", "稍后");
			if (choice == 1)
			{
				_manager.Settings.SkippedVersion = release.Tag;
				_manager.SaveSoon();
			}
			else if (choice == 0)
			{
				DownloadAndInstall(release);
			}
		}
		finally
		{
			_busy = false;
		}
	}

	static async Task<Release?> FetchLatestAsync()
	{
		using var response = await Http.GetAsync(LatestReleaseApi);
		response.EnsureSuccessStatusCode();
		using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		var root = json.RootElement;
		var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
		if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
		{
			return null;
		}
		foreach (var asset in root.GetProperty("assets").EnumerateArray())
		{
			var name = asset.GetProperty("name").GetString() ?? string.Empty;
			if (name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				var notes = root.TryGetProperty("body", out var body) ? FormatNotes(body.GetString() ?? string.Empty) : string.Empty;
				return new Release(Normalize(version), tag, notes, name, asset.GetProperty("browser_download_url").GetString() ?? string.Empty,
						asset.GetProperty("size").GetInt64());
			}
		}
		return null;
	}

	/// <summary>
	/// 发行说明是 Markdown：只取「本版更新」一节（没有时取开头几行），去掉 Markdown 记号，免得对话框太长。
	/// </summary>
	static string FormatNotes(string markdown)
	{
		var lines = markdown.Replace("\r", string.Empty).Split('\n');
		int start = Array.FindIndex(lines, l => l.TrimStart('#', ' ').StartsWith("本版更新", StringComparison.Ordinal));
		var picked = start >= 0
				? lines.Skip(start + 1).TakeWhile(l => !l.StartsWith('#'))
				: lines.Where(l => !l.StartsWith('#')).Take(8);
		var text = new StringBuilder();
		foreach (var line in picked.Where(l => l.Trim().Length > 0).Take(12))
		{
			var plain = line.Trim().Replace("**", string.Empty).Replace("`", string.Empty);
			text.AppendLine(plain.StartsWith("- ", StringComparison.Ordinal) ? "• " + plain[2..] : plain);
		}
		return text.ToString().TrimEnd();
	}

	void DownloadAndInstall(Release release)
	{
		var file = Path.Combine(Path.GetTempPath(), "MyDesktop-Update", release.AssetName);
		bool done = MessageDialog.ShowProgress("正在下载更新", $"MyDesktop {release.Version.ToString(3)}（{release.AssetSize / 1048576.0:0.0} MB）",
				(progress, token) => DownloadAsync(release, file, progress, token), out var error);
		if (!done)
		{
			if (error != null)
			{
				Log.Warn("下载更新失败", error);
				ShowFailure("更新失败", $"下载安装包失败：{error.Message}");
			}
			return;
		}
		if (!IsSignedByPublisher(file))
		{
			Log.Warn($"安装包签名校验未通过：{file}");
			TryDelete(file);
			ShowFailure("更新失败", "下载的安装包没有通过签名校验，已删除。");
			return;
		}
		try
		{
			// 安装程序需要管理员权限，系统会弹出确认；它会先让本程序正常退出，装完再重新启动
			Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /autoupdate=1") { UseShellExecute = true })?.Dispose();
			Log.Info($"开始安装更新 {release.Tag}");
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
		{
			// 用户在管理员确认里点了「否」
		}
		catch (Exception ex)
		{
			Log.Warn("启动安装程序失败", ex);
			ShowFailure("更新失败", $"无法启动安装程序：{ex.Message}");
		}
	}

	static async Task DownloadAsync(Release release, string file, IProgress<double> progress, CancellationToken token)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		var partial = file + ".part";
		using (var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, token))
		{
			response.EnsureSuccessStatusCode();
			long total = response.Content.Headers.ContentLength ?? release.AssetSize;
			await using var source = await response.Content.ReadAsStreamAsync(token);
			await using var target = File.Create(partial);
			var buffer = new byte[81920];
			long received = 0;
			int read;
			while ((read = await source.ReadAsync(buffer, token)) > 0)
			{
				await target.WriteAsync(buffer.AsMemory(0, read), token);
				received += read;
				if (total > 0)
				{
					progress.Report((double)received / total);
				}
			}
		}
		File.Move(partial, file, true);
	}

	/// <summary>
	/// 安装包必须由 MyDesktop 签名证书签名：系统核对签名完好（证书是自签名的，「根证书不受信任」也算完好），
	/// 并且签名证书正是随程序带着的那一张，防止下载到被替换或篡改的安装包。
	/// </summary>
	static bool IsSignedByPublisher(string file)
	{
		int result = WinTrust.Verify(file);
		if (result != 0 && result != CERT_E_UNTRUSTEDROOT)
		{
			Log.Warn($"安装包签名无效：0x{result:X8}");
			return false;
		}
		try
		{
			// 取签名者证书没有不过时的替代 API（X509CertificateLoader 不能从已签名文件中读取）；签名本身已由 WinVerifyTrust 核对
#pragma warning disable SYSLIB0057
			using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
			return string.Equals(signer.Thumbprint, PublisherThumbprint, StringComparison.OrdinalIgnoreCase);
		}
		catch (CryptographicException)
		{
			return false;
		}
	}

	static string LoadPublisherThumbprint()
	{
		using var stream = typeof(Updater).Assembly.GetManifestResourceStream("MyDesktop.cer")!;
		using var memory = new MemoryStream();
		stream.CopyTo(memory);
		using var certificate = X509CertificateLoader.LoadCertificate(memory.ToArray());
		return certificate.Thumbprint;
	}

	static void ShowFailure(string title, string message)
	{
		if (MessageDialog.Show(title, $"{message}\n\n可以到发行版页面手动下载最新版本。", "打开发行版页面", "关闭") == 0)
		{
			try
			{
				Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true })?.Dispose();
			}
			catch (Exception ex)
			{
				Log.Warn("打开发行版页面失败", ex);
			}
		}
	}

	static void TryDelete(string file)
	{
		try
		{
			File.Delete(file);
		}
		catch (IOException)
		{
		}
	}

	/// <summary>
	/// 只比较主、次、修订三段，发行版标签 v1.0.2 与程序版本 1.0.2.0 视为相同。
	/// </summary>
	static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

	static HttpClient CreateClient()
	{
		// 默认走系统代理；GitHub 的接口要求带 User-Agent
		var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd($"MyDesktop/{CurrentVersion.ToString(3)}");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		return client;
	}

	/// <summary>
	/// 调用 WinVerifyTrust 核对文件的 Authenticode 签名，不检查吊销（不联网）。
	/// </summary>
	static class WinTrust
	{
		static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
		const uint WTD_UI_NONE = 2;
		const uint WTD_REVOKE_NONE = 0;
		const uint WTD_CHOICE_FILE = 1;
		const uint WTD_STATEACTION_IGNORE = 0;
		const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		struct WINTRUST_FILE_INFO
		{
			public uint cbStruct;
			public string pcwszFilePath;
			public IntPtr hFile;
			public IntPtr pgKnownSubject;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct WINTRUST_DATA
		{
			public uint cbStruct;
			public IntPtr pPolicyCallbackData;
			public IntPtr pSIPClientData;
			public uint dwUIChoice;
			public uint fdwRevocationChecks;
			public uint dwUnionChoice;
			public IntPtr pFile;
			public uint dwStateAction;
			public IntPtr hWVTStateData;
			public IntPtr pwszURLReference;
			public uint dwProvFlags;
			public uint dwUIContext;
			public IntPtr pSignatureSettings;
		}

		[DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
		static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

		public static int Verify(string file)
		{
			var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
			var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
			try
			{
				Marshal.StructureToPtr(fileInfo, filePointer, false);
				var data = new WINTRUST_DATA
				{
					cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
					dwUIChoice = WTD_UI_NONE,
					fdwRevocationChecks = WTD_REVOKE_NONE,
					dwUnionChoice = WTD_CHOICE_FILE,
					pFile = filePointer,
					dwStateAction = WTD_STATEACTION_IGNORE,
					dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
				};
				var action = GenericVerifyV2;
				return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
			}
			finally
			{
				Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePointer);
				Marshal.FreeHGlobal(filePointer);
			}
		}
	}
}
