using MyDesktop.Models;

namespace MyDesktop.Services;

/// <summary>
/// 把全部分区投影成可见的分区框架；一个标签组始终只占用一个框架。
/// </summary>
internal sealed class FenceWorkspace
{
	public FenceWorkspace(AppSettings settings)
	{
		FenceGroups.Normalize(settings);
		var fences = settings.Fences.ToDictionary(f => f.Id);
		var groups = settings.Groups.SelectMany(group => group.Members.Select(id => (id, group))).ToDictionary(pair => pair.id, pair => pair.group);
		var visited = new HashSet<Guid>();
		var frames = new List<FenceFrame>();
		foreach (var fence in settings.Fences)
		{
			if (!visited.Add(fence.Id))
			{
				continue;
			}
			if (groups.TryGetValue(fence.Id, out var group))
			{
				visited.UnionWith(group.Members);
				frames.Add(new FenceFrame(group.Members.Select(id => fences[id]).ToArray(), group));
			}
			else
			{
				frames.Add(new FenceFrame([fence], null));
			}
		}
		Frames = frames;
	}

	public IReadOnlyList<FenceFrame> Frames { get; }
}

internal sealed class FenceFrame(IReadOnlyList<FenceSettings> members, FenceGroup? group)
{
	public IReadOnlyList<FenceSettings> Members { get; } = members;

	public FenceSettings Active => group == null ? Members[0] : Members.Single(member => member.Id == group.Active);

	public void Activate(Guid id)
	{
		if (!Members.Any(member => member.Id == id))
		{
			throw new ArgumentException("指定分区不属于这个标签组。", nameof(id));
		}
		if (group != null)
		{
			group.Active = id;
		}
	}

	public void MoveTo(int x, int y, int dpi)
	{
		if (dpi <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(dpi));
		}
		(Active.X, Active.Y, Active.LayoutDpi) = (x, y, dpi);
		Sync();
	}

	public void SetRolledUp(bool rolledUp)
	{
		Active.RolledUp = rolledUp;
		Sync();
	}

	void Sync()
	{
		foreach (var member in Members.Where(member => member != Active))
		{
			FenceGroups.CopyFrame(Active, member);
		}
	}
}
