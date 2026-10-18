using System.Drawing;

namespace MyDesktop.Services;

internal sealed record SelectionTarget(Guid Scope, string Key, Rectangle Bounds);

internal sealed class SelectionBand(ItemSelection selection, Point origin, IReadOnlyList<SelectionTarget> targets, bool additive)
{
	readonly IReadOnlyDictionary<Guid, IReadOnlyList<string>> _original = selection.Capture();
	public Rectangle Bounds { get; private set; }

	public void Update(Point point)
	{
		Bounds = Rectangle.FromLTRB(Math.Min(origin.X, point.X), Math.Min(origin.Y, point.Y), Math.Max(origin.X, point.X), Math.Max(origin.Y, point.Y));
		var scopes = additive ? _original.ToDictionary(pair => pair.Key, pair => pair.Value.ToHashSet(StringComparer.OrdinalIgnoreCase)) : [];
		foreach (var target in targets.Where(target => Bounds.Width > 0 && Bounds.Height > 0 && Bounds.IntersectsWith(target.Bounds)))
		{
			if (!scopes.TryGetValue(target.Scope, out var keys)) { scopes.Add(target.Scope, keys = new(StringComparer.OrdinalIgnoreCase)); }
			keys.Add(target.Key);
		}
		selection.Replace(scopes.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray()));
	}

	public void Cancel() => selection.Replace(_original);
}
