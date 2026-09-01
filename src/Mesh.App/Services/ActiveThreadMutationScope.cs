namespace Mesh.App.Services;

public sealed record ActiveThreadMutationScope(
    string AccountId,
    string DatabaseIdentity,
    long Epoch,
    string ThreadId);
