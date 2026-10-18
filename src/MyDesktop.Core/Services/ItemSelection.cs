namespace MyDesktop.Services;

/// <summary>
/// 全桌面共用选择；Guid.Empty 明确表示散放图标，其余标识对应当前可见标签的分区 ID。
/// </summary>
internal sealed class ItemSelection
{
	readonly Dictionary<Guid, HashSet<string>> _scopes = [];
	public event Action? Changed;
	public IReadOnlyList<string> Keys => _scopes.Values.SelectMany(keys => keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
	public IReadOnlyList<string> InScope(Guid scope) => _scopes.TryGetValue(scope, out var keys) ? keys.ToArray() : [];
	public IReadOnlyDictionary<Guid, IReadOnlyList<string>> Capture() => _scopes.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray());

	public void Replace(IReadOnlyDictionary<Guid, IReadOnlyList<string>> scopes)
	{
		_scopes.Clear();
		foreach (var (scope, keys) in scopes.Where(pair => pair.Value.Count > 0))
		{
			_scopes.Add(scope, keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
		}
		Changed?.Invoke();
	}

	public void Set(Guid scope, IEnumerable<string> keys, bool extend)
	{
		var selected = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (!extend)
		{
			_scopes.Clear();
		}
		if (selected.Count == 0)
		{
			_scopes.Remove(scope);
		}
		else
		{
			_scopes[scope] = selected;
		}
		Changed?.Invoke();
	}

	public void Click(Guid scope, string? key, bool extend)
	{
		var keys = extend ? InScope(scope).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
		if (key != null && !keys.Add(key))
		{
			keys.Remove(key);
		}
		Set(scope, keys, extend);
	}

	public void Retain(Guid scope, IEnumerable<string> available)
	{
		if (_scopes.TryGetValue(scope, out var keys))
		{
			var kept = keys.Intersect(available, StringComparer.OrdinalIgnoreCase).ToArray();
			if (kept.Length != keys.Count)
			{
				Set(scope, kept, true);
			}
		}
	}

	public void Remove(Guid scope)
	{
		if (_scopes.Remove(scope))
		{
			Changed?.Invoke();
		}
	}

	public void Rename(string oldPath, string newPath, bool directory)
	{
		foreach (var scope in _scopes.Keys.ToArray())
		{
			_scopes[scope] = _scopes[scope].Select(key => ItemRename.RewritePath(key, oldPath, newPath, directory)).ToHashSet(StringComparer.OrdinalIgnoreCase);
		}
		Changed?.Invoke();
	}
}
