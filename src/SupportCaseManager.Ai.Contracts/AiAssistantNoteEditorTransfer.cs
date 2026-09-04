using System.IO.Pipes;
using System.Text;

namespace SupportCaseManager.Ai.Contracts;

public static class AiAssistantNoteEditorTransfer
{
    private const int ConnectAttempts = 5;
    private const int ConnectTimeoutMilliseconds = 500;

    public static string CreatePipeName()
    {
        return $"SupportCaseManager.NoteEditor.{Guid.NewGuid():N}";
    }

    public static async Task<bool> SendAsync(
        string? pipeName,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        for (var attempt = 0; attempt < ConnectAttempts; attempt++)
        {
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    pipeName.Trim(),
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                await client.ConnectAsync(ConnectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);

                await using var writer = new StreamWriter(
                    client,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4096,
                    leaveOpen: true)
                {
                    AutoFlush = true,
                };
                await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException) when (attempt + 1 < ConnectAttempts)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (attempt + 1 < ConnectAttempts)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }
}

public sealed class AiAssistantNoteEditorTransferServer : IDisposable
{
    private readonly string pipeName;
    private readonly Action<string> onTextReceived;
    private readonly CancellationTokenSource cancellation = new();
    private Task? listenTask;
    private bool disposed;

    public AiAssistantNoteEditorTransferServer(string pipeName, Action<string> onTextReceived)
    {
        this.pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("Pipe name is required.", nameof(pipeName))
            : pipeName.Trim();
        this.onTextReceived = onTextReceived ?? throw new ArgumentNullException(nameof(onTextReceived));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (listenTask is not null)
        {
            throw new InvalidOperationException("The note editor transfer server has already started.");
        }

        listenTask = ListenAsync(cancellation.Token);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        try
        {
            listenTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(
                    server,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);
                var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    onTextReceived(text);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // Recreate the server endpoint after a transient client disconnect.
            }
        }
    }
}
