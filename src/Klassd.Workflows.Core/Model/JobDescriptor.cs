namespace Klassd.Workflows.Core.Model;

/// <summary>Everything needed to start one run of a job.</summary>
public sealed record JobDescriptor(
    string JobName,
    string JobTypeName,
    Dictionary<string, string> Arguments);
