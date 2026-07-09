namespace OpenLineOps.Traceability.Application.Judgements;

public sealed class TraceJudgementOptions
{
    public const string SectionName = "OpenLineOps:Traceability:Judgement";

    public string DefaultJudgement { get; set; } = "Passed";

    public bool FailWhenAnyMeasurementFailed { get; set; } = true;

    public bool UnknownWhenAnyMeasurementIndeterminate { get; set; }

    public bool UnknownWhenNoMeasurements { get; set; }
}
