namespace LegionModes.Core.Model;

// Pipeline names written before the names became English translation keys (a stored name is shown through Tr.T).
public static class PipelineNames
{
    static readonly Dictionary<string, string> Legacy = new()
    {
        ["При входе"] = "On enter",
        ["При выходе"] = "On leave",
        ["Перезапуск ollama serve"] = "Restart ollama serve",
    };

    public static string Canonical(string stored) => Legacy.TryGetValue(stored, out var key) ? key : stored;
}
