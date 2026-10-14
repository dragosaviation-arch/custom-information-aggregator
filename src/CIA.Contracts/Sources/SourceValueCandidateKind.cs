namespace CIA.Contracts.Sources;

public enum SourceValueCandidateKind
{
    Element = 1,
    Attribute = 2,
    Structural = 3,
    ElementScope = 4
}

public enum SourceElementRole
{
    Structural = 1,
    ValueBearing = 2,
    Mixed = 3
}
