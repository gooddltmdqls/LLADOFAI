using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace LLADOFAI.Fmod
{
    // FMOD's ASIO output only finds and opens drivers on a single-threaded COM
    // apartment (verified with FMOD 2.03.14: 0 drivers on an MTA thread). Unity's
    // main thread apartment is not something the mod controls, so ASIO systems are
    // created, initialised and released on this dedicated STA thread, which lives
    // as long as the FMOD System. Other FMOD calls stay on Unity's main thread;
    // the FMOD API is thread-safe.
    internal sealed class FmodStaThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly AutoResetEvent _work = new AutoResetEvent(false);
        private readonly AutoResetEvent _done = new AutoResetEvent(false);
        private Action _action;
        private Exception _exception;
        private volatile bool _exit;

        [DllImport("ole32")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
        [DllImport("ole32")] private static extern void CoUninitialize();

        internal FmodStaThread(string name)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = name };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        internal void Invoke(Action action)
        {
            _action = action;
            _exception = null;
            _work.Set();
            _done.WaitOne();
            if (_exception != null) ExceptionDispatchInfo.Capture(_exception).Throw();
        }

        internal static T RunOnce<T>(Func<T> function)
        {
            T result = default(T);
            using (var thread = new FmodStaThread("LLADOFAI FMOD COM"))
                thread.Invoke(() => result = function());
            return result;
        }

        private void Run()
        {
            // S_OK or S_FALSE: this thread is (now) apartment-threaded.
            int hr = CoInitializeEx(IntPtr.Zero, 0x2 /* COINIT_APARTMENTTHREADED */);
            try
            {
                while (true)
                {
                    _work.WaitOne();
                    if (_exit) return;
                    try { _action(); }
                    catch (Exception ex) { _exception = ex; }
                    _action = null;
                    _done.Set();
                }
            }
            finally
            {
                if (hr >= 0) CoUninitialize();
            }
        }

        public void Dispose()
        {
            _exit = true;
            _work.Set();
            _thread.Join(5000);
            _work.Close();
            _done.Close();
        }
    }
}
