using com.clusterrr.Famicom.Containers;
using com.clusterrr.Famicom.DumperConnection;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using static com.clusterrr.Famicom.Dumper.FlashWriters.CFIInfo;

namespace com.clusterrr.Famicom.Dumper.FlashWriters
{
    public class CoolgirlWriter : FlashWriter
    {
        const int MAPPER_NUMBER = 342;
        const string MAPPER_STRING = "COOLGIRL";

        protected override IFamicomDumperConnectionExt dumper { get; }
        protected override int BankSize => 0x8000;
        protected override FlashEraseMode EraseMode => FlashEraseMode.Sector;
        protected override bool CanUsePpbs => true;

        public CoolgirlWriter(IFamicomDumperConnectionExt dumper)
        {
            this.dumper = dumper;
        }

        protected override void Init()
        {

        }

        protected override bool CheckMapper(ushort mapper, byte submapper)
        {
            return mapper == MAPPER_NUMBER;
        }

        protected override bool CheckMapper(string mapper)
        {
            return mapper == MAPPER_STRING;
        }

        protected override FlashInfo GetFlashInfo()
        {
            var cfi = FlashHelper.GetCFIInfo(dumper);
            return new FlashInfo()
            {
                DeviceSize = (int)cfi.DeviceSize,
                MaximumNumberOfBytesInMultiProgram = cfi.MaximumNumberOfBytesInMultiProgram,
                Regions = cfi.EraseBlockRegionsInfo
            };
        }

        protected override void InitBanking()
        {
            dumper.WriteCpu(0x5007, 0x04); // enable PRG write
            dumper.WriteCpu(0x5002, 0xFE); // mask = 32K
        }

        protected override void Erase(int offset)
        {
            SelectBank(offset / BankSize);
            //dumper.EraseFlashSector();
            ushort sectorAddress = (ushort)(0x8000 | (0xFFFF & ((ushort)offset)));

            dumper.WriteCpu(sectorAddress, 0xF0);
            dumper.WriteCpu(0x8AAA, 0xAA);
            dumper.WriteCpu(0x8555, 0x55);
            dumper.WriteCpu(0x8AAA, 0x80);
            dumper.WriteCpu(0x8AAA, 0xAA);
            dumper.WriteCpu(0x8555, 0x55);
            dumper.WriteCpu(sectorAddress, 0x30);

            var startTime = Environment.TickCount64;
            while (true)
            {
                byte b = dumper.ReadCpu(sectorAddress);
                if (b == 0xFF)
                    break;
                if ((Environment.TickCount64 - startTime) >= 1500)
                {
                    Console.WriteLine(@" - erase failed");
                    throw new Exception($"Erase sector {sectorAddress:x04}");
                }
            }
        }

        protected override void Write(byte[] data, int offset)
        {
            SelectBank(offset / BankSize);
            ushort sectorAddress = (ushort)(0x8000 | (0xFFFF & ((ushort)offset)));
            dumper.WriteFlash(sectorAddress, data);
        }

        protected override ushort ReadCrc(int offset)
        {
            SelectBank(offset / BankSize);
            ushort sectorAddress = (ushort)(0x8000 | (0xFFFF & ((ushort)offset)));
            return dumper.ReadCpuCrc(sectorAddress, BankSize);
        }

        protected override void PPBClear()
        {
            SelectBank(0);
            FlashHelper.PPBClear(dumper);
        }

        protected override void PPBSet(int offset)
        {
            SelectBank(offset / BankSize);
            FlashHelper.PPBSet(dumper);
        }

        public override void PrintFlashInfo()
        {
            Program.Reset(dumper);
            Init();
            InitBanking();
            var cfi = FlashHelper.GetCFIInfo(dumper);
            FlashHelper.PrintCFIInfo(cfi);
            FlashHelper.LockBitsCheckPrint(dumper);
            FlashHelper.PPBLockBitCheckPrint(dumper);
        }

        private void SelectBank(int bank)
        {
            byte r0 = (byte)(bank >> 7);
            byte r1 = (byte)(bank << 1);
            dumper.WriteCpu(0x5000, r0, r1);
        }   
    }
}
