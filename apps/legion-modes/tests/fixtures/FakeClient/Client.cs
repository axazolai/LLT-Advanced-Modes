using FakeHost;

namespace FakeClient;

public static class Client
{
    public static void Use()
    {
        var w = new Widget(1);              // constructor
        w.Count = 2;                        // field
        w.Name = "x";                       // property
        _ = w.Name;
        w.Removable();                      // method (removed in V2)
        _ = w.AddsParam(1);                 // method (gains a parameter in V2)
        _ = w.ChangesReturn();              // method (changes return type in V2)
        _ = w.Echo<int>(3);                 // generic method
        w.Changed += (_, _) => { };         // event
        new Widget.Inner().Ping();          // nested type
        new Box<int>().Put(1);              // member of a generic instantiation
        _ = Stable.Twice(2);
        _ = w.Hidden();                     // public in V1, internal in V2
        w.Tag = "t";                        // setter becomes init-only in V2
        Widget.Shared = 1;                  // static field (instance in V2)
        var v = 1;
        _ = w.UseIn(in v);                  // unchanged in parameter
        _ = w.ToValue(in v);                // in becomes by-value in V2
    }
}

public abstract class PBase : IProvider
{
    public abstract void Initialize();
}

public sealed class PConcrete : PBase
{
    public override void Initialize() { }
}

public sealed class FromHost : ProviderBase, IProvider { }

public sealed class Tuned : Level2
{
    public override int Tune() => 2;
}

public sealed class Impl : IProvider
{
    public void Initialize() { }
}

public sealed class Derived : BaseThing
{
    public override int Compute(int x) => Helper() + Gone() + x;   // protected: Helper stays, Gone is removed in V2
    public override string Describe() => "d";
}
