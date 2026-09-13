namespace DMRoute_ng.Emulator;

public sealed class VirtualRadio(int radioId)
{
    public int RadioId { get; } = radioId > 0
        ? radioId
        : throw new ArgumentOutOfRangeException(nameof(radioId));

    public async Task ReplayAsync(
        VirtualHotspot hotspot,
        RadioScenario scenario,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hotspot);
        ArgumentNullException.ThrowIfNull(scenario);
        if (scenario.RadioId != RadioId)
            throw new ArgumentException("The scenario belongs to a different radio ID.", nameof(scenario));

        foreach (var step in scenario.Steps)
        {
            if (step.DelayBefore > TimeSpan.Zero)
                await Task.Delay(step.DelayBefore, hotspot.TimeProvider, cancellationToken).ConfigureAwait(false);
            await hotspot.SendDmrdAsync(step.Packet, cancellationToken).ConfigureAwait(false);
        }
    }
}
