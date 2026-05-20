using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ClaudeCodeProxy.Tests;

public sealed class RequestDumperTests : IDisposable
{
    private readonly string _outputDir;

    public RequestDumperTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose() => Directory.Delete(_outputDir, recursive: true);

    private RequestDumper Build(bool failureDumpEnabled) =>
        new(new FakeOptionsMonitor<DumpOptions>(new DumpOptions
        {
            FailureDumpEnabled = failureDumpEnabled,
            OutputPath = _outputDir,
        }), NullLogger<RequestDumper>.Instance);

    [Fact]
    public async Task DumpFailureAsync_Disabled_WritesNoFiles()
    {
        var dumper = Build(failureDumpEnabled: false);
        var bytes = "payload"u8.ToArray();

        await dumper.DumpFailureAsync("NoBulkBlock", bytes, "/test/path", CancellationToken.None);

        Directory.GetFiles(_outputDir).ShouldBeEmpty();
    }

    [Fact]
    public async Task DumpFailureAsync_Enabled_WritesPayloadAndPathFiles()
    {
        var dumper = Build(failureDumpEnabled: true);
        var bytes = "payload"u8.ToArray();

        await dumper.DumpFailureAsync("NoBulkBlock", bytes, "/test/path", CancellationToken.None);

        var files = Directory.GetFiles(_outputDir);
        files.ShouldContain(f => Path.GetFileName(f).EndsWith("-failure-NoBulkBlock.json"));
        files.ShouldContain(f => Path.GetFileName(f).EndsWith("-path.txt"));
    }

    [Fact]
    public async Task DumpFailureAsync_ExceedsCap_StopsWriting()
    {
        var dumper = Build(failureDumpEnabled: true);
        // Each chunk is 10MB; six calls exceed the 50MB cap.
        var chunk = new byte[10 * 1024 * 1024];

        for (var i = 0; i < 6; i++)
            await dumper.DumpFailureAsync("NoBulkBlock", chunk, null, CancellationToken.None);

        // Only 5 calls worth of bytes fit within the 50MB cap.
        var jsonFiles = Directory.GetFiles(_outputDir, "*-failure-NoBulkBlock.json");
        jsonFiles.Length.ShouldBe(5);
    }

    [Fact]
    public async Task DumpFailureAsync_DayRollover_ResetsCounter()
    {
        var dumper = Build(failureDumpEnabled: true);
        var chunk = new byte[10 * 1024 * 1024];

        // Fill cap
        for (var i = 0; i < 5; i++)
            await dumper.DumpFailureAsync("NoBulkBlock", chunk, null, CancellationToken.None);

        var filesBefore = Directory.GetFiles(_outputDir, "*-failure-NoBulkBlock.json").Length;
        filesBefore.ShouldBe(5);

        // Simulate day rollover
        dumper._failureBytesDate = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);

        await dumper.DumpFailureAsync("NoBulkBlock", chunk, null, CancellationToken.None);

        var filesAfter = Directory.GetFiles(_outputDir, "*-failure-NoBulkBlock.json").Length;
        filesAfter.ShouldBe(6);
    }
}
