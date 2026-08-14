using Rod.CoreState.Engagements;
using Rod.CoreState.Implants;
using Rod.CoreState.Operators;
using Rod.CoreState.Tasks;
// The domain entity shares its name with System.Threading.Tasks.Task; this file
// is async throughout, so pin Task to the BCL type and reach the entity by its
// full name.
using Task = System.Threading.Tasks.Task;

namespace Rod.CoreState.Tests;

/// <summary>
/// Paged task-history reads of <see cref="InMemoryTaskRepository"/>
/// (architecture.md Sec 10.3): the newest window first, a cursor walking one
/// page older, oldest-first within each page, and the walk ending in a null
/// cursor at the beginning of history.
/// </summary>
public class TaskPageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static async Task<List<Rod.CoreState.Tasks.Task>> SeedAsync(
        InMemoryTaskRepository tasks, EngagementId engagement, ImplantId implant, int count)
    {
        var seeded = new List<Rod.CoreState.Tasks.Task>(count);
        for (var i = 0; i < count; i++)
        {
            var task = Rod.CoreState.Tasks.Task.Create(
                TaskId.New(), engagement, implant, OperatorId.New(), "shell.exec", string.Empty, Now.AddMinutes(i));
            await tasks.SaveAsync(task);
            seeded.Add(task);
        }

        return seeded;
    }

    [Fact]
    public async Task PageWalk_CoversEveryTask_Once_InOrder()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var implant = ImplantId.New();
        var seeded = await SeedAsync(tasks, engagement, implant, count: 7);

        var seen = new List<TaskId>();
        string? cursor = null;
        do
        {
            var page = await tasks.ListByEngagementPageAsync(engagement, limit: 3, cursor);

            // Oldest first within the page.
            Assert.Equal(
                page.Items.Select(t => t.Id),
                page.Items.OrderBy(t => t.CreatedAt).Select(t => t.Id));
            seen.AddRange(page.Items.Select(t => t.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        // Three pages: 3 + 3 + 1, no duplicates, and the concatenation covers
        // the full history (pages walk newest window first, so the concatenation
        // is not globally ascending -- the set is what must match).
        Assert.Equal(
            seeded.Select(t => t.Id).OrderBy(id => id.Value).ToArray(),
            seen.OrderBy(id => id.Value).ToArray());
    }

    [Fact]
    public async Task FirstPage_IsTheNewestWindow()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var seeded = await SeedAsync(tasks, engagement, ImplantId.New(), count: 5);

        var page = await tasks.ListByEngagementPageAsync(engagement, limit: 2, cursor: null);

        // Newest two tasks, oldest first within the page.
        Assert.Equal(new[] { seeded[3].Id, seeded[4].Id }, page.Items.Select(t => t.Id).ToArray());
        Assert.NotNull(page.NextCursor);
    }

    [Fact]
    public async Task LastPage_HasANullCursor()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        await SeedAsync(tasks, engagement, ImplantId.New(), count: 2);

        var page = await tasks.ListByEngagementPageAsync(engagement, limit: 5, cursor: null);

        Assert.Equal(2, page.Items.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Pages_StayScopedByEngagement()
    {
        // Cross-engagement isolation holds on the paged read too (architecture.md
        // Sec 3): another engagement's tasks never leak into a page.
        var tasks = new InMemoryTaskRepository();
        var engagementA = EngagementId.New();
        var engagementB = EngagementId.New();
        await SeedAsync(tasks, engagementA, ImplantId.New(), count: 3);
        await SeedAsync(tasks, engagementB, ImplantId.New(), count: 2);

        var page = await tasks.ListByEngagementPageAsync(engagementA, limit: 10, cursor: null);

        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, t => Assert.Equal(engagementA, t.EngagementId));
    }

    [Fact]
    public async Task ImplantPages_WalkTheImplantHistory()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();
        var implant = ImplantId.New();
        var otherImplant = ImplantId.New();
        var seeded = await SeedAsync(tasks, engagement, implant, count: 4);
        await SeedAsync(tasks, engagement, otherImplant, count: 2);

        var seen = new List<TaskId>();
        string? cursor = null;
        do
        {
            var page = await tasks.ListByImplantPageAsync(implant, limit: 3, cursor);
            seen.AddRange(page.Items.Select(t => t.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(
            seeded.Select(t => t.Id).OrderBy(id => id.Value).ToArray(),
            seen.OrderBy(id => id.Value).ToArray());
    }

    [Fact]
    public async Task GarbageCursor_Throws()
    {
        var tasks = new InMemoryTaskRepository();
        var engagement = EngagementId.New();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            tasks.ListByEngagementPageAsync(engagement, limit: 10, cursor: "not-a-cursor"));
    }
}
