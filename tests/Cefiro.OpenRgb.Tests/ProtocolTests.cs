using System.Buffers.Binary;

namespace Cefiro.OpenRgb.Tests;

public class ProtocolTests
{
    private static OpenRgbDevice SampleDevice() => new()
    {
        Id = 0,
        Type = DeviceType.Motherboard,
        Name = "MSI X670E GAMING PLUS WIFI (MS-7E16)",
        Vendor = "MSI",
        Description = "MSI Mystic Light Device (185-byte)",
        Version = "1.0",
        Serial = "ABC",
        Location = "HID: \\\\?\\HID#VID_0DB0&PID_0076",
        DisplayName = "Placa base",
        ActiveMode = 1,
        Modes =
        [
            new OpenRgbMode { Index = 0, Name = "Off", Value = 0, Flags = ModeFlags.None },
            new OpenRgbMode
            {
                Index = 1, Name = "Direct", Value = 31, Flags = ModeFlags.HasPerLedColor,
                ColorMode = ModeColorMode.PerLed,
            },
            new OpenRgbMode
            {
                Index = 2, Name = "Breathing", Value = 2,
                Flags = ModeFlags.HasSpeed | ModeFlags.HasBrightness | ModeFlags.HasModeSpecificColor,
                SpeedMin = 0, SpeedMax = 2, Speed = 1, BrightnessMin = 0, BrightnessMax = 10, Brightness = 10,
                ColorsMin = 1, ColorsMax = 1, ColorMode = ModeColorMode.ModeSpecific,
                Colors = [new OpenRgbColor(255, 0, 128)],
            },
        ],
        Zones =
        [
            new OpenRgbZone
            {
                Index = 0, Name = "JARGB_V2_1", Type = ZoneType.Linear, LedsMin = 0, LedsMax = 240, LedsCount = 3,
                Flags = ZoneFlags.ConfigurableSize, ActiveMode = -1, DisplayName = "Radiador",
                Segments =
                [
                    new OpenRgbSegment { Name = "Bomba", Type = ZoneType.Linear, StartIndex = 0, LedsCount = 1 },
                    new OpenRgbSegment { Name = "Ventiladores", Type = ZoneType.Linear, StartIndex = 1, LedsCount = 2 },
                ],
            },
            new OpenRgbZone
            {
                Index = 1, Name = "Matriz", Type = ZoneType.Matrix, LedsMin = 2, LedsMax = 2, LedsCount = 2,
                Matrix = new MatrixMap { Height = 1, Width = 3, Map = [0, MatrixMap.NoLed, 1] },
                Modes = [new OpenRgbMode { Index = 0, Name = "Static", Flags = ModeFlags.HasModeSpecificColor }],
                ActiveMode = 0,
            },
        ],
        Leds =
        [
            new OpenRgbLed { Name = "LED 1", Value = 0 },
            new OpenRgbLed { Name = "LED 2", Value = 1 },
            new OpenRgbLed { Name = "LED 3", Value = 2 },
            new OpenRgbLed { Name = "LED 4", Value = 3 },
            new OpenRgbLed { Name = "LED 5", Value = 4 },
        ],
        Colors =
        [
            new OpenRgbColor(255, 0, 0), new OpenRgbColor(0, 255, 0), new OpenRgbColor(0, 0, 255),
            new OpenRgbColor(255, 255, 255), new OpenRgbColor(1, 2, 3),
        ],
        LedDisplayNames = ["Uno", "Dos"],
        Flags = ControllerFlags.Local,
        Configuration = "{\"a\":1}",
    };

    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(6u)]
    public void Parse_reads_every_field_for_each_protocol(uint protocol)
    {
        var source = SampleDevice();
        var bytes = new ReferenceSerializer(protocol).SerializeControllerDataReply(source);

        var parsed = DeviceDataParser.Parse(bytes, protocol, 42);

        Assert.Equal(42u, parsed.Id);
        Assert.Equal(source.Type, parsed.Type);
        Assert.Equal(source.Name, parsed.Name);
        Assert.Equal(source.Vendor, parsed.Vendor);
        Assert.Equal(source.Location, parsed.Location);
        Assert.Equal(source.ActiveMode, parsed.ActiveMode);
        Assert.Equal(3, parsed.Modes.Count);
        Assert.Equal("Breathing", parsed.Modes[2].Name);
        Assert.Equal(new OpenRgbColor(255, 0, 128), parsed.Modes[2].Colors[0]);
        Assert.Equal(protocol >= 3 ? 10u : 0u, parsed.Modes[2].BrightnessMax);
        Assert.Equal(protocol < 6 ? 31 : 0, parsed.Modes[1].Value);

        Assert.Equal(2, parsed.Zones.Count);
        Assert.Equal(3u, parsed.Zones[0].LedsCount);
        Assert.True(parsed.Zones[0].IsResizable);
        Assert.Equal(0, parsed.Zones[0].StartLedIndex);
        Assert.Equal(3, parsed.Zones[1].StartLedIndex);
        Assert.NotNull(parsed.Zones[1].Matrix);
        Assert.Equal(MatrixMap.NoLed, parsed.Zones[1].Matrix!.Map[1]);
        Assert.Equal(protocol >= 4 ? 2 : 0, parsed.Zones[0].Segments.Count);
        Assert.Equal(protocol >= 5 ? ZoneFlags.ConfigurableSize : ZoneFlags.None, parsed.Zones[0].Flags);
        Assert.Equal(protocol >= 6 ? "Radiador" : "", parsed.Zones[0].DisplayName);
        Assert.Equal(protocol >= 6 ? 1 : 0, parsed.Zones[1].Modes.Count);

        Assert.Equal(5, parsed.Leds.Count);
        Assert.Equal(source.Colors, parsed.Colors);
        Assert.Equal(protocol >= 5 ? 2 : 0, parsed.LedDisplayNames.Count);
        Assert.Equal(protocol >= 5 ? ControllerFlags.Local : ControllerFlags.None, parsed.Flags);
        Assert.Equal(protocol >= 6 ? "Placa base" : "", parsed.DisplayName);
        Assert.Equal(protocol >= 6 ? "{\"a\":1}" : "", parsed.Configuration);
        Assert.Equal(1, parsed.FindDirectModeIndex());
    }

    [Fact]
    public void Parse_rejects_truncated_data()
    {
        var bytes = new ReferenceSerializer(6).SerializeControllerDataReply(SampleDevice());
        var truncated = bytes.AsSpan(0, bytes.Length - 10).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(truncated, (uint)truncated.Length);

        Assert.Throws<OpenRgbProtocolException>(() => DeviceDataParser.Parse(truncated, 6, 0));
    }

    [Fact]
    public void Header_round_trips_and_validates_magic()
    {
        var buffer = new byte[16];
        new PacketHeader(7, PacketId.UpdateLeds, 1234).WriteTo(buffer);

        Assert.Equal("ORGB"u8.ToArray(), buffer[..4]);
        Assert.Equal(new PacketHeader(7, PacketId.UpdateLeds, 1234), PacketHeader.Read(buffer));

        buffer[0] = (byte)'X';
        Assert.Throws<OpenRgbProtocolException>(() => PacketHeader.Read(buffer));
    }

    [Fact]
    public void Color_uses_0x00BBGGRR_on_the_wire()
    {
        var color = new OpenRgbColor(0x11, 0x22, 0x33);
        Assert.Equal(0x00332211u, color.ToWire());
        Assert.Equal(color, OpenRgbColor.FromWire(0x00332211u));
    }

    [Fact]
    public void UpdateLeds_payload_has_self_inclusive_data_size()
    {
        OpenRgbColor[] colors = [new(1, 2, 3), new(4, 5, 6)];
        var payload = Payloads.UpdateLeds(colors);

        Assert.Equal(4 + 2 + 2 * 4, payload.Length);
        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4)));
        Assert.Equal(new OpenRgbColor(4, 5, 6).ToWire(), BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(10)));
    }

    [Fact]
    public void UpdateZoneLeds_payload_layout()
    {
        var payload = Payloads.UpdateZoneLeds(3, [new OpenRgbColor(9, 9, 9)]);

        Assert.Equal(4 + 4 + 2 + 4, payload.Length);
        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8)));
    }

    [Theory]
    [InlineData(5u)]
    [InlineData(6u)]
    public void UpdateMode_payload_is_readable_as_mode_data(uint protocol)
    {
        var mode = SampleDevice().Modes[2];
        var payload = Payloads.UpdateMode(mode.Index, mode, protocol);

        Assert.Equal((uint)payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4)));

        var reader = new WireReader(payload.AsSpan(8));
        var back = DeviceDataParser.ReadMode(ref reader, protocol, 2);
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(mode.Name, back.Name);
        Assert.Equal(mode.Speed, back.Speed);
        Assert.Equal(mode.Brightness, back.Brightness);
        Assert.Equal(mode.Colors, back.Colors);
        Assert.Equal(protocol < 6 ? mode.Value : 0, back.Value);
    }

    [Fact]
    public void Diagonal_direction_is_downgraded_below_protocol_6()
    {
        var mode = new OpenRgbMode
        {
            Index = 0, Name = "Wave", Flags = ModeFlags.HasDirectionDiagonal, Direction = ModeDirection.DownRight,
        };

        var payload = Payloads.UpdateMode(0, mode, 5);
        var reader = new WireReader(payload.AsSpan(8));
        var back = DeviceDataParser.ReadMode(ref reader, 5, 0);

        Assert.Equal(ModeDirection.Left, back.Direction);
        Assert.False(back.Flags.HasFlag(ModeFlags.HasDirectionDiagonal));
    }
}
