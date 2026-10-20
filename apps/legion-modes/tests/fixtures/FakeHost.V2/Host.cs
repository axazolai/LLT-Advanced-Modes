namespace FakeHost;

public class Widget
{
    public int Count;
    public string Name { get; set; } = "";
    public event EventHandler? Changed;
    private int _state2;                 // renamed from _state
    private int _label = 1;              // was string

    public Widget(int seed) { _state2 = seed; }

    // Removable() removed
    public int AddsParam(int a, int b) => a + b;   // gained a parameter
    public long ChangesReturn() => 0;              // return type changed
    public T Echo<T>(T value) => value;
    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    public int StateValue() => _state2 + _label;

    public int Shared;                        // was static
    internal int Hidden() => 0;               // was public
    public int UseIn(in int x) => x;
    public int ToValue(int x) => x;           // was in
    public string Tag { get; init; } = "";    // was settable

    public class Inner { public void Ping() { } }
}

public interface IProvider
{
    void Initialize();
    void Extra();                             // added: implementers must follow
}

public abstract class BaseThing
{
    public abstract int Compute(int x, int y);   // signature changed
    public virtual string Describe(int level) => "";   // virtual changed: overrides of Describe() lose their base
    protected int Helper() => 1;
}

public class ProviderBase : IProvider
{
    public virtual void Initialize() { }
    public virtual void Extra() { }           // the base implements the new member itself
}

public class Level1 { public virtual int Tune() => 0; }
public class Level2 : Level1 { public sealed override int Tune() => 1; }

public class Box<T>
{
    public void Put(T item) { }
}

public static class Stable
{
    public static int Twice(int x) => x * 2;
    public static string Unused(string x, int y) => x;   // unused member changed
    public static int Added() => 1;                       // member added
}
