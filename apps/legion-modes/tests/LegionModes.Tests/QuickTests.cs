using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using LegionModes.Core;
using LegionModes.Core.Engine;
using LegionModes.Core.Ipc;
using LegionModes.Core.Model;
using LegionModes.Core.Quick;
using LegionModes.Core.Status;

namespace LegionModes.Tests;

public sealed class QuickTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    const string Exe = @"C:\Program Files\LegionModes\LegionModes.Quick.exe";
    const string Llt = @"C:\Program Files\LenovoLegionToolkit\Lenovo Legion Toolkit.exe";

    static ProfileEngine NewEngine() => new(FactoryProfiles.Create(), new FakeClock());

    static MenuState State(bool onBattery = false, bool autonomy = false, Workload? forced = null) =>
        new(onBattery, autonomy, forced, false, false, false, false, false);

    static IEnumerable<MenuItem> Flatten(MenuItem i) => new[] { i }.Concat(i.Children.SelectMany(Flatten));

    [Fact(DisplayName = "@critical quick pipe: the owner's QuickAction is acknowledged, any other message is refused")]
    public async Task QuickPipeAdmitsOnlyQuickAction()
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var name = "LegionModes.Test." + Guid.NewGuid().ToString("N");
        // The test process is not elevated: it also needs CreateNewInstance to keep a listening instance (the plugin has it
        // as an administrator). The owner's own rule stays ReadWrite from PipeAcl.Quick.
        var acl = PipeAcl.Quick(me);
        acl.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        await using var server = new PipeServer(name,
            m => Task.FromResult<Message>(m is QuickAction ? new Ack(true) : new Ack(false, "refused")), acl);
        server.Start();

        Assert.Equal(new Ack(true), await PipeClient.SendAsync(name, new QuickAction(QuickActionKind.StayAwake), Wait));
        var refused = Assert.IsType<Ack>(await PipeClient.SendAsync(name,
            new Heartbeat(DateTimeOffset.UnixEpoch, true, false, true), Wait));
        Assert.False(refused.Ok);
    }

    [Fact(DisplayName = "@critical quick pipe ACL: only SYSTEM, Administrators and the owner, the owner without pipe creation")]
    public void QuickAclRules()
    {
        var owner = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var rules = PipeAcl.Quick(owner).GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.Equal(3, rules.Count);
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        var mine = Assert.Single(rules, r => r.IdentityReference.Equals(owner));
        Assert.True(mine.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
        Assert.False(mine.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.Contains(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.Contains(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
    }

    [Fact(DisplayName = "@important QuickActions.Apply: StayAwake toggles, a forced workload is set then cleared by Auto")]
    public void ApplyToggles()
    {
        var e = NewEngine();
        Assert.Equal(new QuickResult(true, null), QuickActions.Apply(e, QuickActionKind.StayAwake));
        Assert.True(e.IsSet(OverrideKind.StayAwake));
        QuickActions.Apply(e, QuickActionKind.StayAwake);
        Assert.False(e.IsSet(OverrideKind.StayAwake));

        QuickActions.Apply(e, QuickActionKind.WorkloadHeavy);
        Assert.Equal(Workload.Heavy, e.ForcedWorkload);
        QuickActions.Apply(e, QuickActionKind.WorkloadAuto);
        Assert.Null(e.ForcedWorkload);
    }

    [Fact(DisplayName = "@important QuickActions.Apply: autonomy is refused on AC, toggles on battery; Microphone and DGpu are the plugin's")]
    public void ApplyAutonomyAndPluginActions()
    {
        var e = NewEngine();
        e.Update(new Inputs(PowerSource.Ac, LidState.Open, Workload.Idle, false));
        var refused = QuickActions.Apply(e, QuickActionKind.Autonomy);
        Assert.Equal("Автономия доступна только от батареи", refused.Error);
        Assert.False(e.Autonomy);

        var clock = new FakeClock();
        e = new ProfileEngine(FactoryProfiles.Create(), clock);
        e.Update(new Inputs(PowerSource.Battery, LidState.Open, Workload.Idle, false));
        Assert.Equal(new QuickResult(true, null), QuickActions.Apply(e, QuickActionKind.Autonomy));
        Assert.True(e.Autonomy);
        QuickActions.Apply(e, QuickActionKind.Autonomy);
        Assert.False(e.Autonomy);

        Assert.False(QuickActions.Apply(e, QuickActionKind.Microphone).Handled);
        Assert.False(QuickActions.Apply(e, QuickActionKind.DGpu).Handled);
    }

    [Fact(DisplayName = "@important DesktopMenu: Автономия only on battery, ticks follow the state")]
    public void MenuLabels()
    {
        Assert.DoesNotContain(Flatten(DesktopMenu.Build(State())), i => i.Label.Contains("Автономия"));
        Assert.Contains(Flatten(DesktopMenu.Build(State(onBattery: true, autonomy: true))), i => i.Label == "✓ Автономия");

        var heavy = Flatten(DesktopMenu.Build(State(forced: Workload.Heavy))).ToList();
        Assert.Contains(heavy, i => i.Label == "✓ Тяжёлая");
        Assert.Contains(heavy, i => i.Label == "Авто");
        Assert.Contains(Flatten(DesktopMenu.Build(State())), i => i.Label == "✓ Авто");

        var muted = DesktopMenu.Build(State() with { MicrophoneMuted = true });
        Assert.Contains(Flatten(muted), i => i.Label == "✓ Микрофон выключен");
    }

    [Fact(DisplayName = "@important MenuRegistry.Values: root verb, icon and cascade; Не спать command; NN order; Arg/Parse round-trip")]
    public void RegistryValues()
    {
        var values = MenuRegistry.Values(DesktopMenu.Build(State()), Exe, Llt);
        string? Get(string sub, string name) => values.SingleOrDefault(v => v.SubKey == sub && v.Name == name)?.Value;

        Assert.Equal("Расширенные режимы", Get("", "MUIVerb"));
        Assert.Equal(Llt, Get("", "Icon"));
        Assert.Equal("", Get("", "SubCommands"));
        Assert.Equal("Нагрузка", Get(@"shell\01-workload", "MUIVerb"));
        Assert.Equal("", Get(@"shell\01-workload", "SubCommands"));
        Assert.Equal($"\"{Exe}\" workload-heavy", Get(@"shell\01-workload\shell\02-workload-heavy\command", ""));
        Assert.Equal("Не спать", Get(@"shell\02-stay-awake", "MUIVerb"));
        Assert.Equal($"\"{Exe}\" stay-awake", Get(@"shell\02-stay-awake\command", ""));

        var order = values.Where(v => v.Name == "MUIVerb" && v.SubKey.Count(c => c == '\\') == 1).Select(v => v.SubKey).ToList();
        Assert.Equal(order.OrderBy(s => s, StringComparer.Ordinal), order);
        Assert.Equal(7, order.Count);

        foreach (var k in Enum.GetValues<QuickActionKind>())
            Assert.Equal(k, QuickActions.Parse(QuickActions.Arg(k)));
        Assert.Null(QuickActions.Parse("nope"));
    }

    [Fact(DisplayName = "@critical ShutdownSafety.OnStart: counts abnormal runs, resets Curve Optimizer at 3, a clean run zeroes the count")]
    public void Shutdowns()
    {
        Assert.Equal((1, false), ShutdownSafety.OnStart(0, false));
        Assert.Equal((0, true), ShutdownSafety.OnStart(2, false));
        Assert.Equal((0, false), ShutdownSafety.OnStart(2, true));
    }

    [Fact(DisplayName = "@important ControlFile.Load: Task 11's control.json keeps its values, the new fields default")]
    public void ControlFileOldFormat()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lm-quick-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "control.json");
        try
        {
            var scheme = Guid.NewGuid();
            File.WriteAllText(path, $$"""
                { "hasControl": true, "savedLidActionAc": 1, "savedLidActionDc": 2, "savedAutomationEnabled": true,
                  "savedPowerModeMappingMode": "Enabled", "scheme": "{{scheme}}" }
                """);
            var d = ControlFile.Load(path);
            Assert.True(d.HasControl);
            Assert.Equal(1u, d.SavedLidActionAc);
            Assert.Equal(2u, d.SavedLidActionDc);
            Assert.True(d.SavedAutomationEnabled);
            Assert.Equal("Enabled", d.SavedPowerModeMappingMode);
            Assert.Equal(scheme, d.Scheme);
            Assert.False(d.Autonomy);
            Assert.Null(d.CameraOff);
            Assert.Null(d.CameraAt);
            Assert.Equal(0, d.AbnormalShutdowns);
            Assert.False(d.Running);
            Assert.Empty(d.WakeOverrides!);

            ControlFile.Save(path, d with { AbnormalShutdowns = 2, Running = true, WakeOverrides = ["a"] });
            var back = ControlFile.Load(path);
            Assert.Equal(2, back.AbnormalShutdowns);
            Assert.True(back.Running);
            Assert.Equal(["a"], back.WakeOverrides);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(dir);
        }
    }
}
