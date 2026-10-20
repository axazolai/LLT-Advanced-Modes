using FakeHost;

namespace FakeClientStable;

// Uses only members that stay unchanged in V2.
public static class Client
{
    public static void Use()
    {
        var w = new Widget(1);
        w.Count = 2;
        _ = w.Name;
        _ = w.Echo<int>(3);
        new Box<int>().Put(1);
        _ = Stable.Twice(2);
    }
}
