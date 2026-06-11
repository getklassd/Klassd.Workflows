namespace Klassd.Workflows.Abstractions;

/// <summary>How a declared job input is rendered and validated in the UI.</summary>
public enum JobInputType { Text, Number, Boolean }

/// <summary>
/// Declares an input a job accepts at start time, right next to the job class.
/// Purely descriptive metadata: it drives the dashboard's "Run" form and lets
/// callers see what a job expects — the job still reads values from
/// <see cref="IJobContext.Arguments"/>, and programmatic callers still pass a
/// plain <c>Dictionary&lt;string,string&gt;</c> to <c>EnqueueAsync</c>. Apply
/// once per input; the attribute is repeatable.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class JobInputAttribute(string name) : Attribute
{
    /// <summary>Argument key the job reads from <c>context.Arguments</c>.</summary>
    public string Name { get; } = name;

    /// <summary>Human label shown in the form (defaults to <see cref="Name"/>).</summary>
    public string? Label { get; set; }

    /// <summary>Pre-filled value in the form when the user hasn't typed one.</summary>
    public string? Default { get; set; }

    /// <summary>When true, the form blocks start until a value is supplied.</summary>
    public bool Required { get; set; }

    /// <summary>Editor kind / coarse validation hint.</summary>
    public JobInputType Type { get; set; } = JobInputType.Text;

    /// <summary>Optional helper text shown under the field.</summary>
    public string? Description { get; set; }
}
