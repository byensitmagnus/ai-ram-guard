using AiRamGuard.Tray;
using Xunit;

public class StartupTests
{
    [Fact]
    public void Installed_app_can_start_while_installer_still_holds_the_released_mutex_object()
    {
        var name = @"Local\AiRamGuard.Test." + Guid.NewGuid();
        using var installer = new Mutex(true, name);
        installer.ReleaseMutex();
        using var app = new Mutex(false, name);
        Assert.True(Program.TryAcquire(app));
        app.ReleaseMutex();
    }

    [Fact]
    public void A_running_instance_keeps_ownership()
    {
        using var mutex = new Mutex(true);
        var acquired = true;
        var other = new Thread(() => acquired = Program.TryAcquire(mutex));
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(2)));
        Assert.False(acquired);
        mutex.ReleaseMutex();
    }
}
