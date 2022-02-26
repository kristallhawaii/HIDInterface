using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Globalization;

namespace HIDAPIInterface
{

    public class USBHIDDevice : IDisposable
    {
        public bool IsOpen
        {
            get { return DeviceHandle != IntPtr.Zero; }
        }

        // If the read process grabs ownership of device
        // and blocks (unable to get any data from device) 
        // for more than Timeout millisecons 
        // it will abandon reading, pause for readIntervalInMillisecs
        // and try reading again.
        private int readTimeoutInMillisecs = 100;
        public int ReadTimeoutInMillisecs
        {
            get { lock (syncLock) { return  readTimeoutInMillisecs; } }
            set { lock(syncLock) {  readTimeoutInMillisecs = value; } }
        }

        // Interval of time between two reads,
        // during this time the device is free and 
        // we can write to it.
        private int readIntervalInMillisecs = 100;
        public int ReadIntervalInMillisecs
        {
            get { lock (syncLock) { return readIntervalInMillisecs; } }
            set { lock(syncLock) { readIntervalInMillisecs = value; } }
        }

        // for async reading
        private readonly object syncLock = new object();

        // Flag: Has Dispose already been called?
        // Marked as volatile because Dispose() can be called from another thread.
        private volatile bool disposed = false;

        private IntPtr DeviceHandle = IntPtr.Zero;

        // this will be the return buffer for strings,
        // make it big, becasue by the HID spec (can not find page)
        // we are allowed to request more bytes than the device can return.
        private StringBuilder pOutBuf = new StringBuilder(1024);

        // This is very convinient to use for the 90% of devices that 
        // dont use ReportIDs and so have only one input report
        private readonly int DefaultInputReportLength = 65;
        private readonly int DefaultOutputReportLength = 65;

        // This only affects the read function.
        // receiving / sending a feature report,
        // and writing to device always requiers you to prefix the
        // data with a Report ID (use 0x00 if device does not use Report IDs)
        // however when reading if the device does NOT use Report IDs then
        // the prefix byte is NOT inserted. On the other hand if the device uses 
        // Report IDs then when reading we must read +1 byte and byte 0 
        // of returned data array will be the Report ID.
        readonly private bool hasReportIds = false;

        private readonly byte defaulReportId = 0;

        // HIDAPI does not provide any way to get or parse the HID Report Descriptor,
        // This means you must know in advance what it the report size for your device.
        // For this reason, reportLen is a necessary parameter to the constructor.
        // 
        // Serial Number is optional, pass null (do NOT pass an empty string) if it is unknown.
        // 
        public USBHIDDevice(ushort VendorID
            , ushort ProductID
            , string serial_number
            , bool HasReportIDs 
            , int defaultInputReportLen )
        {
            if (serial_number == "")
                serial_number = null;
            IntPtr device_info = HidApi.hid_enumerate(VendorID, ProductID);
            DeviceHandle = HidApi.hid_open(VendorID, ProductID, serial_number);
            if (defaultInputReportLen < 0 || defaultInputReportLen > 65)
                defaultInputReportLen = 65;
            if (AssertValidDev())
            {
                DefaultInputReportLength = defaultInputReportLen;
                hasReportIds = HasReportIDs;
                }
        }

        private Boolean AssertValidDev()
        {
            if (DeviceHandle == IntPtr.Zero)
            {
               // throw new Exception("No device opened");
                return false;
            }
            else
                return true;
        }

        #region Write USB HID output report

        public int Write(byte[] user_data, byte reportID)
        {
            int wirttenBytes = 0;
            AssertValidDev();
            // so we don't read and write at the same time
            lock (syncLock)
            {
                byte[] output_report = new byte[user_data.Length + 1];
                output_report[0] = reportID; //reportID = 0;
                Array.Copy(user_data, 0, output_report, 1, user_data.Length);
                int block = HidApi.hid_set_nonblocking(DeviceHandle, 0);
                wirttenBytes = HidApi.hid_write(DeviceHandle, output_report, (uint)DefaultOutputReportLength);
                if (wirttenBytes < 0)
                {
                    block = HidApi.hid_set_nonblocking(DeviceHandle, 0);
                    ulong err = HidApi.hid_error_num(DeviceHandle);
                    String hiderror = System.Runtime.InteropServices.Marshal.PtrToStringAuto(HidApi.hid_error(DeviceHandle));

                    throw new Exception("HIDAPI: Failed to write. Errorcode: "+err.ToString() +"Errormsg: " + hiderror);
                    
                }
                return wirttenBytes;
            }
        }
        /// <summary>
        /// Write to the usb device (HID output report). Output ReportID is always 0.
        /// </summary>
        /// <param name="user_data"></param>
        /// <returns>number of written bytes. Written bytes below 0 indicates an error</returns>
        public int Write(byte[] user_data)
        {
            return Write(user_data, defaulReportId);
        }
        #endregion

        #region Read USB HID input report
        public int Read(byte[] ret)
        {
            return Read(ret, readTimeoutInMillisecs, DefaultInputReportLength);
        }
        public int Read(byte[] ret, int timeout)
        {
            return Read(ret, timeout, DefaultInputReportLength);
        }

        public int Read(byte[] readbuffer, int timeout, int length)
        {
            AssertValidDev();
            lock (syncLock)
            {
                if (length <= 0)
                {
                    length = DefaultInputReportLength;
                }
                if (readbuffer.Length < length)
                    throw new Exception("Buffer too small");

                int read_bytes = HidApi.hid_read_timeout(DeviceHandle, readbuffer, (uint)length, timeout);
                if (read_bytes < 0)
                {
                    String hiderror = System.Runtime.InteropServices.Marshal.PtrToStringAuto(HidApi.hid_error(DeviceHandle));
                    throw new Exception("HIDAPI: Failed to Read. Errormsg: " + hiderror);
                }
                return read_bytes;
            }
        }
        #endregion

        #region Control functions

        public Boolean SetNonBlocking(Boolean nonblock)
        {
            int nblock = 0;
            if (nonblock)
                nblock = 1;
            else
                nblock = 0;
            if (HidApi.hid_set_nonblocking(DeviceHandle, nblock) < 0)
            {
                throw new Exception("failed to to set nonblock mode");
            }
            else
                return true;
        }

        public bool FlushReadBuf()
        {
            int ret = -2;
            AssertValidDev();
            lock (syncLock)
            {
                ret = HidApi.hid_flush_input(DeviceHandle);
            }
            if (ret == 0)
                return true;
            else
                return false;
        }


        public string GetErrorString()
        {
            AssertValidDev();
            IntPtr ret = HidApi.hid_error(DeviceHandle);
            // I can not find the info in the docs, but guess this frees 
            // the ret pointer after we created a managed string object
            // else this would be a memory leak
            return Marshal.PtrToStringAuto(ret);
        }


        // All the string functions are in a little bit of trouble becasue 
        // wchar_t is 2 bytes on windows and 4 bytes on linux.
        // So we should just alloc a hell load of space for the return buffer.
        // 
        // We must divide Capacity / 4 because this takes the buffer length in multiples of 
        // wchar_t whoose length is 4 on Linux and 2 on Windows. So we allocate a big 
        // buffer beforehand and just divide the capacity by 4.
        public string GetIndexedString(int index)
        {
            lock(syncLock)
            {
                AssertValidDev();
                if (HidApi.hid_get_indexed_string(DeviceHandle, index, pOutBuf, (uint)pOutBuf.Capacity / 4) < 0)
                {
                    throw new Exception("failed to get indexed string");
                }
                return pOutBuf.ToString();
            }
        }
        #endregion

        #region String descriptor functions
        public string GetManufacturerString()
        {
            lock (syncLock)
            {
                AssertValidDev();
                if (HidApi.hid_get_manufacturer_string(DeviceHandle, pOutBuf, (uint)pOutBuf.Capacity / 4) < 0)
                {
                    throw new Exception("failed to get manufacturer string");
                }
                return pOutBuf.ToString();
            }
        }

        public string GetProductString()
        {
            lock (syncLock)
            {
                AssertValidDev();
                if (HidApi.hid_get_product_string(DeviceHandle, pOutBuf, (uint)pOutBuf.Capacity / 4) < 0)
                {
                    throw new Exception("failed to get product string");
                }
                return pOutBuf.ToString();
            }
        }

        public string GetSerialNumberString()
        {
            lock (syncLock)
            {
                AssertValidDev();
                if (HidApi.hid_get_serial_number_string(DeviceHandle, pOutBuf, (uint)pOutBuf.Capacity / 4) < 0)
                {
                    throw new Exception("failed to get serial number string");
                }
                return pOutBuf.ToString();
            }
        }



        public string Description()
        {
            AssertValidDev();
            return string.Format("Manufacturer: {0}\nProduct: {1}\nSerial number:{2}\n"
                , GetManufacturerString(), GetProductString(), GetSerialNumberString());
        }
        #endregion

        // Public implementation of Dispose pattern callable by consumers.
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // Protected implementation of Dispose pattern.
        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }
            // Free any UN-managed objects here.
            // so we are not reading or writing as the device gets closed
            lock (syncLock)
            {
                if (IsOpen)
                {
                    HidApi.hid_close(DeviceHandle);
                    DeviceHandle = IntPtr.Zero;
                }
            }
            HidApi.hid_exit();
            // mark object as having been disposed
            disposed = true;
        }

        ~USBHIDDevice()
        {
            Dispose(false);
        }

    }
}


