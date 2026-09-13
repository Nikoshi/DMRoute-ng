namespace DMRoute_ng.Emulator;

public readonly record struct RadioScenarioStep(ReadOnlyMemory<byte> Packet, TimeSpan DelayBefore);

public sealed class RadioScenario
{
    private readonly IReadOnlyList<RadioScenarioStep> _steps;

    public RadioScenario(string name, int radioId, IEnumerable<RadioScenarioStep> steps)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radioId);

        Name = name;
        RadioId = radioId;
        var ownedSteps = new List<RadioScenarioStep>();
        foreach (var step in steps)
        {
            if (step.DelayBefore < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(steps), "Scenario delays cannot be negative.");
            if (!HomebrewProtocol.IsDmrd(step.Packet.Span))
                throw new ArgumentException("Every scenario step must contain a complete DMRD datagram.", nameof(steps));
            if (HomebrewProtocol.ReadDmrdSourceId(step.Packet.Span) != radioId)
                throw new ArgumentException("Every DMRD source ID must match the scenario radio ID.", nameof(steps));

            ownedSteps.Add(new RadioScenarioStep(step.Packet.ToArray(), step.DelayBefore));
        }

        if (ownedSteps.Count == 0)
            throw new ArgumentException("A radio scenario must contain at least one DMRD datagram.", nameof(steps));
        _steps = ownedSteps.AsReadOnly();
    }

    public string Name { get; }
    public int RadioId { get; }
    public IReadOnlyList<RadioScenarioStep> Steps => _steps;
}
