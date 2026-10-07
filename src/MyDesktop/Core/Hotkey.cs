using System.Windows.Input;
using static MyDesktop.Native.NativeMethods;

namespace MyDesktop.Core;

/// <summary>
/// 全局快捷键：修饰键加一个按键，在设置里存成「Ctrl+Alt+Space」这样的文字。
/// </summary>
internal readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
	/// <summary>
	/// 至少带 Ctrl、Alt、Win 中的一个（F1～F24 单独也行），否则平时打字的按键会被抢走。
	/// </summary>
	public bool IsValid => Key != Key.None && !IsModifierKey(Key)
			&& ((Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0 || Key is >= Key.F1 and <= Key.F24);

	public uint NativeModifiers => (Modifiers.HasFlag(ModifierKeys.Control) ? MOD_CONTROL : 0)
			| (Modifiers.HasFlag(ModifierKeys.Alt) ? MOD_ALT : 0)
			| (Modifiers.HasFlag(ModifierKeys.Shift) ? MOD_SHIFT : 0)
			| (Modifiers.HasFlag(ModifierKeys.Windows) ? MOD_WIN : 0);

	public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

	public override string ToString() => ModifiersText(Modifiers) + KeyName(Key);

	/// <summary>
	/// 修饰键部分，如「Ctrl+Alt+」；录入快捷键时先显示已按下的修饰键。
	/// </summary>
	public static string ModifiersText(ModifierKeys modifiers)
	{
		var text = string.Empty;
		if (modifiers.HasFlag(ModifierKeys.Control))
		{
			text += "Ctrl+";
		}
		if (modifiers.HasFlag(ModifierKeys.Alt))
		{
			text += "Alt+";
		}
		if (modifiers.HasFlag(ModifierKeys.Shift))
		{
			text += "Shift+";
		}
		if (modifiers.HasFlag(ModifierKeys.Windows))
		{
			text += "Win+";
		}
		return text;
	}

	/// <summary>
	/// 解析设置里的文字；为空或写错时返回 null。
	/// </summary>
	public static Hotkey? Parse(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		var modifiers = ModifierKeys.None;
		var key = Key.None;
		foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
		{
			switch (part.ToLowerInvariant())
			{
				case "ctrl":
					modifiers |= ModifierKeys.Control;
					break;
				case "alt":
					modifiers |= ModifierKeys.Alt;
					break;
				case "shift":
					modifiers |= ModifierKeys.Shift;
					break;
				case "win":
					modifiers |= ModifierKeys.Windows;
					break;
				default:
					key = ParseKey(part);
					break;
			}
		}
		var hotkey = new Hotkey(modifiers, key);
		return hotkey.IsValid ? hotkey : null;
	}

	static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
			or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

	/// <summary>
	/// 数字键显示成 0～9，其余用 WPF 的按键名（Space、F1、A……）。
	/// </summary>
	static string KeyName(Key key) => key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString();

	static Key ParseKey(string text)
	{
		if (text.Length == 1 && char.IsAsciiDigit(text[0]))
		{
			return Key.D0 + (text[0] - '0');
		}
		return Enum.TryParse<Key>(text, true, out var key) ? key : Key.None;
	}
}
