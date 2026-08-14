namespace Mesh.App.Services;

public interface IAccountInstanceCoordinator
{
    AccountSwitchReservation Reserve(AccountRef target);
}

public sealed class DefaultAccountInstanceCoordinator : IAccountInstanceCoordinator
{
    public AccountSwitchReservation Reserve(AccountRef target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return AccountSwitchReservation.Allowed();
    }
}

public sealed class DesktopAccountInstanceCoordinator : IAccountInstanceCoordinator
{
    public AccountSwitchReservation Reserve(AccountRef target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return MeshDesktopInstanceRuntime.ReserveSwitch(target.Id, target.Handle);
    }
}
