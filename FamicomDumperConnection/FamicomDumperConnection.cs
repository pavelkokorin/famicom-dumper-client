﻿using FTD2XX_NET;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Management;
using com.clusterrr.Famicom.Containers;

namespace com.clusterrr.Famicom.DumperConnection
{
    public class FamicomDumperConnection : MarshalByRefObject, IDisposable, IFamicomDumperConnection
    {
        const int PortBaudRate = 250000;
        const ushort DefaultMaxReadPacketSize = 1024;
        const ushort DefaultMaxWritePacketSize = 1024;
        const byte Magic = 0x46;
        string[] DeviceNames = new string[] { "Famicom Dumper/Programmer", "Famicom Dumper/Writer" };

        public string PortName { get; set; }
        public byte ProtocolVersion { get; private set; } = 0;
        public bool Verbose { get; set; } = false;
        public uint Timeout
        {
            get
            {
                return timeout;
            }
            set
            {
                timeout = value;
                if (serialPort != null)
                {
                    serialPort.ReadTimeout = (int)timeout;
                    serialPort.WriteTimeout = (int)timeout;
                }
            }
        }

        private SerialPort serialPort = null;
        private FTDI d2xxPort = null;
        public ushort MaxReadPacketSize { get; private set; }
        public ushort MaxWritePacketSize { get; private set; }

        private uint timeout = 10000;

        enum DumperCommand
        {
            STARTED = 0,
            CHR_STARTED = 1, // deprecated
            ERROR_INVALID = 2,
            ERROR_CRC = 3,
            ERROR_OVERFLOW = 4,
            PRG_INIT = 5,
            CHR_INIT = 6,
            PRG_READ_REQUEST = 7,
            PRG_READ_RESULT = 8,
            PRG_WRITE_REQUEST = 9,
            PRG_WRITE_DONE = 10,
            CHR_READ_REQUEST = 11,
            CHR_READ_RESULT = 12,
            CHR_WRITE_REQUEST = 13,
            CHR_WRITE_DONE = 14,
            //PHI2_INIT = 15,
            //PHI2_INIT_DONE = 16,
            MIRRORING_REQUEST = 17,
            MIRRORING_RESULT = 18,
            RESET = 19,
            RESET_ACK = 20,
            //PRG_EPROM_WRITE_REQUEST = 21,
            //CHR_EPROM_WRITE_REQUEST = 22,
            //EPROM_PREPARE = 23,
            //PRG_FLASH_ERASE_REQUEST = 24,
            //PRG_FLASH_WRITE_REQUEST = 25,
            //CHR_FLASH_ERASE_REQUEST = 26,
            //CHR_FLASH_WRITE_REQUEST = 27,
            //TEST_SET = 32,
            //TEST_RESULT = 33,
            COOLBOY_READ_REQUEST = 34,
            COOLBOY_ERASE_SECTOR_REQUEST = 35,
            COOLBOY_WRITE_REQUEST = 36,
            FLASH_ERASE_SECTOR_REQUEST = 37,
            FLASH_WRITE_REQUEST = 38,
            PRG_CRC_READ_REQUEST = 39,
            CHR_CRC_READ_REQUEST = 40,
            FLASH_WRITE_ERROR = 41,
            FLASH_WRITE_TIMEOUT = 42,
            FLASH_ERASE_ERROR = 43,
            FLASH_ERASE_TIMEOUT = 44,
            FDS_READ_REQUEST = 45,
            FDS_READ_RESULT_BLOCK = 46,
            FDS_READ_RESULT_END = 47,
            FDS_TIMEOUT = 48,
            FDS_NOT_CONNECTED = 49,
            FDS_BATTERY_LOW = 50,
            FDS_DISK_NOT_INSERTED = 51,
            FDS_END_OF_HEAD = 52,
            FDS_WRITE_REQUEST = 53,
            FDS_WRITE_DONE = 54,
            SET_FLASH_BUFFER_SIZE = 55,
            SET_VALUE_DONE = 56,

            BOOTLOADER = 0xFE,
            DEBUG = 0xFF
        }

        public FamicomDumperConnection(string portName = null)
        {
            this.PortName = portName;
        }

        /// <summary>
        /// Method to obtain list of Linux USB devices
        /// </summary>
        /// <returns>Array of usb devices </returns>
        private static string[] GetLinuxUsbDevices()
        {
            return Directory.GetDirectories("/sys/bus/usb/devices").Where(d => File.Exists(Path.Combine(d, "dev"))).ToArray();
        }

        /// <summary>
        /// Method to get serial port path for specified USB converter
        /// </summary>
        /// <param name="deviceSerial">Serial number of USB to serial converter</param>
        /// <returns>Path of serial port</returns>
        private static string LinuxDeviceToPort(string device)
        {
            var subdirectories = Directory.GetDirectories(device);
            foreach (var subdir in subdirectories)
            {
                // Searching for /sys/bus/usb/devices/{device}/xxx/ttyZZZ/
                var subsubdirectories = Directory.GetDirectories(subdir);
                var ports = subsubdirectories.Where(d =>
                {
                    var directory = Path.GetFileName(d);
                    return directory.Length > 3 && directory.StartsWith("tty");
                });
                if (ports.Any())
                    return $"/dev/{Path.GetFileName(ports.First())}";

                // Searching for /sys/bus/usb/devices/{device}/xxx/tty/ttyZZZ/
                var ttyDirectory = Path.Combine(subdir, "tty");
                if (Directory.Exists(ttyDirectory))
                {
                    ports = Directory.GetDirectories(ttyDirectory).Where(d =>
                    {
                        var directory = Path.GetFileName(d);
                        return directory.Length > 3 && directory.StartsWith("tty");
                    });
                    if (ports.Any())
                        return $"/dev/{Path.GetFileName(ports.First())}";
                }
            }
            return null;
        }

        /// <summary>
        /// Method to get serial port path for specified USB converter
        /// </summary>
        /// <param name="deviceSerial">Serial number of USB to serial converter</param>
        /// <returns>Path of serial port</returns>
        private static string LinuxDeviceSerialToPort(string deviceSerial)
        {
            var devices = GetLinuxUsbDevices().Where(d =>
            {
                var serialFile = Path.Combine(d, "serial");
                return File.Exists(serialFile) && File.ReadAllText(serialFile).Trim() == deviceSerial;
            });
            if (!devices.Any()) return null;
            var device = devices.First();
            return LinuxDeviceToPort(device);
        }

        private static SerialPort OpenPort(string name, int timeout)
        {
            var sPort = new SerialPort();
            sPort.PortName = name;
            sPort.WriteTimeout = timeout;
            sPort.ReadTimeout = timeout;
            if (!name.Contains("ttyACM"))
            {
                // Not supported by ACM devices
                sPort.BaudRate = PortBaudRate;
                sPort.Parity = Parity.None;
                sPort.DataBits = 8;
                sPort.StopBits = StopBits.One;
                sPort.Handshake = Handshake.None;
                sPort.DtrEnable = false;
                sPort.RtsEnable = false;
            }
            sPort.NewLine = Environment.NewLine;
            sPort.Open();
            return sPort;
        }

        public void Open()
        {
            ProtocolVersion = 0;
            MaxReadPacketSize = DefaultMaxReadPacketSize;
            MaxWritePacketSize = DefaultMaxWritePacketSize;

            SerialPort sPort = null;
            string portName = PortName;
            // Is port specified?
            if (string.IsNullOrEmpty(portName) || portName.ToLower() == "auto")
            {
                portName = null;
                // Need to autodetect port
                if (!IsRunningOnMono()) // Is it running on Windows?
                {
                    // First of all lets check bus reported device description
                    var allComPorts = Win32DeviceMgmt.GetAllCOMPorts();
                    foreach (var port in allComPorts)
                    {
                        if (!DeviceNames.Contains(port.bus_description))
                            continue;
                        // Seems like it's dumper port but it can me already busy
                        try
                        {
                            sPort = OpenPort(port.name, (int)Timeout);
                            // It's not busy
                            portName = port.name;
                            Console.WriteLine($"Autodetected virtual serial port: {portName}");
                            break;
                        }
                        catch
                        {
                            continue;
                        }
                    }

                    if (portName == null)
                    {
                        try
                        {
                            // Port still not detected, ising Windows FTDI driver to determine serial number
                            FTDI myFtdiDevice = new FTDI();
                            uint ftdiDeviceCount = 0;
                            FTDI.FT_STATUS ftStatus = FTDI.FT_STATUS.FT_OK;
                            // FTDI serial number autodetect
                            ftStatus = myFtdiDevice.GetNumberOfDevices(ref ftdiDeviceCount);
                            // Check status
                            if (ftStatus != FTDI.FT_STATUS.FT_OK)
                                throw new IOException("Failed to get number of devices (error " + ftStatus.ToString() + ")");

                            // If no devices available, return
                            if (ftdiDeviceCount == 0)
                                throw new IOException("Failed to get number of devices (error " + ftStatus.ToString() + ")");

                            // Allocate storage for device info list
                            FTDI.FT_DEVICE_INFO_NODE[] ftdiDeviceList = new FTDI.FT_DEVICE_INFO_NODE[ftdiDeviceCount];

                            // Populate our device list
                            ftStatus = myFtdiDevice.GetDeviceList(ftdiDeviceList);

                            portName = null;
                            if (ftStatus == FTDI.FT_STATUS.FT_OK)
                            {
                                var dumpers = ftdiDeviceList.Where(d => DeviceNames.Contains(d.Description));
                                portName = dumpers.First().SerialNumber;
                                Console.WriteLine($"Autodetected USB device serial number: {portName}");
                            }
                            if (ftStatus != FTDI.FT_STATUS.FT_OK)
                                throw new IOException("Failed to get FTDI devices (error " + ftStatus.ToString() + ")");
                        }
                        catch
                        {
                            throw new IOException($"{DeviceNames[0]} not found");
                        }
                    }
                }
                else
                {
                    // Linux?
                    var devices = GetLinuxUsbDevices();
                    var dumpers = devices.Where(d =>
                    {
                        var productFile = Path.Combine(d, "product");
                        return File.Exists(productFile) && DeviceNames.Contains(File.ReadAllText(productFile).Trim());
                    });
                    if (!dumpers.Any())
                        throw new IOException($"{DeviceNames[0]} not found");
                    portName = LinuxDeviceToPort(dumpers.First());
                    if (string.IsNullOrEmpty(portName))
                        throw new IOException($"Can't detect device path");
                    Console.WriteLine($"Autodetected USB device path: {portName}");
                }
            }

            if (portName.ToUpper().StartsWith("COM") || IsRunningOnMono())
            {
                // Using VCP
                if (IsRunningOnMono() && !File.Exists(portName))
                {
                    // Need to convert serial number to port address
                    var ttyPath = LinuxDeviceSerialToPort(portName);
                    if (string.IsNullOrEmpty(ttyPath))
                        throw new IOException($"Device with serial number {portName} not found");
                    portName = ttyPath;
                    Console.WriteLine($"Autodetected USB device path: {portName}");
                }
                // Port specified 
                if (sPort == null)
                {
                    // If not already opened
                    sPort = OpenPort(portName, (int)Timeout);
                }
                serialPort = sPort;
            }
            else
            {
                // Using Windows FTDI driver
                FTDI.FT_STATUS ftStatus = FTDI.FT_STATUS.FT_OK;
                // Create new instance of the FTDI device class
                FTDI myFtdiDevice = new FTDI();
                // Open first device in our list by serial number
                ftStatus = myFtdiDevice.OpenBySerialNumber(portName);
                if (ftStatus != FTDI.FT_STATUS.FT_OK)
                    throw new IOException($"Failed to open device (error {ftStatus})");
                // Set data characteristics - Data bits, Stop bits, Parity
                ftStatus = myFtdiDevice.SetTimeouts(Timeout, Timeout);
                if (ftStatus != FTDI.FT_STATUS.FT_OK)
                    throw new IOException($"Failed to set timeouts (error {ftStatus})");
                ftStatus = myFtdiDevice.SetDataCharacteristics(FTDI.FT_DATA_BITS.FT_BITS_8, FTDI.FT_STOP_BITS.FT_STOP_BITS_1, FTDI.FT_PARITY.FT_PARITY_NONE);
                if (ftStatus != FTDI.FT_STATUS.FT_OK)
                    throw new IOException($"Failed to set data characteristics (error {ftStatus})");
                // Set flow control
                ftStatus = myFtdiDevice.SetFlowControl(FTDI.FT_FLOW_CONTROL.FT_FLOW_NONE, 0x11, 0x13);
                if (ftStatus != FTDI.FT_STATUS.FT_OK)
                    throw new IOException($"Failed to set flow control (error {ftStatus})");
                // Set up device data parameters
                ftStatus = myFtdiDevice.SetBaudRate(PortBaudRate);
                if (ftStatus != FTDI.FT_STATUS.FT_OK)
                    throw new IOException($"Failed to set Baud rate (error {ftStatus})");
                // Set latency
                ftStatus = myFtdiDevice.SetLatency(0);
                if (ftStatus != FTDI.FT_STATUS.FT_OK)
                    throw new IOException($"Failed to set latency (error {ftStatus})");
                d2xxPort = myFtdiDevice;
            }
        }

        public void Close()
        {
            if (serialPort != null)
            {
                if (serialPort.IsOpen)
                    serialPort.Close();
                serialPort = null;
            }
            if (d2xxPort != null)
            {
                if (d2xxPort.IsOpen)
                    d2xxPort.Close();
                d2xxPort = null;
            }
        }

        private byte[] ReadPort()
        {
            var buffer = new byte[MaxReadPacketSize + 8];
            if (serialPort != null)
            {
                var l = serialPort.Read(buffer, 0, buffer.Length);
                var result = new byte[l];
                Array.Copy(buffer, result, l);
                return result;
            }
            else if (d2xxPort != null)
            {
                uint numBytesAvailable = 0;
                FTDI.FT_STATUS ftStatus;
                int t = 0;
                do
                {
                    ftStatus = d2xxPort.GetRxBytesAvailable(ref numBytesAvailable);
                    if (ftStatus != FTDI.FT_STATUS.FT_OK)
                        throw new IOException("Failed to get number of bytes available to read (error " + ftStatus.ToString() + ")");
                    if (numBytesAvailable > 0)
                        break;
                    Thread.Sleep(10);
                    t += 10;
                    if (t >= Timeout)
                        throw new TimeoutException("Read timeout");
                } while (numBytesAvailable == 0);
                uint numBytesRead = 0;
                ftStatus = d2xxPort.Read(buffer, Math.Min(numBytesAvailable, (uint)MaxReadPacketSize + 8), ref numBytesRead);
                if (ftStatus != FTDI.FT_STATUS.FT_OK)
                    throw new IOException("Failed to read data (error " + ftStatus.ToString() + ")");
                var result = new byte[numBytesRead];
                Array.Copy(buffer, result, numBytesRead);
                return result;
            }
            return null;
        }

        void SendCommand(DumperCommand command, byte[] data)
        {
            byte[] buffer = new byte[data.Length + 5];
            buffer[0] = Magic;
            buffer[1] = (byte)command;
            buffer[2] = (byte)(data.Length & 0xFF);
            buffer[3] = (byte)((data.Length >> 8) & 0xFF);
            Array.Copy(data, 0, buffer, 4, data.Length);

            byte crc = 0;
            for (var i = 0; i < buffer.Length - 1; i++)
            {
                byte inbyte = buffer[i];
                for (int j = 0; j < 8; j++)
                {
                    byte mix = (byte)((crc ^ inbyte) & 0x01);
                    crc >>= 1;
                    if (mix != 0)
                        crc ^= 0x8C;
                    inbyte >>= 1;
                }
            }
            buffer[buffer.Length - 1] = crc;
            //foreach (var b in buffer) Console.Write(", 0x{0:X2}", b);
            if (serialPort != null)
                serialPort.Write(buffer, 0, buffer.Length);
            if (d2xxPort != null)
            {
                uint numBytesWritten = 0;
                var ftStatus = d2xxPort.Write(buffer, buffer.Length, ref numBytesWritten);
                if (ftStatus != FTDI.FT_STATUS.FT_OK)
                    throw new IOException("Failed to write to device (error " + ftStatus.ToString() + ")");
            }
        }

        (DumperCommand Command, byte[] Data) RecvCommand()
        {
            int commRecvPos = 0;
            DumperCommand commRecvCommand = 0;
            int commRecvLength = 0;
            List<byte> recvBuffer = new List<byte>();
            while (true)
            {
                var data = ReadPort();
                foreach (var b in data)
                {
                    recvBuffer.Add(b);
                    switch (commRecvPos)
                    {
                        case 0:
                            if (b == Magic)
                                commRecvPos++;
                            else
                            {
                                recvBuffer.Clear();
                                continue;
                                //throw new InvalidDataException("Received invalid magic");
                            }
                            break;
                        case 1:
                            commRecvCommand = (DumperCommand)b;
                            commRecvPos++;
                            break;
                        case 2:
                            commRecvLength = b;
                            commRecvPos++;
                            break;
                        case 3:
                            commRecvLength |= b << 8;
                            commRecvPos++;
                            break;
                        default:
                            if (recvBuffer.Count == commRecvLength + 5)
                            {
                                // CRC
                                var calculatecCRC = CRC(recvBuffer);
                                if (calculatecCRC == 0)
                                {
                                    // CRC OK
                                    if (commRecvCommand == DumperCommand.ERROR_CRC)
                                        throw new InvalidDataException("Dumper reported CRC error");
                                    else if (commRecvCommand == DumperCommand.ERROR_INVALID)
                                        throw new InvalidDataException("Dumper reported invalid magic");
                                    else if (commRecvCommand == DumperCommand.ERROR_OVERFLOW)
                                        throw new InvalidDataException("Dumper reported overflow error");
                                    else
                                        return (commRecvCommand, recvBuffer.Skip(4).Take(commRecvLength).ToArray());
                                }
                                else
                                {
                                    // CRC NOT OK
                                    throw new InvalidDataException("Received data CRC error");
                                }
                            }
                            break;
                    }
                }
            }
        }

        byte CRC(IEnumerable<byte> data)
        {
            byte commRecvCrc = 0;
            foreach (var b in data)
            {
                var inbyte = b;
                int j;
                for (j = 0; j < 8; j++)
                {
                    byte mix = (byte)((commRecvCrc ^ inbyte) & 0x01);
                    commRecvCrc >>= 1;
                    if (mix != 0)
                        commRecvCrc ^= 0x8C;
                    inbyte >>= 1;
                }
            }
            return commRecvCrc;
        }

        public bool DumperInit()
        {
            if (Verbose)
                Console.Write("Dumper initialization... ");

            bool result = false;
            var oldTimeout = Timeout;
            try
            {
                Timeout = 250;
                for (int i = 0; i < 300 && !result; i++)
                {
                    try
                    {
                        SendCommand(DumperCommand.PRG_INIT, new byte[0]);
                        var recv = RecvCommand();
                        if (recv.Command == DumperCommand.STARTED)
                        {
                            if (recv.Data.Length >= 1)
                                ProtocolVersion = recv.Data[0];
                            if (recv.Data.Length >= 3)
                                MaxReadPacketSize = (ushort)(recv.Data[1] | (recv.Data[2] << 8));
                            if (recv.Data.Length >= 5)
                                MaxWritePacketSize = (ushort)(recv.Data[3] | (recv.Data[4] << 8));
                            result = true;
                        }
                    }
                    catch { }
                }
                // Flush all queud data
                while (true)
                {
                    try
                    {
                        RecvCommand();
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            finally
            {
                Timeout = oldTimeout;
            }

            if (Verbose)
                Console.WriteLine("failed");
            return result;
        }

        public byte[] ReadCpu(ushort address, int length)
        {
            if (Verbose)
                Console.Write($"Reading 0x{length:X4}B <= 0x{address:X4} @ CPU...");
            var result = new List<byte>();
            while (length > 0)
            {
                result.AddRange(ReadCpuBlock(address, Math.Min(MaxReadPacketSize, length)));
                address += MaxReadPacketSize;
                length -= MaxReadPacketSize;
            }
            if (Verbose && result.Count <= 32)
            {
                foreach (var b in result)
                    Console.Write($" {b:X2}");
            }
            else if (Verbose)
                Console.WriteLine(" OK");
            return result.ToArray();
        }

        private byte[] ReadCpuBlock(ushort address, int length)
        {
            var buffer = new byte[4];
            buffer[0] = (byte)(address & 0xFF);
            buffer[1] = (byte)((address >> 8) & 0xFF);
            buffer[2] = (byte)(length & 0xFF);
            buffer[3] = (byte)((length >> 8) & 0xFF);
            SendCommand(DumperCommand.PRG_READ_REQUEST, buffer);
            var recv = RecvCommand();
            if (recv.Command != DumperCommand.PRG_READ_RESULT)
                throw new IOException($"Invalid data received: {recv.Command}");
            return recv.Data;
        }

        public ushort ReadCpuCrc(ushort address, int length)
        {
            if (Verbose)
                Console.Write($"Reading CRC of 0x{length:X4}b of 0x{address:X4} @ CPU...");
            var buffer = new byte[4];
            buffer[0] = (byte)(address & 0xFF);
            buffer[1] = (byte)((address >> 8) & 0xFF);
            buffer[2] = (byte)(length & 0xFF);
            buffer[3] = (byte)((length >> 8) & 0xFF);
            SendCommand(DumperCommand.PRG_CRC_READ_REQUEST, buffer);
            var recv = RecvCommand();
            if (recv.Command != DumperCommand.PRG_READ_RESULT)
                throw new IOException($"Invalid data received: {recv.Command}");
            return (ushort)(recv.Data[0] | (recv.Data[1] << 8));
        }

        public void WriteCpu(ushort address, byte data)
            => WriteCpu(address, new byte[] { data });

        public void WriteCpu(ushort address, byte[] data)
        {
            if (Verbose)
            {
                if (data.Length <= 32)
                {
                    Console.Write($"Writing ");
                    foreach (var b in data)
                        Console.Write($"0x{b:X2} ");
                    Console.Write($"=> 0x{address:X4} @ CPU...");
                }
                else
                {
                    Console.Write($"Writing 0x{data.Length:X4}B => 0x{address:X4} @ CPU...");
                }
            }
            int wlength = data.Length;
            int pos = 0;
            while (wlength > 0)
            {
                var wdata = new byte[Math.Min(MaxWritePacketSize, wlength)];
                Array.Copy(data, pos, wdata, 0, wdata.Length);
                WriteCpuBlock(address, wdata);
                address += (ushort)wdata.Length;
                pos += wdata.Length;
                wlength -= wdata.Length;
            }
            if (Verbose)
                Console.WriteLine(" OK");
            return;
        }

        private void WriteCpuBlock(ushort address, byte[] data)
        {
            int length = data.Length;
            var buffer = new byte[4 + length];
            buffer[0] = (byte)(address & 0xFF);
            buffer[1] = (byte)((address >> 8) & 0xFF);
            buffer[2] = (byte)(length & 0xFF);
            buffer[3] = (byte)((length >> 8) & 0xFF);
            Array.Copy(data, 0, buffer, 4, length);
            SendCommand(DumperCommand.PRG_WRITE_REQUEST, buffer);
            var recv = RecvCommand();
            if (recv.Command != DumperCommand.PRG_WRITE_DONE)
                throw new IOException($"Invalid data received: {recv.Command}");
        }

        public void EraseCpuFlashSector()
        {
            SendCommand(DumperCommand.FLASH_ERASE_SECTOR_REQUEST, new byte[0]);
            var recv = RecvCommand();
            if (recv.Command == DumperCommand.FLASH_ERASE_ERROR)
                throw new IOException($"Flash erase error (0x{recv.Data[0]:X2})");
            else if (recv.Command == DumperCommand.FLASH_ERASE_TIMEOUT)
                throw new TimeoutException($"Flash erase timeout");
            else if (recv.Command != DumperCommand.PRG_WRITE_DONE)
                throw new IOException($"Invalid data received: {recv.Command}");
        }

        public void WriteCpuFlash(ushort address, byte[] data)
        {
            if (Verbose)
            {
                if (data.Length <= 32)
                {
                    Console.Write($"Writing ");
                    foreach (var b in data)
                        Console.Write($"0x{b:X2} ");
                    Console.Write($"=> 0x{address:X4} @ CPU flash...");
                }
                else
                {
                    Console.Write($"Writing 0x{data.Length:X4}B => 0x{address:X4} @ CPU flash...");
                }
            }
            int wlength = data.Length;
            int pos = 0;
            while (wlength > 0)
            {
                var wdata = new byte[Math.Min(MaxWritePacketSize, wlength)];
                Array.Copy(data, pos, wdata, 0, wdata.Length);
                if (data.Select(b => b != 0xFF).Any()) // if there is any not FF byte
                    WriteCpuFlashBlock(address, wdata);
                address += (ushort)wdata.Length;
                pos += wdata.Length;
                wlength -= wdata.Length;
            }
            if (Verbose)
                Console.WriteLine(" OK");
        }

        private void WriteCpuFlashBlock(ushort address, byte[] data)
        {
            int length = data.Length;
            var buffer = new byte[4 + length];
            buffer[0] = (byte)(address & 0xFF);
            buffer[1] = (byte)((address >> 8) & 0xFF);
            buffer[2] = (byte)(length & 0xFF);
            buffer[3] = (byte)((length >> 8) & 0xFF);
            Array.Copy(data, 0, buffer, 4, length);
            SendCommand(DumperCommand.FLASH_WRITE_REQUEST, buffer);
            var recv = RecvCommand();
            if (recv.Command == DumperCommand.FLASH_WRITE_ERROR)
                throw new IOException($"Flash write error");
            else if (recv.Command == DumperCommand.FLASH_WRITE_TIMEOUT)
                throw new IOException($"Flash write timeout");
            else if (recv.Command != DumperCommand.PRG_WRITE_DONE)
                throw new IOException($"Invalid data received: {recv.Command}");
        }

        public byte[] ReadPpu(ushort address, int length)
        {
            if (Verbose)
                Console.Write($"Reading 0x{length:X4}B <= 0x{address:X4} @ PPU...");
            var result = new List<byte>();
            while (length > 0)
            {
                result.AddRange(ReadPpuBlock(address, Math.Min(MaxReadPacketSize, length)));
                address += MaxReadPacketSize;
                length -= MaxReadPacketSize;
            }
            if (Verbose && result.Count <= 32)
            {
                foreach (var b in result)
                    Console.Write($" {b:X2}");
                Console.WriteLine();
            }
            else if (Verbose)
                Console.WriteLine(" OK");
            return result.ToArray();
        }

        private byte[] ReadPpuBlock(ushort address, int length)
        {
            var buffer = new byte[4];
            buffer[0] = (byte)(address & 0xFF);
            buffer[1] = (byte)((address >> 8) & 0xFF);
            buffer[2] = (byte)(length & 0xFF);
            buffer[3] = (byte)((length >> 8) & 0xFF);
            SendCommand(DumperCommand.CHR_READ_REQUEST, buffer);
            var recv = RecvCommand();
            if (recv.Command != DumperCommand.CHR_READ_RESULT)
                throw new IOException($"Invalid data received: {recv.Command}");
            return recv.Data;
        }

        public ushort ReadPpuCrc(ushort address, int length)
        {
            if (Verbose)
                Console.Write($"Reading CRC of 0x{length:X4}b of 0x{address:X4} @ PPU...");
            var buffer = new byte[4];
            buffer[0] = (byte)(address & 0xFF);
            buffer[1] = (byte)((address >> 8) & 0xFF);
            buffer[2] = (byte)(length & 0xFF);
            buffer[3] = (byte)((length >> 8) & 0xFF);
            SendCommand(DumperCommand.CHR_CRC_READ_REQUEST, buffer);
            var recv = RecvCommand();
            if (recv.Command != DumperCommand.CHR_READ_RESULT)
                throw new IOException($"Invalid data received: {recv.Command}");
            return (ushort)(recv.Data[0] | (recv.Data[1] << 8));
        }

        public void WritePpu(ushort address, byte data)
            => WritePpu(address, new byte[] { data });

        public void WritePpu(ushort address, byte[] data)
        {
            if (Verbose)
            {
                if (data.Length <= 32)
                {
                    Console.Write($"Writing ");
                    foreach (var b in data)
                        Console.Write($"0x{b:X2} ");
                    Console.Write($"=> 0x{address:X4} @ PPU...");
                }
                else
                {
                    Console.Write($"Writing 0x{data.Length:X4}B => 0x{address:X4} @ PPU...");
                }
            }
            if (data.Length > MaxWritePacketSize) // Split packets
            {
                int wlength = data.Length;
                int pos = 0;
                while (wlength > 0)
                {
                    var wdata = new byte[Math.Min(MaxWritePacketSize, wlength)];
                    Array.Copy(data, pos, wdata, 0, wdata.Length);
                    WritePpu(address, wdata);
                    address += (ushort)wdata.Length;
                    pos += wdata.Length;
                    wlength -= wdata.Length;
                }
                if (Verbose)
                    Console.WriteLine(" OK");
                return;
            }

            int length = data.Length;
            var buffer = new byte[4 + length];
            buffer[0] = (byte)(address & 0xFF);
            buffer[1] = (byte)((address >> 8) & 0xFF);
            buffer[2] = (byte)(length & 0xFF);
            buffer[3] = (byte)((length >> 8) & 0xFF);
            Array.Copy(data, 0, buffer, 4, length);
            SendCommand(DumperCommand.CHR_WRITE_REQUEST, buffer);
            var recv = RecvCommand();
            if (recv.Command != DumperCommand.CHR_WRITE_DONE)
                throw new IOException($"Invalid data received: {recv.Command}");
        }

        public IFdsBlock[] ReadFdsBlocks(byte startBlock = 0, byte blockCount = byte.MaxValue)
        {
            if (ProtocolVersion < 3)
                throw new NotSupportedException("Dumper firmware version is too old, update it to read/write FDS cards");

            if (Verbose)
                Console.Write($"Reading FDS block(s) {startBlock}-{startBlock + blockCount - 1}... ");

            var blocks = new List<IFdsBlock>();
            var buffer = new byte[2];
            buffer[0] = startBlock;
            buffer[1] = blockCount;
            SendCommand(DumperCommand.FDS_READ_REQUEST, buffer);
            bool receiving = true;
            int currentBlock = startBlock;
            while (receiving)
            {
                var recv = RecvCommand();
                switch (recv.Command)
                {
                    case DumperCommand.FDS_READ_RESULT_BLOCK:
                        {
                            // ignore any data after invalid data
                            var data = recv.Data.Take(recv.Data.Length - 2).ToArray();
                            IFdsBlock newBlock;
                            if (currentBlock == 0)
                                newBlock = FdsBlockDiskInfo.FromBytes(data);
                            else if (currentBlock == 1)
                                newBlock = FdsBlockFileAmount.FromBytes(data);
                            else if ((currentBlock % 2) == 0)
                                newBlock = FdsBlockFileHeader.FromBytes(data);
                            else
                                newBlock = FdsBlockFileData.FromBytes(data);
                            newBlock.CrcOk = recv.Data[recv.Data.Length - 2] != 0;
                            newBlock.EndOfHeadMeet = recv.Data[recv.Data.Length - 1] != 0;
                            blocks.Add(newBlock);
                            currentBlock++;
                        }
                        break;
                    case DumperCommand.FDS_READ_RESULT_END:
                        receiving = false;
                        break;
                    case DumperCommand.FDS_NOT_CONNECTED:
                        throw new IOException("RAM adapter IO error, is it connected?");
                    case DumperCommand.FDS_DISK_NOT_INSERTED:
                        throw new IOException("Disk card is not set");
                    case DumperCommand.FDS_BATTERY_LOW:
                        throw new IOException("Battery voltage is low or power supply is not connected");
                    case DumperCommand.FDS_TIMEOUT:
                        throw new IOException("FDS read timeout");
                    case DumperCommand.FDS_END_OF_HEAD:
                        throw new IOException("End of head");
                    default:
                        throw new IOException($"Invalid data received: {recv.Command}");
                }
            }
            if (Verbose)
                Console.WriteLine(" OK");
            return blocks.ToArray();
        }

        public void WriteFdsBlocks(byte[] blockNumbers, byte[][] blocks)
        {
            if (blockNumbers.Length != blocks.Length)
                throw new ArgumentException("blockNumbers.Length != blocks.Length");
            var buffer = new byte[1 + blocks.Length + blocks.Length * 2 + blocks.Sum(e => e.Length)];
            buffer[0] = (byte)(blocks.Length);
            for (int i = 0; i < blocks.Length; i++)
            {
                buffer[1 + i] = blockNumbers[i];
            }
            for (int i = 0; i < blocks.Length; i++)
            {
                buffer[1 + blocks.Length + i * 2] = (byte)(blocks[i].Length & 0xFF);
                buffer[1 + blocks.Length + i * 2 + 1] = (byte)((blocks[i].Length >> 8) & 0xFF);
            }
            int pos = 1 + blocks.Length + blocks.Length * 2;
            foreach (var block in blocks)
            {
                Array.Copy(block, 0, buffer, pos, block.Length);
                pos += block.Length;
            }
            SendCommand(DumperCommand.FDS_WRITE_REQUEST, buffer);
            var recv = RecvCommand();
            switch (recv.Command)
            {
                case DumperCommand.FDS_WRITE_DONE:
                    return;
                case DumperCommand.FDS_NOT_CONNECTED:
                    throw new IOException("RAM adapter IO error, is it connected?");
                case DumperCommand.FDS_DISK_NOT_INSERTED:
                    throw new IOException("Disk card is not set");
                case DumperCommand.FDS_BATTERY_LOW:
                    throw new IOException("Battery low");
                case DumperCommand.FDS_TIMEOUT:
                    throw new IOException("FDS read timeout");
                case DumperCommand.FDS_END_OF_HEAD:
                    throw new IOException("End of head");
                case DumperCommand.FDS_READ_RESULT_END:
                    throw new IOException("Unexpected end of data");
                default:
                    throw new IOException($"Invalid data received: {recv.Command}");
            }
        }

        public void WriteFdsBlocks(byte[] blockNumbers, IEnumerable<IFdsBlock> blocks)
            => WriteFdsBlocks(blockNumbers, blocks.Select(b => b.ToBytes()).ToArray());
        public void WriteFdsBlocks(byte[] blockNumbers, byte[] block)
            => WriteFdsBlocks(blockNumbers, new byte[][] { block });
        public void WriteFdsBlocks(byte blockNumber, IFdsBlock block)
            => WriteFdsBlocks(new byte[] { blockNumber }, new byte[][] { block.ToBytes() });

        public bool[] GetMirroringRaw()
        {
            if (Verbose)
                Console.Write("Reading mirroring... ");
            SendCommand(DumperCommand.MIRRORING_REQUEST, new byte[0]);
            var recv = RecvCommand();
            if (recv.Command != DumperCommand.MIRRORING_RESULT)
                throw new IOException($"Invalid data received: {recv.Command}");
            var mirroringRaw = recv.Data;
            foreach (var b in mirroringRaw)
                Console.Write($"{b} ");
            Console.WriteLine();
            return mirroringRaw.Select(v => v != 0 ? true : false).ToArray();
        }

        public NesFile.MirroringType GetMirroring()
        {
            var mirroringRaw = GetMirroringRaw();
            if (mirroringRaw.Length == 1)
            {
                // Backward compatibility with old firmwares
                return mirroringRaw[0] ? NesFile.MirroringType.Vertical : NesFile.MirroringType.Horizontal;
            }
            else if (mirroringRaw.Length == 4)
            {
                var mirrstr = $"{(mirroringRaw[0] ? 1 : 0)}{(mirroringRaw[1] ? 1 : 0)}{(mirroringRaw[2] ? 1 : 0)}{(mirroringRaw[3] ? 1 : 0)}";
                switch (mirrstr)
                {
                    case "0011":
                        return NesFile.MirroringType.Horizontal; // Horizontal
                    case "0101":
                        return NesFile.MirroringType.Vertical; // Vertical
                    case "0000":
                        return NesFile.MirroringType.OneScreenA; // One-screen A
                    case "1111":
                        return NesFile.MirroringType.OneScreenB; // One-screen B
                }                
            }
            return NesFile.MirroringType.Unknown; // Unknown
        }

        public void SetMaximumNumberOfBytesInMultiProgram(uint pageSize)
        {
            if (ProtocolVersion < 3)
                throw new NotSupportedException("Dumper firmware version is too old");
            var buffer = new byte[2];
            buffer[0] = (byte)(pageSize & 0xFF);
            buffer[1] = (byte)((pageSize >> 8) & 0xFF);
            SendCommand(DumperCommand.SET_FLASH_BUFFER_SIZE, buffer);
            var recv = RecvCommand();
            if (recv.Command != DumperCommand.SET_VALUE_DONE)
                throw new IOException($"Invalid data received: {recv.Command}");
        }

        /// <summary>
        /// Simulate reset (M2 goes to Z-state for a second)
        /// </summary>
        public void Reset()
        {
            SendCommand(DumperCommand.RESET, new byte[0]);
            var recv = RecvCommand();
            if (recv.Command != DumperCommand.RESET_ACK)
                throw new IOException($"Invalid data received: {recv.Command}");
        }

        public void Bootloader()
        {
            SendCommand(DumperCommand.BOOTLOADER, new byte[0]);
        }

        private static bool IsRunningOnMono()
        {
            return Type.GetType("Mono.Runtime") != null;
        }

        public override object InitializeLifetimeService()
        {
            return null; // Infinity
        }

        public void Dispose()
        {
            Close();
        }
    }
}
