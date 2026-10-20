namespace FakeHost;

public class Widget
{
    public int Count;
    public string Name { get; set; } = "";
    public event EventHandler? Changed;
    private int _state;
    private string _label = "";

    public Widget(int seed) { _state = seed; }

    public void Removable() { }
    public int AddsParam(int a) => a;
    public int ChangesReturn() => 0;
    public T Echo<T>(T value) => value;
    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    public int StateValue() => _state + _label.Length;

    public static int Shared;                 // static in V1, instance in V2
    public int Hidden() => 0;                 // internal in V2
    public int UseIn(in int x) => x;          // unchanged in V2
    public int ToValue(in int x) => x;        // by value in V2
    public string Tag { get; set; } = "";     // init-only in V2

    public class Inner { public void Ping() { } }
}

public interface IProvider
{
    void Initialize();
}

public abstract class BaseThing
{
    public abstract int Compute(int x);
    public virtual string Describe() => "";
    protected int Helper() => 1;
    protected int Gone() => 2;                 // removed in V2
}

public class ProviderBase : IProvider
{
    public virtual void Initialize() { }
}

public class Level1 { public virtual int Tune() => 0; }
public class Level2 : Level1 { public override int Tune() => 1; }   // sealed in V2

public class Box<T>
{
    public void Put(T item) { }
}

public static class Stable
{
    public static int Twice(int x) => x * 2;
    public static int Unused(int x) => x;
}
