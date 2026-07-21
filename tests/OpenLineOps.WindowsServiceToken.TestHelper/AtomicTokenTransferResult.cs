using System.Text;
using System.Text.Json;

namespace OpenLineOps.WindowsServiceToken.TestHelper;

internal sealed class AtomicTokenTransferResult : IDisposable
{
    private readonly string _resultPath;
    private readonly string _temporaryPath;
    private FileStream? _stream;
    private bool _published;

    public AtomicTokenTransferResult(string resultPath, string nonce)
    {
        _resultPath = resultPath;
        var directory = Path.GetDirectoryName(resultPath)
                        ?? throw new InvalidDataException(
                            "The token-transfer result path has no parent directory.");
        var fileName = Path.GetFileName(resultPath);
        _temporaryPath = Path.Combine(directory, $".{fileName}.{nonce}.tmp");
        _stream = new FileStream(
            _temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
    }

    public void Publish(WindowsServiceTokenTransferResult result)
    {
        ObjectDisposedException.ThrowIf(_stream is null, this);
        if (_published)
        {
            throw new InvalidOperationException("The token-transfer result was already published.");
        }

        using (var writer = new Utf8JsonWriter(_stream, new JsonWriterOptions
        {
            Indented = true,
            SkipValidation = false
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("nonce", result.Nonce);
            writer.WriteNumber("sourceProcessId", result.SourceProcessId);
            writer.WriteBoolean("helperIdentityValidated", result.HelperIdentityValidated);
            writer.WriteBoolean("sourceServiceValidated", result.SourceServiceValidated);
            writer.WriteBoolean("sourceProcessValidated", result.SourceProcessValidated);
            writer.WriteBoolean("sourceTokenValidated", result.SourceTokenValidated);
            writer.WriteBoolean("controlPipeConnected", result.ControlPipeConnected);
            writer.WriteBoolean("receiptReceived", result.ReceiptReceived);
            writer.WriteString("failurePhase", result.FailurePhase);
            writer.WriteEndObject();
            writer.Flush();
        }

        _stream.Write(Encoding.UTF8.GetBytes("\n"));
        _stream.Flush(flushToDisk: true);
        _stream.Dispose();
        _stream = null;
        File.Move(_temporaryPath, _resultPath, overwrite: false);
        _published = true;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        if (!_published && File.Exists(_temporaryPath))
        {
            File.Delete(_temporaryPath);
        }
    }
}
