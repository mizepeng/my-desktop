using MyDesktop.Models;

namespace MyDesktop.Services;

/// <summary>
/// 标签组的配置规则，与具体界面框架及窗口生命周期无关。
/// </summary>
internal static class FenceGroups
{
	public static void CopyFrame(FenceSettings from, FenceSettings to)
	{
		(to.X, to.Y, to.Width, to.Height, to.LayoutDpi) = (from.X, from.Y, from.Width, from.Height, from.LayoutDpi);
		(to.RolledUp, to.RollDirection, to.RollEdge, to.Locked) = (from.RolledUp, from.RollDirection, from.RollEdge, from.Locked);
		to.BoundsByDpi = new Dictionary<int, FenceBounds>(from.BoundsByDpi);
		to.LayoutByDisplay = new Dictionary<string, FenceLayout>(from.LayoutByDisplay);
	}

	/// <summary>
	/// 沿用旧版配置修复规则：删除失效和重复成员，解散不足两个成员的组，按当前标签统一框架设置。
	/// </summary>
	public static bool Normalize(AppSettings settings)
	{
		bool changed = false;
		var fences = settings.Fences.ToDictionary(f => f.Id);
		var grouped = new HashSet<Guid>();
		foreach (var group in settings.Groups.ToList())
		{
			var members = new List<Guid>();
			foreach (var id in group.Members)
			{
				if (fences.ContainsKey(id) && grouped.Add(id))
				{
					members.Add(id);
				}
			}
			changed |= members.Count != group.Members.Count;
			group.Members = members;
			if (members.Count < 2)
			{
				settings.Groups.Remove(group);
				changed = true;
				continue;
			}
			if (!members.Contains(group.Active))
			{
				group.Active = members[0];
				changed = true;
			}
			foreach (var id in members.Where(id => id != group.Active))
			{
				CopyFrame(fences[group.Active], fences[id]);
			}
		}
		return changed;
	}
}
