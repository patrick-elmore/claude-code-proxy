namespace ClaudeCodeProxy;

public enum PromptFailureMode
{
    PromptNotConfigured,
    PromptFileNotFound,
    PromptFileReadError,
    NoBulkBlock,
    NoBoundaryMarker,
    IdentityNotRecognized,
    UnhandledException,
    UnknownPath,
}

public readonly record struct RewriteOutcome
{
    public bool Mutated { get; init; }

    // Non-null when mutation did not complete due to a tracked failure.
    public PromptFailureMode? FailureMode { get; init; }

    // Orthogonal warning — can be true even when Mutated is true.
    public bool IdentityNotRecognized { get; init; }

    // Request was not a message-shaped call — rewriting was never applicable.
    public static readonly RewriteOutcome NotApplicable = new();

    public static RewriteOutcome Success(bool identityNotRecognized = false) =>
        new() { Mutated = true, IdentityNotRecognized = identityNotRecognized };

    public static RewriteOutcome Failure(PromptFailureMode mode, bool identityNotRecognized = false) =>
        new() { FailureMode = mode, IdentityNotRecognized = identityNotRecognized };
}
