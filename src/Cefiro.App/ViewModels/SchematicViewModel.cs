using System.Windows.Media;
using Cefiro.App.Infrastructure;
using Cefiro.Ipc.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cefiro.App.ViewModels;

/// <summary>Estado del esquema de la caja: RPM de cada grupo de ventiladores y color de los LEDs.</summary>
public sealed partial class SchematicViewModel : ObservableObject
{
    private static readonly IReadOnlyList<Color> Dark8 = Enumerable.Repeat(Color.FromArgb(0x40, 0x9A, 0xA6, 0xC0), 8).ToArray();
    private static readonly IReadOnlyList<Color> Dark10 = Enumerable.Repeat(Color.FromArgb(0x40, 0x9A, 0xA6, 0xC0), 10).ToArray();

    [ObservableProperty] public partial double CaseRpm { get; set; }
    [ObservableProperty] public partial double RadiatorRpm { get; set; }
    [ObservableProperty] public partial double PumpRpm { get; set; }
    [ObservableProperty] public partial double GpuRpm { get; set; }
    [ObservableProperty] public partial IReadOnlyList<Color> CaseLeds { get; set; } = Dark8;
    [ObservableProperty] public partial IReadOnlyList<Color> AioLeds { get; set; } = Dark8;
    [ObservableProperty] public partial IReadOnlyList<Color> Ram1Leds { get; set; } = Dark10;
    [ObservableProperty] public partial IReadOnlyList<Color> Ram2Leds { get; set; } = Dark10;
    [ObservableProperty] public partial bool RadiatorOnTop { get; set; } = true;
    [ObservableProperty] public partial int FrontFans { get; set; } = 3;
    [ObservableProperty] public partial int RearFans { get; set; } = 1;
    [ObservableProperty] public partial int BottomFans { get; set; }
    [ObservableProperty] public partial string CaseRpmText { get; set; } = "—";
    [ObservableProperty] public partial string AioRpmText { get; set; } = "—";
    [ObservableProperty] public partial string PumpRpmText { get; set; } = "—";
    [ObservableProperty] public partial string GpuName { get; set; } = "GPU";

    // Lo que se ve en el techo y en el frontal depende de dónde va la líquida.
    [ObservableProperty] public partial double TopRpm { get; set; }
    [ObservableProperty] public partial IReadOnlyList<Color> TopLeds { get; set; } = Dark8;
    [ObservableProperty] public partial string TopLabel { get; set; } = "LÍQUIDA";
    [ObservableProperty] public partial string TopRpmText { get; set; } = "—";
    [ObservableProperty] public partial int TopCount { get; set; } = 3;
    [ObservableProperty] public partial double FrontRpm { get; set; }
    [ObservableProperty] public partial IReadOnlyList<Color> FrontLeds { get; set; } = Dark8;
    [ObservableProperty] public partial string FrontLabel { get; set; } = "FRONTAL";
    [ObservableProperty] public partial string FrontRpmText { get; set; } = "—";
    [ObservableProperty] public partial int FrontCount { get; set; } = 3;

    public void ApplyLayout(CaseLayout layout)
    {
        RadiatorOnTop = layout.RadiatorPosition != "Front";
        FrontFans = Math.Clamp(layout.FrontFans, 0, 3);
        RearFans = Math.Clamp(layout.RearFans, 0, 1);
        BottomFans = Math.Clamp(layout.BottomFans, 0, 2);
        Recompute();
    }

    private void Recompute()
    {
        if (RadiatorOnTop)
        {
            (TopRpm, TopLeds, TopLabel, TopRpmText, TopCount) = (RadiatorRpm, AioLeds, "LÍQUIDA", AioRpmText, 3);
            (FrontRpm, FrontLeds, FrontLabel, FrontRpmText, FrontCount) = (CaseRpm, CaseLeds, "FRONTAL", CaseRpmText, FrontFans);
        }
        else
        {
            (TopRpm, TopLeds, TopLabel, TopRpmText, TopCount) = (CaseRpm, CaseLeds, "TECHO", CaseRpmText, FrontFans);
            (FrontRpm, FrontLeds, FrontLabel, FrontRpmText, FrontCount) = (RadiatorRpm, AioLeds, "LÍQUIDA", AioRpmText, 3);
        }
    }

    public void UpdateFans(CefiroSession session)
    {
        if (session.Catalog is not { } catalog || session.FanFrame is not { } frame)
            return;

        float? RpmOf(FanRole role, bool spinningOnly = false)
        {
            foreach (var ch in catalog.FanChannels.Where(c => EffectiveRole(session, c) == role))
            {
                var state = frame.Channels.FirstOrDefault(s => s.ChannelId == ch.Id);
                if (state?.Rpm is { } rpm && (!spinningOnly || rpm > 0))
                    return rpm;
            }

            return null;
        }

        var pump = RpmOf(FanRole.Pump, spinningOnly: true) ?? RpmOf(FanRole.Pump);
        var radiator = RpmOf(FanRole.Radiator, spinningOnly: true) ?? RpmOf(FanRole.Radiator);
        var caseFans = RpmOf(FanRole.Case, spinningOnly: true);
        var gpu = RpmOf(FanRole.Gpu, spinningOnly: true) ?? RpmOf(FanRole.Gpu);

        PumpRpm = pump ?? 0;
        RadiatorRpm = radiator ?? 0;
        CaseRpm = caseFans ?? 0;
        GpuRpm = gpu ?? 0;
        PumpRpmText = Format.Rpm(pump);
        AioRpmText = Format.Rpm(radiator);
        CaseRpmText = Format.Rpm(caseFans);
        GpuName = catalog.Backend.Gpu ?? "GPU";
        Recompute();
    }

    private static FanRole EffectiveRole(CefiroSession session, FanChannelDto channel) =>
        session.FanSettings?.Channels.FirstOrDefault(c => c.ChannelId == channel.Id)?.Role ?? channel.DetectedRole;

    public void UpdateLeds(CefiroSession session, CaseLayout layout)
    {
        if (session.Lighting is not { } lighting || session.LightingFrame is not { } frame)
            return;

        var board = lighting.Devices.FirstOrDefault(d => d.Kind == "Motherboard");
        if (board is not null && frame.Devices.FirstOrDefault(f => f.DeviceKey == board.Key) is { } boardFrame)
        {
            var colors = Decode(boardFrame.Colors);
            CaseLeds = ZoneColors(board, colors, layout.CaseLightZone) ?? CaseLeds;
            AioLeds = ZoneColors(board, colors, layout.AioLightZone) ?? AioLeds;
        }

        var rams = lighting.Devices.Where(d => d.Kind == "Dram").OrderBy(d => d.Location, StringComparer.Ordinal).ToList();
        if (rams.Count > 0 && frame.Devices.FirstOrDefault(f => f.DeviceKey == rams[0].Key) is { } r1)
            Ram1Leds = Decode(r1.Colors);
        if (rams.Count > 1 && frame.Devices.FirstOrDefault(f => f.DeviceKey == rams[1].Key) is { } r2)
            Ram2Leds = Decode(r2.Colors);

        Recompute();
    }

    private static IReadOnlyList<Color>? ZoneColors(LightingDeviceDto device, IReadOnlyList<Color> colors, string zoneName)
    {
        var zone = device.Zones.FirstOrDefault(z => z.Name.Equals(zoneName, StringComparison.OrdinalIgnoreCase));
        if (zone is null || zone.LedCount <= 0 || zone.StartLed + zone.LedCount > colors.Count)
            return null;

        // El anillo de un ventilador tiene 8 LEDs: si la zona es más larga, se toman los 8 primeros
        // (en tu PC los ventiladores van en paralelo y todos repiten esos 8).
        return colors.Skip(zone.StartLed).Take(Math.Min(8, zone.LedCount)).ToArray();
    }

    public static IReadOnlyList<Color> Decode(string hex)
    {
        var count = hex.Length / 6;
        var result = new Color[count];
        for (var i = 0; i < count; i++)
        {
            var span = hex.AsSpan(i * 6, 6);
            result[i] = Color.FromRgb(
                byte.Parse(span[..2], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(span[2..4], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(span[4..6], System.Globalization.NumberStyles.HexNumber));
        }

        return result;
    }
}
