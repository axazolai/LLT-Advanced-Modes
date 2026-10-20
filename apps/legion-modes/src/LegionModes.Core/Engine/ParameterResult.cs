namespace LegionModes.Core.Engine;

// Ok: applied and read back. Note: applied with a stated difference (the DRR fallback); not an error and not retried.
// Error: a result text (read-back mismatch, refusal, timeout); retried. Fault: the step threw; retried, and the self-test fails.
public enum ResultKind { Ok, Note, Error, Fault }

public sealed record ParameterResult(ResultKind Kind, string? Text)
{
    public static ParameterResult Ok { get; } = new(ResultKind.Ok, null);
    public static ParameterResult Of(string? error) => error is null ? Ok : new(ResultKind.Error, error);
    public static ParameterResult Note(string text) => new(ResultKind.Note, text);
    public static ParameterResult Fault(string text) => new(ResultKind.Fault, text);

    // Counts toward a retry.
    public bool Failed => Kind is ResultKind.Error or ResultKind.Fault;
}
