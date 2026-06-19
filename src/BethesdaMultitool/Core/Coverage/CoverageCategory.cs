namespace BethesdaMultitool.Core.Coverage;

/// <summary>Classifies how a recognized span of a memory dump was identified during coverage analysis.</summary>
public enum CoverageCategory
{
    Header,
    Module,
    CarvedFile,
    EsmRecord,
    Region // internal use only
}
