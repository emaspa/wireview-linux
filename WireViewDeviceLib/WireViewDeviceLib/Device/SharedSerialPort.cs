using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;

namespace WireView2.Device
{
    /// <summary>
    /// A <see cref="SerialPort"/> wrapper that serializes access to USB sensor devices
    /// across processes using a named, global mutex.
    ///
    /// The mutex is held only for the duration of a single open/transaction/close
    /// cycle and is released in a <c>finally</c> on every exit path (port-open
    /// failure, close failure, dispose). A held <c>Global\</c> mutex blocks every
    /// sensor app on the machine — including HWiNFO and the official client — so a
    /// leaked release here previously wedged the device system-wide.
    /// </summary>
    internal sealed class SharedSerialPort : SerialPort
    {
        private const string MutexName = @"Global\Access_USB_Sensors";
        private readonly Mutex _mutex = new Mutex(false, MutexName);

        /// <summary>
        /// Default wait time when acquiring the mutex. Override via ctor if needed.
        /// </summary>
        private int MutexTimeout { get; set; } = 2000; // ms
        private bool hasMutex = false;

        public SharedSerialPort()
        {
        }

        public SharedSerialPort(string portName) : base(portName)
        {
        }

        public SharedSerialPort(string portName, int baudRate) : base(portName, baudRate)
        {
        }

        public SharedSerialPort(string portName, int baudRate, Parity parity) : base(portName, baudRate, parity)
        {
        }

        public SharedSerialPort(string portName, int baudRate, Parity parity, int dataBits)
            : base(portName, baudRate, parity, dataBits)
        {
        }

        public SharedSerialPort(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
            : base(portName, baudRate, parity, dataBits, stopBits)
        {
        }

        public new bool Open()
        {
            if (hasMutex) return true; // already holding the bus + port on this instance

            bool acquired;
            try
            {
                acquired = _mutex.WaitOne(MutexTimeout);
            }
            catch (AbandonedMutexException)
            {
                // A previous owner died without releasing; ownership now passes to us.
                acquired = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SharedSerialPort.Open] WaitOne failed: {ex.Message}");
                return false;
            }

            // Bus is busy (another process/instance holds it). Report unavailable so
            // the caller surfaces a failed read instead of pretending to be connected.
            if (!acquired) return false;

            try
            {
                base.Open();
                hasMutex = true;
                return true;
            }
            catch (Exception ex)
            {
                // Port couldn't be opened (e.g. already in use). Release the bus lock
                // immediately so we never wedge other sensor apps, then report failure.
                Debug.WriteLine($"[SharedSerialPort.Open] base.Open failed: {ex.Message}");
                ReleaseMutexSafe();
                return false;
            }
        }

        public new void Close()
        {
            if (!hasMutex) return;
            try
            {
                // Close the underlying stream (not base.Close(), which is SerialPort's
                // Dispose and would tear the port down for good / recurse via Dispose).
                if (IsOpen)
                {
                    try { BaseStream.Flush(); BaseStream.Close(); } catch { /* best-effort */ }
                }
            }
            finally
            {
                // Always release the bus lock, even if closing the port threw. A
                // swallowed release here is exactly what left the global mutex held
                // and bricked sensor access machine-wide.
                ReleaseMutexSafe();
            }
        }

        /// <summary>
        /// Release the global mutex if we hold it. A .NET <see cref="Mutex"/> is
        /// thread-affine — only the acquiring thread may release it — which the
        /// device layer's per-transaction Open/Close pairing guarantees. A
        /// wrong-thread release is logged loudly rather than silently swallowed,
        /// because it signals a real ownership bug (and would otherwise leak the lock).
        /// </summary>
        private void ReleaseMutexSafe()
        {
            if (!hasMutex) return;
            hasMutex = false; // clear first so a failed release can't cause a double-release
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException ex)
            {
                Debug.WriteLine($"[SharedSerialPort] ReleaseMutex called off the owning thread (ownership bug): {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SharedSerialPort] ReleaseMutex failed: {ex.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    // Teardown must never leave the global mutex held. Release the lock
                    // if we still own it, then dispose the mutex. base.Dispose below
                    // closes the port — don't call Close()/base.Close() here, as
                    // SerialPort.Close() routes through Dispose() and would recurse.
                    ReleaseMutexSafe();
                    _mutex.Dispose();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        public new void Write(byte[] buffer, int offset, int count)
        {
            if (hasMutex)
            {
                base.Write(buffer, offset, count);
            }
        }

        public new int Read(byte[] buffer, int offset, int count)
        {
            if (hasMutex)
            {
                return base.Read(buffer, offset, count);
            }
            return 0;
        }

        public new void DiscardInBuffer()
        {
            if (hasMutex)
            {
                base.DiscardInBuffer();
            }
        }

        public new void DiscardOutBuffer()
        {
            if (hasMutex)
            {
                base.DiscardOutBuffer();
            }

        }
    }
}
