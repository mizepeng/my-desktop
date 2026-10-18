using MyDesktop.Models;

namespace MyDesktop.Services;

/// <summary>
/// 标签组的配置规则，与具体界面框架及窗口生命周期无关。
/// </summary>
internal static class FenceGroups
{
	public static FenceGroup Merge(AppSettings settings, Guid sourceId, Guid targetId)
	{
		var source = settings.Fences.Single(fence => fence.Id == sourceId);
		var target = settings.Fences.Single(fence => fence.Id == targetId);
		var sourceGroup = GroupOf(settings, sourceId);
		var targetGroup = GroupOf(settings, targetId);
		if (sourceId == targetId || sourceGroup != null && sourceGroup == targetGroup)
		{
			throw new InvalidOperationException("同一个标签组不能与自身合并。");
		}
		if (source.Locked || target.Locked) { throw new InvalidOperationException("锁定的分区不能合并标签。"); }
		var incoming = sourceGroup?.Members.ToArray() ?? [sourceId];
		if (sourceGroup != null) { settings.Groups.Remove(sourceGroup); }
		if (targetGroup == null)
		{
			targetGroup = new FenceGroup { Members = [targetId], Active = targetId };
			settings.Groups.Add(targetGroup);
		}
		targetGroup.Members.AddRange(incoming);
		targetGroup.Active = sourceId;
		foreach (var id in incoming) { CopyFrame(target, settings.Fences.Single(fence => fence.Id == id)); }
		return targetGroup;
	}

	public static bool Move(AppSettings settings, Guid id, int index)
	{
		var fence = settings.Fences.Single(fence => fence.Id == id);
		if (fence.Locked) { throw new InvalidOperationException("锁定的分区不能调整标签顺序。"); }
		var group = GroupOf(settings, id);
		if (group == null) { return false; }
		int from = group.Members.IndexOf(id), to = Math.Clamp(index, 0, group.Members.Count - 1);
		if (from == to) { return false; }
		group.Members.RemoveAt(from);
		group.Members.Insert(to, id);
		return true;
	}

	public static bool Detach(AppSettings settings, Guid id)
	{
		var fence = settings.Fences.Single(item => item.Id == id);
		if (fence.Locked) { throw new InvalidOperationException("锁定的分区不能拆出标签。"); }
		var group = GroupOf(settings, id);
		if (group == null) { return false; }
		int index = group.Members.IndexOf(id);
		group.Members.RemoveAt(index);
		if (group.Active == id) { group.Active = group.Members[Math.Min(index, group.Members.Count - 1)]; }
		if (group.Members.Count < 2) { settings.Groups.Remove(group); }
		return true;
	}

	static FenceGroup? GroupOf(AppSettings settings, Guid id) => settings.Groups.SingleOrDefault(group => group.Members.Contains(id));

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
