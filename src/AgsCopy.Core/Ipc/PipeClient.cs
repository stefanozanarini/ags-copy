using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AgsCopy.Core.Ipc;

public sealed class ServiceException(string message) : Exception(message);

public static class PipeClient
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Sends one request to the service.</summary>
    /// <exception cref="ServiceUnavailableException">The service is not running.</exception>
    /// <exception cref="ServiceException">The service refused or failed the request.</exception>
    public static async Task<T?> CallAsync<T>(string command, object? payload = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);

        // Identification lets the service check whether the caller is an administrator.
        await using var pipe = new NamedPipeClientStream(".", Pipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(3000, cts.Token);
        }
        catch (TimeoutException)
        {
            throw new ServiceUnavailableException();
        }

        var request = new PipeRequest
        {
            Command = command,
            Lang = Loc.Language,
            Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, Json.Options),
        };

        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json.Options).AsMemory(), cts.Token);

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 65536, leaveOpen: true);
        var line = await reader.ReadLineAsync(cts.Token) ?? throw new ServiceException("The service closed the connection.");
        var response = JsonSerializer.Deserialize<PipeResponse>(line, Json.Options) ?? throw new ServiceException("Invalid response.");
        if (!response.Ok) throw new ServiceException(response.Error ?? "Error");
        return response.Payload is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } p
            ? p.Deserialize<T>(Json.Options)
            : default;
    }

    public static Task CallAsync(string command, object? payload = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
        CallAsync<JsonElement?>(command, payload, timeout, ct);
}

public sealed class ServiceUnavailableException() : Exception("The AGS-Copy service is not running.");
