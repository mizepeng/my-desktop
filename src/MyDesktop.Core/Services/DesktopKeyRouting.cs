namespace MyDesktop.Services;

internal enum DesktopInputCommand
{
	Open,
	SelectAll,
	Previous,
	Next,
	First,
	Last,
	TypeAhead,
	Unsupported,
}

internal sealed class DesktopKeyRouting
{
	readonly HashSet<uint> _consumed = [];

	public (bool Intercept, DesktopInputCommand? Command) Route(uint key, bool down, bool desktopFocused, bool win, bool ctrl, bool shift, bool alt, bool menuOrEditor)
	{
		if (!down)
		{
			return (_consumed.Remove(key), null);
		}
		if (!desktopFocused || win || menuOrEditor || ctrl && alt)
		{
			return (false, null);
		}
		DesktopInputCommand? command = key switch
		{
			0x0D when !alt && !ctrl => DesktopInputCommand.Open,
			0x41 when ctrl && !alt => DesktopInputCommand.SelectAll,
			0x25 or 0x26 when !ctrl && !alt => DesktopInputCommand.Previous,
			0x27 or 0x28 when !ctrl && !alt => DesktopInputCommand.Next,
			0x24 when !ctrl && !alt => DesktopInputCommand.First,
			0x23 when !ctrl && !alt => DesktopInputCommand.Last,
			0x2E or 0x71 or 0x5D => DesktopInputCommand.Unsupported,
			0x79 when shift => DesktopInputCommand.Unsupported,
			0x0D when alt => DesktopInputCommand.Unsupported,
			0x0D when ctrl => DesktopInputCommand.Unsupported,
			0x2D when ctrl || shift => DesktopInputCommand.Unsupported,
			0x43 or 0x58 or 0x56 or 0x44 or 0x5A or 0x59 when ctrl && !alt => DesktopInputCommand.Unsupported,
			>= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A when !ctrl && !alt => DesktopInputCommand.TypeAhead,
			_ => null,
		};
		if (command != null)
		{
			_consumed.Add(key);
		}
		return (command != null, command);
	}
}
