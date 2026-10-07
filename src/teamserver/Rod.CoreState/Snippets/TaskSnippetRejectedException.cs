namespace Rod.CoreState.Snippets;

/// <summary>
/// A task-snippet operation was refused -- the engagement is closed, the
/// name is already taken, or the engagement holds its snippet budget. A
/// client-correctable domain refusal: the caller maps it to a wire status
/// the way <see cref="Automation.AutomationRuleRejectedException"/> is
/// mapped.
/// </summary>
public sealed class TaskSnippetRejectedException : DomainException
{
    public TaskSnippetRejectedException(string message)
        : base(message)
    {
    }
}
