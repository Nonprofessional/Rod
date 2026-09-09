using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Rod.Transport;
using Rod.Transport.Endpoints;

namespace Rod.Integration.Tests;

/// <summary>
/// The runtime settings endpoints: the session-presence pair reads back, a
/// PUT applies live (the settings singleton the sweeper reads changes
/// immediately), bounds violations refuse with a 400, and the persisted file
/// a change writes is what the next settings instance loads -- the
/// restart-remembers contract.
/// </summary>
public class SettingsEndpointsTests
{
    [Fact]
    public async Task SessionSettings_ReadAndChange_OverTheApi()
    {
        var (client, host, _) = AuthenticatedHost.Create();
        using (client)
        using (host)
        {
            await AuthenticatedHost.LoginAsync(client);

            // No Sessions:Staleness section in the test host: the boot
            // defaults (15 minutes / 1 minute) are what the endpoint reads.
            var initial = await client.GetFromJsonAsync<SettingsEndpoints.SessionSettingsResponse>(
                "/settings/sessions");
            Assert.NotNull(initial);
            Assert.Equal(15, initial!.ThresholdMinutes);
            Assert.Equal(1, initial.SweepIntervalMinutes);

            // The change applies live: the singleton the sweeper reads on
            // every pass carries the new pair the moment the PUT returns.
            var changed = await client.PutAsJsonAsync(
                "/settings/sessions",
                new SettingsEndpoints.SessionSettingsRequest(ThresholdMinutes: 5, SweepIntervalMinutes: 0.5));
            changed.EnsureSuccessStatusCode();
            var applied = await changed.Content.ReadFromJsonAsync<SettingsEndpoints.SessionSettingsResponse>();
            Assert.NotNull(applied);
            Assert.Equal(5, applied!.ThresholdMinutes);
            Assert.Equal(0.5, applied.SweepIntervalMinutes);

            var settings = host.Services.GetRequiredService<SessionRuntimeSettings>();
            Assert.Equal(TimeSpan.FromMinutes(5), settings.Current.Threshold);
            Assert.Equal(TimeSpan.FromSeconds(30), settings.Current.SweepInterval);

            var reread = await client.GetFromJsonAsync<SettingsEndpoints.SessionSettingsResponse>(
                "/settings/sessions");
            Assert.Equal(5, reread!.ThresholdMinutes);

            // Out-of-bounds values refuse loudly rather than clamping: a
            // threshold under a minute would flap every quiet check-in gap
            // to offline.
            var refused = await client.PutAsJsonAsync(
                "/settings/sessions",
                new SettingsEndpoints.SessionSettingsRequest(ThresholdMinutes: 0.5, SweepIntervalMinutes: 1));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
        }
    }

    [Fact]
    public void SessionSettings_PersistedFile_IsWhatTheNextInstanceLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), "rod-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var first = new SessionRuntimeSettings(SessionStalenessOptions.Default, path);
            first.Change(TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(20));

            // The same contract a restart exercises: a fresh settings holder
            // over the same file starts from the persisted pair, not the boot
            // defaults.
            var second = new SessionRuntimeSettings(SessionStalenessOptions.Default, path);
            Assert.Equal(TimeSpan.FromMinutes(3), second.Current.Threshold);
            Assert.Equal(TimeSpan.FromSeconds(20), second.Current.SweepInterval);
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp cleanup */ }
        }
    }
}
