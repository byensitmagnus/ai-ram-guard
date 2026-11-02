using AiRamGuard.Tray;
using Xunit;

public class UiThreadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Window_signals_and_async_rounds_return_to_the_UI_thread(bool hasDefaultContext)
    {
        Exception? failure = null;
        var completed = false;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(hasDefaultContext ? new SynchronizationContext() : null);
                using var context = TrayContext.CaptureUiContext();
                Assert.Same(context, SynchronizationContext.Current);
                Assert.Same(context, TrayContext.CaptureUiContext());
                var uiThread = Environment.CurrentManagedThreadId;
                using var loop = new ApplicationContext();
                using var timeout = new System.Windows.Forms.Timer { Interval = 5000 };
                timeout.Tick += (_, _) => loop.ExitThread();
                timeout.Start();

                // The same Post path as a second launch asking the tray to show its window.
                _ = Task.Run(() => context.Post(async _ =>
                {
                    try
                    {
                        Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
                        using var form = new Form { ShowInTaskbar = false };
                        _ = form.Handle;
                        Assert.False(form.InvokeRequired);
                        // Guard rounds also resume here after their background scan.
                        await Task.Run(() => Thread.Sleep(20));
                        Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
                        completed = true;
                    }
                    catch (Exception e) { failure = e; }
                    finally { context.Post(_ => loop.ExitThread(), null); }
                }, null));

                Application.Run(loop);
            }
            catch (Exception e) { failure = e; }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The UI thread did not exit its message loop.");
        Assert.Null(failure);
        Assert.True(completed, "The UI message loop did not handle the show-window signal and scan continuation.");
    }
}
