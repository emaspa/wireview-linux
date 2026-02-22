using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Numerics;
using System.Threading;

namespace WireView2.Device
{
    /// <summary>
    /// A <see cref="SerialPort"/> wrapper that serializes access to USB sensor devices
    /// across processes using a named, global mutex.
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
            try
            {
                if (_mutex.WaitOne(MutexTimeout))
                {
                    base.Open();
                    hasMutex = true;
                }
            }
            catch (AbandonedMutexException)
            {
                // Another process terminated without releasing the mutex.
                // We can still acquire it, so just proceed.
                try
                {
                    base.Open();
                    hasMutex = true;
                }
                catch (Exception ex)
                {
                    _mutex.ReleaseMutex();
                    hasMutex = false;
                    Debug.WriteLine($"[{DateTime.Now.ToString("mm:ss.fff")}] SharedSerialPort.Open: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                hasMutex = false;
                Debug.WriteLine($"[{DateTime.Now.ToString("mm:ss.fff")}] SharedSerialPort.Open: {ex.Message}");
            }
            return hasMutex;
        }

        public new void Close()
        {
            if (hasMutex)
            {
                if (IsOpen)
                {
                    try
                    {
                        BaseStream.Flush();
                        BaseStream.Close();
                    }
                    catch { }
                }
                try { 
                    hasMutex = false;
                    _mutex.ReleaseMutex();
                }
                catch(Exception ex) {
                    Debug.WriteLine($"[{DateTime.Now.ToString("mm:ss.fff")}] SharedSerialPort.Close: {ex.Message}");
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    // Don’t guard Dispose with the mutex; Dispose may be called during teardown and
                    // we want to avoid deadlocks if another thread/process is misbehaving.
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
