using HotspotApp.Localization;
using System.Buffers.Binary;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HotspotApp.Services.Infrastructure.Supervision;

public sealed record SupervisorRequest(string Command, JsonElement? Configuration = null);
public sealed record SupervisorReply(bool Success, string Status, bool OwnsSession);

public static class SupervisorProtocol
{
    private const int MaximumBytes = 32768;
    // Dados da rede trafegam somente no pipe privado; nunca em argumentos ou logs.
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumBytes) throw new IOException(AppStrings.Get("Text_RecoveryMessage19"));
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumBytes) throw new IOException(AppStrings.Get("Text_RecoveryMessage20"));
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new IOException(AppStrings.Get("Text_RecoveryMessage21"));
    }
}

public static partial class PipePeer
{
    // O nome aleatório e CurrentUserOnly não bastam: conferir o processo no outro lado.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
