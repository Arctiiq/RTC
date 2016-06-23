﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BizHawk.Common;
using BizHawk.Emulation.Common;
using BizHawk.Emulation.Cores.Computers.Commodore64.Cassette;

namespace BizHawk.Emulation.Cores.Computers.Commodore64.Serial
{
    public sealed class SerialPort : IDriveLight
    {
        public Func<bool> ReadMasterAtn = () => true;
        public Func<bool> ReadMasterClk = () => true;
        public Func<bool> ReadMasterData = () => true;

        private SerialPortDevice _device;
        private bool _connected;

        public void HardReset()
        {
            if (_connected)
            {
                _device.HardReset();
            }
        }

        public void ExecutePhase()
        {
            if (_connected)
            {
                _device.ExecutePhase();
            }
        }

        public void ExecuteDeferred(int cycles)
        {
            if (_connected)
            {
                _device.ExecuteDeferred(cycles);
            }
        }

        public bool ReadDeviceClock()
        {
            return !_connected || _device.ReadDeviceClk();
        }

        public bool ReadDeviceData()
        {
            return !_connected || _device.ReadDeviceData();
        }

        public bool ReadDeviceLight()
        {
            return _connected && _device.ReadDeviceLight();
        }

        public void SyncState(Serializer ser)
        {
            SaveState.SyncObject(ser, this);
        }

        public void Connect(SerialPortDevice device)
        {
            _connected = device != null;
            _device = device;
            if (_device == null)
            {
                return;
            }

            _device.ReadMasterAtn = () => ReadMasterAtn();
            _device.ReadMasterClk = () => ReadMasterClk();
            _device.ReadMasterData = () => ReadMasterData();
        }

        public bool DriveLightEnabled { get { return true; } }
        public bool DriveLightOn { get { return ReadDeviceLight(); } }
    }
}
