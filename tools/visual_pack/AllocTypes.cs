using System.Diagnostics.Tracing;

/// <summary>--types: samples allocations with the runtime's AllocationTick event (about one sample per
/// 100 KB allocated, with the type that crossed the line) - a cheap "what is allocating" histogram.</summary>
sealed class AllocTypes : EventListener
{
    public readonly Dictionary<string, long> Counts = new();
    public volatile bool On;
    protected override void OnEventSourceCreated(EventSource s)
    {
        if (s.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(s, EventLevel.Verbose, (EventKeywords)0x1);
    }
    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!On || e.EventName == null || !e.EventName.StartsWith("GCAllocationTick") || e.Payload == null) return;
        int i = e.PayloadNames!.IndexOf("TypeName");
        string t = i >= 0 ? e.Payload[i]?.ToString() ?? "?" : "?";
        lock (Counts) Counts[t] = Counts.GetValueOrDefault(t) + 1;
    }
    public void Dump(string title)
    {
        lock (Counts)
        {
            Console.WriteLine($"-- allocation samples ({title}), ~100 KB each:");
            foreach (var kv in Counts.OrderByDescending(k => k.Value).Take(25)) Console.WriteLine($"   {kv.Value,6}  {kv.Key}");
            Counts.Clear();
        }
    }
}
