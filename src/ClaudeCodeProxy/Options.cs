namespace ClaudeCodeProxy;

public sealed class ProxyOptions
{
    public string PromptFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Variables substituted into the prompt file at request time.
    /// Each "{{key}}" in the prompt is replaced with the corresponding value.
    /// </summary>
    public Dictionary<string, string> Variables { get; set; } = new();
}

public sealed class DumpOptions
{
    public bool Enabled { get; set; } = false;
    public int MaxRequests { get; set; } = 5;
    public string OutputPath { get; set; } = "./dumps";
}
