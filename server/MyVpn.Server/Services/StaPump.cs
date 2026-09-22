using System.Windows.Forms;

namespace MyVpn.Server.Services;

/// <summary>
/// A dedicated single-threaded-apartment thread with a real window/message queue and a running
/// message loop. The Internet Connection Sharing (HNetCfg) COM object needs the calling STA to
/// have a message queue so COM can deliver its callbacks; a bare STA thread without one fails with
/// HRESULT 0x80040201 (EVENT_E_ALL_SUBSCRIBERS_FAILED) when enabling sharing.
/// </summary>
internal sealed class StaPump
{
    public static StaPump Instance { get; } = new();

    private Control _dispatcher = null!;
    private readonly Thread _thread;

    private StaPump()
    {
        using var ready = new ManualResetEventSlim(false);
        Exception? startupError = null;

        _thread = new Thread(() =>
        {
            try
            {
                _dispatcher = new Control();
                _ = _dispatcher.Handle; // force creation of a window and the thread's message queue
                ready.Set();
                Application.Run(new ApplicationContext());
            }
            catch (Exception ex)
            {
                startupError = ex;
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "MyVpn.StaPump",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();

        if (startupError is not null)
            throw new InvalidOperationException("Could not start the STA dispatcher thread.", startupError);
    }

    /// <summary>Runs <paramref name="action"/> on the pumping STA thread and rethrows its exception.</summary>
    public void Invoke(Action action)
    {
        Exception? error = null;
        _dispatcher.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        if (error is not null)
            throw error;
    }
}
