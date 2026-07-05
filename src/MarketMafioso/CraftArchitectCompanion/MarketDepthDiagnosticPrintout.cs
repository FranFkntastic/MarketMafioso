using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;

namespace MarketMafioso.CraftArchitectCompanion;

public sealed record MarketDepthDiagnosticReport
{
    public MarketAppraisalRequest Request { get; init; } = new();
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset FinishedAtUtc { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<MarketDepthDiagnosticStep> Steps { get; init; } = [];
}

public sealed record MarketDepthDiagnosticStep
{
    public string Name { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }
    public long DurationMs { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string ExceptionType { get; init; } = string.Empty;
    public string ExceptionMessage { get; init; } = string.Empty;
    public HttpStatusCode? HttpStatusCode { get; init; }
    public string RequestUri { get; init; } = string.Empty;
}

public static class MarketDepthDiagnosticPrintout
{
    public static string Write(
        string directory,
        MarketDepthDiagnosticReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(report);

        Directory.CreateDirectory(directory);
        var path = GetAvailablePath(directory, report.FinishedAtUtc);
        File.WriteAllLines(path, BuildLines(report));
        return path;
    }

    private static IReadOnlyList<string> BuildLines(MarketDepthDiagnosticReport report)
    {
        var request = report.Request;
        var lines = new List<string>
        {
            "Craft Architect market-depth diagnostic",
            $"Started: {report.StartedAtUtc:O}",
            $"Finished: {report.FinishedAtUtc:O}",
            $"Outcome: {report.Outcome}",
            $"Summary: {report.Summary}",
            string.Empty,
            $"Request: {request.ItemName} ({request.ItemId}) x{request.Quantity}",
            $"Scope: region {request.Region}, world mode {request.WorldMode}, sweep {request.SweepScope}",
            $"Threshold: {request.BuyThresholdUnitPrice:N0} gil/unit, gil cap {request.GilCap:N0}, HQ {request.HqPolicy}",
            string.Empty,
            "Steps:",
        };

        foreach (var step in report.Steps)
        {
            lines.Add($"- {step.Name}: {step.Outcome}, {step.DurationMs:N0} ms");
            if (!string.IsNullOrWhiteSpace(step.Detail))
                lines.Add($"  Detail: {step.Detail}");
            if (step.HttpStatusCode is { } statusCode)
                lines.Add($"  HTTP: {(int)statusCode} {statusCode}");
            if (!string.IsNullOrWhiteSpace(step.RequestUri))
                lines.Add($"  Endpoint: {step.RequestUri}");
            if (!string.IsNullOrWhiteSpace(step.ExceptionType))
                lines.Add($"  Exception: {step.ExceptionType}: {step.ExceptionMessage}");
        }

        return lines;
    }

    private static string GetAvailablePath(string directory, DateTimeOffset writtenAtUtc)
    {
        var baseName = $"market-depth-{writtenAtUtc:yyyyMMdd-HHmmss}";
        var path = Path.Combine(directory, $"{baseName}.log");
        if (!File.Exists(path))
            return path;

        for (var suffix = 1; suffix < 1000; suffix++)
        {
            path = Path.Combine(directory, $"{baseName}-{suffix.ToString(CultureInfo.InvariantCulture)}.log");
            if (!File.Exists(path))
                return path;
        }

        throw new IOException($"Unable to create a unique market-depth diagnostic file under {directory}.");
    }
}
