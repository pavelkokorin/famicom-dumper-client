class MMC3 : IMapper
{
    public string Name { get => "Mapper 176"; }
    public string UnifName { get => "FK23CA"; }
    public int Number { get => 176; }
    public int DefaultPrgSize { get => 512 * 1024; }
    public int DefaultChrSize { get => 256 * 1024; }

    public void DumpPrg(IFamicomDumperConnection dumper, List<byte> data, int size)
    {
        dumper.WriteCpu(0xA000, 0x00);
        dumper.WriteCpu(0x5FF0, 0x00); // Mode PRG 512k

        DumpPrgMmc3(dumper, data, size);
    }

    public void DumpChr(IFamicomDumperConnection dumper, List<byte> data, int size)
    {
        dumper.WriteCpu(0xA000, 0x00);
        dumper.WriteCpu(0x5FF0, 0x00); // Mode CHR 256k

        var bank_size = 0x40000;
        var banks = Math.Ceiling((double)size / bank_size);
        if (banks > 6) throw new ArgumentOutOfRangeException("CHR total size is too big");
        for (var bank = 0; bank < banks; bank++)
        {
            var read_size = Math.Min(size, bank_size);
            var outer_bank = (bank << 5) & 0xF0;
            Console.WriteLine($"Set CHR 256k outer-banks #{bank}/{banks}");
            dumper.WriteCpu(0x5FF2, (byte)outer_bank);
            DumpChrMmc3(dumper, data, read_size);
            size -= read_size;
        }
    }

    private void DumpPrgMmc3(IFamicomDumperConnection dumper, List<byte> data, int size)
    {
        var banks = size / 0x2000;
        if (banks > 256) throw new ArgumentOutOfRangeException("PRG size is too big");
        for (var bank = 0; bank < banks; bank++)
        {
            Console.Write($"Reading PRG banks #{bank}/{banks}... ");
            dumper.WriteCpu(0x8000, 0x07, (byte)bank);
            data.AddRange(dumper.ReadCpu(0xA000, 0x2000));

            Console.WriteLine("OK");
        }
        Console.WriteLine("OK");
    }

    private void DumpChrMmc3(IFamicomDumperConnection dumper, List<byte> data, int size)
    {
        var banks = size / 0x400;
        if (banks > 256) throw new ArgumentOutOfRangeException("CHR size is too big");
        for (var bank = 0; bank < banks; bank++)
        {
            Console.Write($"Reading CHR banks #{bank}/{banks}... ");
            dumper.WriteCpu(0x8000, 2, (byte)bank);
            data.AddRange(dumper.ReadPpu(0x1000, 0x0400));
            Console.WriteLine("OK");
        }
    }

    public void EnablePrgRam(IFamicomDumperConnection dumper)
    {
        dumper.WriteCpu(0xA001, 0x80);
    }

    public MirroringType GetMirroring(IFamicomDumperConnection dumper)
    {
        return MirroringType.MapperControlled;
    }
}
