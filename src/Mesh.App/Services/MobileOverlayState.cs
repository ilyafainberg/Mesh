namespace Mesh.App.Services;

/// <summary>
/// Tracks modal surfaces rendered inside the mobile shell's native scrolling layer.
/// The shell uses this state to give those surfaces the full safe-area viewport.
/// </summary>
public sealed class MobileOverlayState
{
    readonly object gate = new();
    readonly HashSet<object> owners = new(ReferenceEqualityComparer.Instance);

    public event Action? Changed;

    public bool IsOpen
    {
        get
        {
            lock (gate) return owners.Count > 0;
        }
    }

    public void SetActive(object owner, bool active)
    {
        ArgumentNullException.ThrowIfNull(owner);

        bool openChanged;
        lock (gate)
        {
            var wasOpen = owners.Count > 0;
            if (active) owners.Add(owner);
            else owners.Remove(owner);
            openChanged = wasOpen != (owners.Count > 0);
        }
        if (openChanged) Changed?.Invoke();
    }
    }
}
