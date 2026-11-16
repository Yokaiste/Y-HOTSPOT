namespace HotspotApp.Services.Infrastructure;

internal static class AdapterReadiness
{
    internal static async Task<T> WaitAsync<T>(Func<Task<T?>> probe, CancellationToken token,
        int attempts = 20, TimeSpan? interval = null) where T : class
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = await probe();
                token.ThrowIfCancellationRequested();
                if (result != null) return result;
            }
            // Restart-NetAdapter pode terminar antes de o WinRT reconstruir a placa.
            // Somente o estado transitório observado permite repetir a consulta.
            catch (System.Runtime.InteropServices.COMException ex)
                when (ex.HResult == unchecked((int)0x8007139F) && attempt + 1 < attempts) { }
            if (attempt + 1 < attempts)
                await Task.Delay(interval ?? TimeSpan.FromMilliseconds(500), token);
        }
        throw new TimeoutException("Wi-Fi adapter did not become ready after restart.");
    }
}
