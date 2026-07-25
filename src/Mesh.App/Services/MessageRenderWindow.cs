namespace Mesh.App.Services;

internal static class MessageRenderWindow
{
    public const int InitialCount = 80;
    public const int PageSize = 80;

    public static IReadOnlyList<T> Latest<T>(IReadOnlyList<T> items, int requestedCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        var count = Math.Max(1, requestedCount);
        if (items.Count <= count) return items;

        var start = items.Count - count;
        var result = new T[count];
        for (var i = 0; i < count; i++)
            result[i] = items[start + i];
        return result;
    }

    public static int HiddenCount(int totalCount, int requestedCount)
        => Math.Max(0, totalCount - Math.Max(1, requestedCount));

    public static int NextCount(int totalCount, int requestedCount)
    {
        if (totalCount <= 0) return InitialCount;
        var current = Math.Max(1, requestedCount);
        var expanded = Math.Min((long)totalCount, (long)current + PageSize);
        return (int)expanded;
    }

    public static int NextPageSize(int totalCount, int requestedCount)
        => Math.Min(PageSize, HiddenCount(totalCount, requestedCount));
}
