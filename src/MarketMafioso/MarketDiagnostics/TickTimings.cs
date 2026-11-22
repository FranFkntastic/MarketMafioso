using System;
using System.Diagnostics;
using System.Linq;

namespace MarketMafioso.MarketDiagnostics;

// Framework-thread only. Reset the count, not the buffer: early returns expose only this tick's steps.
internal sealed class TickTimings(int capacity)
{
    private readonly (string Name, long Ticks)[] steps = new (string, long)[capacity];
    private int count;
    private int overflow;
    private long previous;

    internal long Start()
    {
        count = 0;
        overflow = 0;
        return previous = Stopwatch.GetTimestamp();
    }

    // Diagnostics must never take down the frame: past capacity, count the step instead of throwing.
    internal long Mark(string name)
    {
        var now = Stopwatch.GetTimestamp();
        if (count < steps.Length)
            steps[count++] = (name, now - previous);
        else
            overflow++;
        return previous = now;
    }

    // Enumerate and format only after the slow-frame and rate-limit checks.
    internal string Format(string? nestedStep = null, TickTimings? nested = null) =>
        FormatSteps(nestedStep, nested) + (overflow == 0
            ? string.Empty
            : FormattableString.Invariant($" [{overflow} step(s) past TickTimings capacity {steps.Length}; raise it]"));

    private string FormatSteps(string? nestedStep, TickTimings? nested) =>
        string.Join(", ", steps.Take(count)
            .Where(step => step.Ticks >= (Stopwatch.Frequency + 999) / 1000)
            .OrderByDescending(step => step.Ticks)
            .Select(step =>
            {
                var label = FormattableString.Invariant($"{step.Name} {step.Ticks * 1000d / Stopwatch.Frequency:F1}");
                return step.Name == nestedStep && nested?.Format() is { Length: > 0 } detail
                    ? $"{label} ({detail})"
                    : label;
            }));
}
