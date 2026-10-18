using OneDrive_Simple_Management_Tool.Services;

internal static class ResponseStreamChecks
{
    public static async Task RunAsync()
    {
        // TLS stream disconnects can surface as plain IOException, unlike the
        // HttpIOException returned by the HTTP-only loopback test server.
        foreach (bool synchronous in new[] { false, true })
        {
            var disconnected = new IOException("Simulated transport disconnect.");
            using var stream = new DownloadResponseHandler.BoundedStream(
                new DisconnectingStream(disconnected), 8, null, 1000);
            byte[] buffer = new byte[4];
            int count = synchronous ? stream.Read(buffer, 0, buffer.Length) : await stream.ReadAsync(buffer.AsMemory());
            if (count != 4) throw new Exception("Valid prefix was lost.");
            try
            {
                if (synchronous) stream.Read(buffer, 0, buffer.Length);
                else await stream.ReadAsync(buffer.AsMemory());
                throw new Exception("Disconnected transport was accepted.");
            }
            catch (HttpRequestException exception) when (exception.InnerException == disconnected) { }
        }

        // Size validation remains a non-retryable response error.
        using var oversized = new DownloadResponseHandler.BoundedStream(new MemoryStream(new byte[5]), 4, null, 1000);
        try
        {
            await oversized.ReadAsync(new byte[8].AsMemory());
            throw new Exception("Oversized response was accepted.");
        }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS transport IO failures are network errors; body validation remains distinct");
    }

    private sealed class DisconnectingStream(IOException error) : MemoryStream(new byte[4])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == Length ? ValueTask.FromException<int>(error) : base.ReadAsync(buffer, cancellationToken);
    }
}
