using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DMRoute_ng.Audio;

/// <summary>Führt den GPL-lizenzierten OpenDMR-Codec in einem getrennten Prozess aus.</summary>
public sealed class OpenDmrProcessCodec(string executablePath)
{
    public const int PcmSamplesPerFrame = 160;
    public const int AmbeBytesPerFrame = 9;

    public string ExecutablePath { get; } =
        string.IsNullOrWhiteSpace(executablePath)
            ? throw new ArgumentException("Der OpenDMR-Pfad darf nicht leer sein.", nameof(executablePath))
            : executablePath;

    public Task<string> GetInfoAsync(CancellationToken cancellationToken = default) =>
        RunAsync(["info"], cancellationToken);

    public async Task<byte[]> EncodeAsync(
        ReadOnlyMemory<short> samples, CancellationToken cancellationToken = default)
    {
        if (samples.Length == 0 || samples.Length % PcmSamplesPerFrame != 0)
            throw new ArgumentException("PCM muss aus vollständigen 20-ms-Frames mit je 160 Samples bestehen.", nameof(samples));

        return await TransformAsync(samples, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<short[]> DecodeAsync(
        ReadOnlyMemory<byte> ambe, CancellationToken cancellationToken = default)
    {
        if (ambe.Length == 0 || ambe.Length % AmbeBytesPerFrame != 0)
            throw new ArgumentException("AMBE muss aus vollständigen Frames mit je 9 Byte bestehen.", nameof(ambe));

        var bytes = await TransformAsync(ambe, false, cancellationToken).ConfigureAwait(false);
        if (bytes.Length != ambe.Length / AmbeBytesPerFrame * PcmSamplesPerFrame * sizeof(short))
            throw new InvalidDataException("OpenDMR hat eine unerwartete PCM-Länge erzeugt.");
        var samples = new short[bytes.Length / sizeof(short)];
        MemoryMarshal.Cast<byte, short>(bytes).CopyTo(samples);
        return samples;
    }

    private async Task<byte[]> TransformAsync<T>(
        ReadOnlyMemory<T> input, bool encode, CancellationToken cancellationToken) where T : unmanaged
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dmroute-opendmr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var inputPath = Path.Combine(directory, encode ? "input.raw" : "input.ambe");
        var outputPath = Path.Combine(directory, encode ? "output.ambe" : "output.raw");
        try
        {
            await using (var stream = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                var bytes = MemoryMarshal.AsBytes(input.Span).ToArray();
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            await RunAsync([encode ? "encode" : "decode", inputPath, outputPath], cancellationToken).ConfigureAwait(false);
            var result = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            var expected = encode
                ? input.Length / PcmSamplesPerFrame * AmbeBytesPerFrame
                : input.Length / AmbeBytesPerFrame * PcmSamplesPerFrame * sizeof(short);
            if (result.Length != expected)
                throw new InvalidDataException($"OpenDMR hat {result.Length} statt {expected} Byte erzeugt.");
            return result;
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("OpenDMR konnte nicht gestartet werden.");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"OpenDMR wurde mit Exit-Code {process.ExitCode} beendet: {error.Trim()}");
            return output.Trim();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }
}
