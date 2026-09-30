using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Android.App;
using Android.Content;

namespace JunimoGate.Android;

/// <summary>Persists bounded Android process-exit records for inclusion in user-exported diagnostics.</summary>
public static class AndroidProcessExitDiagnostics
{
    public const string ReportFileName = "process-exits.jsonl";
    public const string TraceFileName = "process-exit-traces.txt";
    private const int MaximumRecords = 8;
    private const int MaximumTraces = 2;
    private const int MaximumTraceBytes = 256 * 1024;
    private static readonly SemaphoreSlim CaptureGate = new(1, 1);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async ValueTask<ProcessExitCaptureResult> CaptureAsync(
        Context context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return new ProcessExitCaptureResult(false, null, 0, 0);

        await CaptureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var applicationContext = context.ApplicationContext ?? context;
            var manager = applicationContext.GetSystemService(Context.ActivityService) as ActivityManager;
            if (manager is null)
                return new ProcessExitCaptureResult(false, null, 0, 0);
            var exits = manager.GetHistoricalProcessExitReasons(null, 0, MaximumRecords);
            var records = exits
                .Where(static info => info is not null)
                .Select(ToRecord)
                .ToArray();
            var root = AndroidPrivateStorage.GetProductLogsRoot(applicationContext);
            var reportPath = Path.Combine(root, ReportFileName);
            var tracePath = Path.Combine(root, TraceFileName);
            await WriteReportAtomicallyAsync(reportPath, records, cancellationToken).ConfigureAwait(false);
            var traceCount = await WriteTracesAtomicallyAsync(tracePath, exits, cancellationToken).ConfigureAwait(false);
            var gameExit = records.FirstOrDefault(record =>
                record.ProcessName?.EndsWith(":game", StringComparison.Ordinal) == true);
            return new ProcessExitCaptureResult(true, gameExit, records.Length, traceCount);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Diagnostics must not prevent launcher resume or a diagnostic export.
            JunimoGateLog.Warn("JunimoGate.ProcessExit", "historical-exit-capture-failed", exception);
            return new ProcessExitCaptureResult(false, null, 0, 0);
        }
        finally
        {
            CaptureGate.Release();
        }
    }

    [SupportedOSPlatform("android30.0")]
    private static ProcessExitRecord ToRecord(ApplicationExitInfo info)
    {
        var stateBytes = info.GetProcessStateSummary();
        var state = stateBytes is null ? null : DecodeUtf8(stateBytes);
        var reason = (ApplicationExitInfoReason)info.Reason;
        return new ProcessExitRecord(
            DateTimeOffset.FromUnixTimeMilliseconds(info.Timestamp),
            info.ProcessName,
            info.Pid,
            reason.ToString(),
            info.Reason,
            info.Status,
            info.Importance,
            info.Pss,
            info.Rss,
            info.Description,
            state);
    }

    private static async ValueTask WriteReportAtomicallyAsync(
        string destination,
        IReadOnlyList<ProcessExitRecord> records,
        CancellationToken cancellationToken)
    {
        var stage = destination + ".stage";
        try
        {
            await using (var stream = new FileStream(
                stage,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(stream, Utf8WithoutBom))
            {
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(JsonSerializer.Serialize(record, JsonOptions).AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                }
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(stage, destination, overwrite: true);
        }
        finally
        {
            TryDelete(stage);
        }
    }

    [SupportedOSPlatform("android30.0")]
    private static async ValueTask<int> WriteTracesAtomicallyAsync(
        string destination,
        IEnumerable<ApplicationExitInfo> exits,
        CancellationToken cancellationToken)
    {
        var stage = destination + ".stage";
        var count = 0;
        try
        {
            await using (var output = new FileStream(
                stage,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(output, Utf8WithoutBom, 16 * 1024, leaveOpen: true))
            {
                foreach (var info in exits)
                {
                    if (count == MaximumTraces)
                        break;
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((ApplicationExitInfoReason)info.Reason != ApplicationExitInfoReason.Anr)
                        continue;
                    await using var trace = info.TraceInputStream;
                    if (trace is null)
                        continue;
                    var bytes = await ReadBoundedAsync(trace, MaximumTraceBytes, cancellationToken).ConfigureAwait(false);
                    var reason = (ApplicationExitInfoReason)info.Reason;
                    await writer.WriteLineAsync(
                        $"exit timestampUtc={DateTimeOffset.FromUnixTimeMilliseconds(info.Timestamp):O} " +
                        $"process={info.ProcessName} pid={info.Pid} reason={reason} encoding=utf8 bytes={bytes.Length}")
                        .ConfigureAwait(false);
                    await writer.WriteLineAsync(DecodeUtf8(bytes).AsMemory(), cancellationToken).ConfigureAwait(false);
                    count++;
                }
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(stage, destination, overwrite: true);
            return count;
        }
        finally
        {
            TryDelete(stage);
        }
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        Stream input,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(maximumBytes);
        var buffer = new byte[16 * 1024];
        while (output.Length < maximumBytes)
        {
            var remaining = maximumBytes - (int)output.Length;
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string DecodeUtf8(byte[] bytes) =>
        Utf8WithoutBom.GetString(bytes).Replace('\0', '\ufffd');

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public sealed record ProcessExitRecord(
        DateTimeOffset TimestampUtc,
        string? ProcessName,
        int ProcessId,
        string Reason,
        int ReasonCode,
        int Status,
        int Importance,
        long PssKilobytes,
        long RssKilobytes,
        string? Description,
        string? LastCheckpoint);
}

public sealed record ProcessExitCaptureResult(
    bool IsSupported,
    AndroidProcessExitDiagnostics.ProcessExitRecord? LatestGameExit,
    int RecordCount,
    int TraceCount);
