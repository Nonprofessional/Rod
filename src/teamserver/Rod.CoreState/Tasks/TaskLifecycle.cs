namespace Rod.CoreState.Tasks;

/// <summary>
/// Where a <see cref="Task"/> sits in its dispatch lifecycle
/// (architecture.md Sec 10.3): <c>Task -&gt; dispatched -&gt; result</c>.
///
/// - <see cref="Queued"/>: an operator issued it; it awaits the next dispatch.
/// - <see cref="Dispatched"/>: the teamserver handed it to the implant stream.
/// - <see cref="Completed"/>: the implant returned a result, success or failure.
/// - <see cref="Cancelled"/>: an operator retracted it while queued; it is
///   terminal and never dispatched.
/// </summary>
public enum TaskStatus
{
    /// <summary>Issued by an operator; not yet handed to the implant.</summary>
    Queued,

    /// <summary>Handed to the implant stream; awaiting its result.</summary>
    Dispatched,

    /// <summary>The implant returned a result (see <see cref="TaskOutcome"/>).</summary>
    Completed,

    /// <summary>
    /// Retracted by an operator before dispatch. Terminal: a cancelled task is
    /// never claimed by a dispatch and never runs on the implant.
    /// </summary>
    Cancelled,
}

/// <summary>
/// How a completed task turned out, as reported by the implant. A non-zero exit
/// or an execution error is <see cref="Failed"/>; output is still captured either
/// way. Kept separate from <see cref="TaskStatus"/> so the lifecycle and the
/// outcome are independent facts.
/// </summary>
public enum TaskOutcome
{
    /// <summary>The verb ran and reported success.</summary>
    Succeeded,

    /// <summary>The verb ran but reported failure (e.g. non-zero exit).</summary>
    Failed,
}
