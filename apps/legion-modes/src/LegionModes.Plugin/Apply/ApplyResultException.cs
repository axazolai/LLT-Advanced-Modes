namespace LegionModes.Plugin.Apply;

// A result the system gave on purpose (a refused write, a value that did not read back). The applier reports it as an error
// result; any other exception from an apply step is a fault (a missing member, a null) and fails the self-test.
internal sealed class ApplyResultException(string message) : InvalidOperationException(message);
