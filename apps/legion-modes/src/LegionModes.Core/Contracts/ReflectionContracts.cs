namespace LegionModes.Core.Contracts;

public enum MemberKind { Field, Property, Method, Type }

public sealed record ReflectionContract(string Assembly, string TypeFullName, string Member, MemberKind Kind, string? MemberTypeFullName);

public static class ReflectionContracts
{
    public static readonly IReadOnlyList<ReflectionContract> All =
    [
        new("Lenovo Legion Toolkit", "LenovoLegionToolkit.WPF.Windows.MainWindow", "_trayHelper", MemberKind.Field, "LenovoLegionToolkit.WPF.Utils.TrayHelper"),
        new("Lenovo Legion Toolkit", "LenovoLegionToolkit.WPF.Utils.TrayHelper", "_contextMenu", MemberKind.Field, "System.Windows.Controls.ContextMenu"),
        new("Lenovo Legion Toolkit", "LenovoLegionToolkit.WPF.Pages.DashboardPage", "_panel", MemberKind.Field, "System.Windows.Controls.StackPanel"),
        new("Lenovo Legion Toolkit", "LenovoLegionToolkit.WPF.Windows.MainWindow", "_rootFrame", MemberKind.Field, "System.Windows.Controls.Frame"),
        new("Lenovo Legion Toolkit", "LenovoLegionToolkit.WPF.Windows.MainWindow", "_navigationStore", MemberKind.Field, "Wpf.Ui.Controls.NavigationStore"),
        // LltTranslations replaces these to correct LLT's Russian texts.
        new("Lenovo Legion Toolkit", "LenovoLegionToolkit.WPF.Resources.Resource", "resourceMan", MemberKind.Field, "System.Resources.ResourceManager"),
        new("LenovoLegionToolkit.Lib", "LenovoLegionToolkit.Lib.Resources.Resource", "resourceMan", MemberKind.Field, "System.Resources.ResourceManager"),
        new("LenovoLegionToolkit.Lib.Automation", "LenovoLegionToolkit.Lib.Automation.Resources.Resource", "resourceMan", MemberKind.Field, "System.Resources.ResourceManager"),
    ];
}
