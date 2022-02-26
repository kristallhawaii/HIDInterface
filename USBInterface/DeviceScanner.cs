using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;

namespace HIDAPIInterface
{
    public class DeviceEventArgs : EventArgs
    {
        public DeviceEventArgs(byte[] data)
        {
            Data = data;
        }

        public byte[] Data { get; private set; }
    }

    public class DeviceScanner
    { 
        public event EventHandler DeviceArrived;
        public event EventHandler DeviceRemoved;

        public bool isDeviceConnected
        {
            get { return deviceConnected; }
        }

        // for async reading
        private object syncLock = new object();
        private Thread scannerThread;
        private volatile bool asyncScanOn = false;

        private volatile bool deviceConnected = false;

        private int scanIntervalMillisecs = 500;
        public int ScanIntervalInMillisecs
        {
            get { lock (syncLock) { return scanIntervalMillisecs; } }
            set { lock (syncLock) { scanIntervalMillisecs = value; } }
        }

        public bool isScanning
        {
            get { return asyncScanOn; }
        }

        public ushort VendorId { get => vendorId; set => vendorId = value; }
        public ushort ProductId { get => productId; set => productId = value; }

        private ushort vendorId;
        private ushort productId;

        // Use this class to monitor when your devices connects.
        // Note that scanning for device when it is open by another process will return FALSE
        // even though the device is connected (because the device is unavailiable)
        public DeviceScanner(ushort VendorID, ushort ProductID, int scanIntervalMillisecs = 500 )
        {
            vendorId = VendorID;
            productId = ProductID;
            ScanIntervalInMillisecs = scanIntervalMillisecs;
        }

        public bool ScanOnce()
        {
            return ScanOnce(vendorId, productId);
        }

        // scanning for device when it is open by another process will return false
        public bool ScanOnce(ushort vid, ushort pid)
        {
            IntPtr device_info = HidApi.hid_enumerate(vid, pid);
            bool device_on_bus = device_info != IntPtr.Zero;
            // freeing the enumeration releases the device, 
            // do it as soon as you can, so we dont block device from others
            HidApi.hid_free_enumeration(device_info);
            deviceConnected = device_on_bus;
            return device_on_bus;
        }

        public void StartAsyncScan()
        {
            // Build the thread to listen for reads
            if (asyncScanOn)
            {
                // dont run more than one thread
                return;
            }
            asyncScanOn = true;
            scannerThread = new Thread(ScanLoop);
            scannerThread.IsBackground = true;
            scannerThread.Name = "HidApiAsyncDeviceScanThread";
            scannerThread.Start();
        }

        public void StopAsyncScan()
        {
            asyncScanOn = false;
            if (scannerThread != null)
            {
                scannerThread.Join(200);
                if (scannerThread.IsAlive)
                    scannerThread.Abort();
            }
        }

        public bool WaitScan(int timeoutms= 5000, int intervallms=500)
        {
            Stopwatch timeoutWatch = new Stopwatch();
            timeoutWatch.Start();

            if (timeoutms < intervallms)
                timeoutms = 3 * intervallms;
            bool device_on_bus = false;
            while (true)
            {
                if (timeoutWatch.ElapsedMilliseconds > timeoutms)
                {
                    timeoutWatch.Stop();
                    break;
                }
                try
                {
                    device_on_bus = ScanOnce(vendorId, productId);
                    if (device_on_bus)
                    {
                        // just found new device
                        break;
                    }
                }
                catch (Exception e)
                {
                    // stop scan, user can manually restart again with StartAsyncScan()
                    Console.WriteLine(e.ToString());
                }

                Thread.Sleep(intervallms);
            }
            return deviceConnected;
        }

        private void ScanLoop()
        {
            var culture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;

            // The read has a timeout parameter, so every X milliseconds
            // we check if the user wants us to continue scanning.
            bool deviceWasConnected = false;
            while (asyncScanOn)
            {
                try
                {
                    //IntPtr device_info = HidApi.hid_enumerate(vendorId, productId);
                    //bool device_on_bus = device_info != IntPtr.Zero;
                    //// freeing the enumeration releases the device, 
                    //// do it as soon as you can, so we dont block device from others
                    //HidApi.hid_free_enumeration(device_info);
                    
                    bool device_on_bus = ScanOnce(vendorId, productId);
                    if (device_on_bus && !deviceWasConnected)
                    {
                        // just found new device
                        deviceConnected = true;
                        if (DeviceArrived != null)
                        {
                            Console.WriteLine("Scanner: Device VID=0x{0:X4} PID=0x{1:X4} arrived!", vendorId, productId);
                            DeviceArrived(this, EventArgs.Empty);
                        }
                    }
                    if (!device_on_bus && deviceWasConnected)
                    {
                        // just lost device connection
                        deviceConnected = false;
                        if (DeviceRemoved != null)
                        {
                            Console.WriteLine("Scanner: Device VID 0x{0:X4} PID 0x{1:X4} removed!",vendorId,productId);
                            DeviceRemoved(this, EventArgs.Empty);
                        }
                    }
                    deviceWasConnected = deviceConnected;
                }
                catch (Exception e)
                {
                    // stop scan, user can manually restart again with StartAsyncScan()
                    Console.WriteLine(e.ToString());
                    asyncScanOn = false;
                }

                // when read 0 bytes, sleep and read again
                Thread.Sleep(ScanIntervalInMillisecs);
            }
        }
    }
}
