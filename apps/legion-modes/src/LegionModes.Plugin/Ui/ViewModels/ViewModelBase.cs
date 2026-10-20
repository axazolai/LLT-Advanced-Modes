using System.ComponentModel;
using System.Runtime.CompilerServices;
using LegionModes.Core;
using LegionModes.Core.Model;

namespace LegionModes.Plugin.Ui.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// A value with the Russian label a ComboBox shows (DisplayMemberPath=Label, SelectedValuePath=Value).
public sealed record Option<T>(T Value, string Label);

public static class Options
{
    public static IReadOnlyList<Option<GpuMode>> Gpu =>
    [
        new(GpuMode.Hybrid, Tr.T("Гибридный")), new(GpuMode.HybridIgpuOnly, Tr.T("Только встроенная")), new(GpuMode.HybridAuto, Tr.T("Авто")),
    ];
    public static IReadOnlyList<Option<ChargingMode>> Charging =>
    [
        new(ChargingMode.DoNotTouch, Tr.T("Не трогать")), new(ChargingMode.Conservation, Tr.T("Сбережение")),
        new(ChargingMode.Normal, Tr.T("Обычная")), new(ChargingMode.Rapid, Tr.T("Быстрая")),
    ];
    public static IReadOnlyList<Option<UsbPower>> Usb =>
    [
        new(UsbPower.Off, Tr.T("Выключено")), new(UsbPower.WhenSleeping, Tr.T("Во сне")), new(UsbPower.Always, Tr.T("Всегда")),
    ];
    public static IReadOnlyList<Option<Projection>> Projection =>
    [
        new(LegionModes.Core.Model.Projection.DoNotTouch, Tr.T("Не трогать")), new(LegionModes.Core.Model.Projection.Extend, Tr.T("Расширить")),
        new(LegionModes.Core.Model.Projection.ExternalOnly, Tr.T("Только внешний")), new(LegionModes.Core.Model.Projection.Duplicate, Tr.T("Дублировать")),
        new(LegionModes.Core.Model.Projection.InternalOnly, Tr.T("Только встроенный")),
    ];
    public static IReadOnlyList<Option<ExternalRateKind>> ExternalRate =>
    [
        new(ExternalRateKind.DoNotTouch, Tr.T("Не трогать")), new(ExternalRateKind.Maximum, Tr.T("Максимальная")),
        new(ExternalRateKind.Fixed, Tr.T("Фиксированная")),
    ];
    public static IReadOnlyList<int> PanelHz { get; } = FirmwareRanges.PanelHz;
    public static IReadOnlyList<int> LightingProfiles { get; } = [1, 2, 3, 4, 5, 6];
    public static IReadOnlyList<int> Brightness { get; } = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
}
