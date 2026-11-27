using KoLibri;

namespace Cookbook.Events;

public sealed class EventsOptions
{
}

public static class Recipe
{
    /// <summary>Read-only: hängt sich an die Events der Sitzung und löst per Status-/Inventarabruf ein paar Requests aus.</summary>
    public static async Task<int> RunAsync(KolibriSession session, EventsOptions options, TextWriter output, CancellationToken ct = default)
    {
        var count = 0;
        void Log(string line) { Interlocked.Increment(ref count); lock (output) output.WriteLine(line); }

        EventHandler<RequestEventArgs> onRequest = (_, e) => Log($"request {e.Method} {e.Path} -> {e.StatusCode} {e.Kind} ({e.Duration.TotalMilliseconds:N0} ms, gewartet {e.Waited.TotalMilliseconds:N0} ms)");
        EventHandler<EncounterEventArgs> onEncounter = (_, e) => Log($"encounter {e.Encounter.GetType().Name} {e.Encounter.Title}");
        EventHandler<ItemAcquiredEventArgs> onItem = (_, e) => Log($"item {e.Item.Quantity} x {e.Item.Name} ({e.Source})");

        session.Events.Request += onRequest;
        session.Events.Encounter += onEncounter;
        session.Events.ItemAcquired += onItem;
        try
        {
            await session.Character.GetStatusAsync(refresh: true, ct);
            await session.Inventory.GetAsync(refresh: true, ct);
        }
        finally
        {
            session.Events.Request -= onRequest;
            session.Events.Encounter -= onEncounter;
            session.Events.ItemAcquired -= onItem;
        }
        output.WriteLine($"{count} Ereignisse protokolliert.");
        return 0;
    }
}
