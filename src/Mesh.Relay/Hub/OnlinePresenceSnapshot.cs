namespace Mesh.Relay.Hub;

/// <summary>Online presence for one queried handle: whether it has any live device and which ones.</summary>
public sealed record OnlineHandlePresence(
    string Handle,
    bool Online,
    IReadOnlyList<string> Devices);

/// <summary>The relay's answer to a ResolvePresence query: a snapshot per requested handle.</summary>
public sealed record OnlinePresenceSnapshot(
    IReadOnlyList<OnlineHandlePresence> Handles);
