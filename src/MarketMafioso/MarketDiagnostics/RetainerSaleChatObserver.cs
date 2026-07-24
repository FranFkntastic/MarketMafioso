using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MarketMafioso.Contracts;

namespace MarketMafioso.MarketDiagnostics;

internal sealed class RetainerSaleChatObserver : IDisposable
{
    private static readonly Regex[] SalePatterns =
    [
        new(@"(?:have|has) sold for (?<value>[\d,. ]+) gil \(after fees\)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"(?<value>[\d,. ]+)ギルを入手しました", RegexOptions.Compiled),
        new(@"(?<value>[\d,. ]+) Gil (?:verkauft|erhalten)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"pour (?<value>[\d,. ]+) gil", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    private readonly HttpClient httpClient = new();
    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly string outboxPath;
    private readonly object outboxGate = new();
    private readonly SemaphoreSlim flushGate = new(1, 1);
    private readonly List<RetainerSaleEvidenceCreateRequest> pending;
    private DateTimeOffset nextRetryAtUtc = DateTimeOffset.MinValue;
    private bool disposed;

    public RetainerSaleChatObserver(
        Configuration configuration,
        IPlayerState playerState,
        IDataManager dataManager,
        IChatGui chatGui,
        IPluginLog log,
        string outboxPath)
    {
        this.configuration = configuration;
        this.playerState = playerState;
        this.dataManager = dataManager;
        this.chatGui = chatGui;
        this.log = log;
        this.outboxPath = outboxPath;
        pending = LoadOutbox(outboxPath, log);
        chatGui.ChatMessage += OnChatMessage;
        if (configuration.EnableMarketDiagnostics && pending.Count > 0)
            _ = FlushAsync();
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (disposed ||
            !configuration.EnableMarketDiagnostics ||
            message.LogKind != XivChatType.RetainerSale)
        {
            return;
        }

        try
        {
            var item = message.Message.Payloads.OfType<ItemPayload>().FirstOrDefault();
            if (item == null || item.ItemId == 0)
            {
                log.Warning("[MarketMafioso] Retainer sale chat event had no item payload.");
                return;
            }

            var text = message.Message.TextValue;
            if (!TryReadTotalGil(text, out var totalGil))
            {
                log.Warning("[MarketMafioso] Retainer sale chat event had no parseable gil total.");
                return;
            }

            var eventAt = message.Timestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(message.Timestamp)
                : DateTimeOffset.UtcNow;
            var itemName = item.DisplayName;
            if (string.IsNullOrWhiteSpace(itemName))
                itemName = dataManager.GetExcelSheet<Item>().GetRowOrDefault(item.ItemId)?.Name.ToString();
            var rawMessage = text.Length <= 2000 ? text : text[..2000];
            var evidenceId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{eventAt:O}|{item.ItemId}|{item.IsHQ}|{totalGil}|{rawMessage}"))));
            var evidence = new RetainerSaleEvidenceCreateRequest
            {
                EvidenceId = evidenceId,
                ItemId = item.ItemId,
                ItemName = itemName,
                IsHq = item.IsHQ,
                TotalGil = totalGil,
                EventAtUtc = eventAt,
                CharacterName = playerState.CharacterName,
                HomeWorld = playerState.HomeWorld.IsValid
                    ? playerState.HomeWorld.Value.Name.ToString()
                    : null,
                RawMessage = rawMessage,
            };
            Enqueue(evidence);
            _ = FlushAsync();
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Failed to capture retainer sale chat evidence.");
        }
    }

    public void Tick()
    {
        if (disposed ||
            !configuration.EnableMarketDiagnostics ||
            DateTimeOffset.UtcNow < nextRetryAtUtc)
        {
            return;
        }

        lock (outboxGate)
        {
            if (pending.Count == 0)
                return;
        }

        _ = FlushAsync();
    }

    internal void EnqueueExternal(RetainerSaleEvidenceCreateRequest evidence)
    {
        if (disposed || !configuration.EnableMarketDiagnostics)
            return;

        Enqueue(evidence);
        _ = FlushAsync();
    }

    private void Enqueue(RetainerSaleEvidenceCreateRequest evidence)
    {
        lock (outboxGate)
        {
            if (pending.Any(candidate => candidate.EvidenceId == evidence.EvidenceId))
                return;

            pending.Add(evidence);
            SaveOutbox();
        }
    }

    private async Task FlushAsync()
    {
        if (!await flushGate.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            while (!disposed && configuration.EnableMarketDiagnostics)
            {
                RetainerSaleEvidenceCreateRequest? evidence;
                lock (outboxGate)
                    evidence = pending.FirstOrDefault();
                if (evidence == null)
                    return;

                if (!await SendAsync(evidence).ConfigureAwait(false))
                {
                    nextRetryAtUtc = DateTimeOffset.UtcNow.AddMinutes(1);
                    return;
                }

                lock (outboxGate)
                {
                    pending.RemoveAll(candidate => candidate.EvidenceId == evidence.EvidenceId);
                    SaveOutbox();
                }
            }
        }
        finally
        {
            flushGate.Release();
        }
    }

    private async Task<bool> SendAsync(RetainerSaleEvidenceCreateRequest evidence)
    {
        var endpoint = ReceiverEndpointClassifier.BuildMarketDiagnosticSaleUrl(configuration.ServerUrl);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            log.Warning("[MarketMafioso] Retainer sale evidence was not sent because the receiver URL is invalid.");
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(evidence),
            };
            if (!string.IsNullOrWhiteSpace(configuration.ApiKey))
                request.Headers.Add("X-Api-Key", configuration.ApiKey);
            using var response = await httpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                log.Warning(
                    "[MarketMafioso] Retainer sale evidence upload failed with {StatusCode}: {Body}",
                    (int)response.StatusCode,
                    body[..Math.Min(body.Length, 300)]);
                return false;
            }

            log.Information(
                "[MarketMafioso] Recorded {Source} sale evidence for item {ItemId}, {TotalGil} gil.",
                evidence.Source,
                evidence.ItemId,
                evidence.TotalGil);
            return true;
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[MarketMafioso] Retainer sale evidence upload failed.");
            return false;
        }
    }

    private void SaveOutbox()
    {
        try
        {
            var directory = Path.GetDirectoryName(outboxPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var temporaryPath = $"{outboxPath}.tmp";
            File.WriteAllText(temporaryPath, System.Text.Json.JsonSerializer.Serialize(pending));
            File.Move(temporaryPath, outboxPath, overwrite: true);
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Failed to persist the retainer sale outbox.");
        }
    }

    private static List<RetainerSaleEvidenceCreateRequest> LoadOutbox(
        string path,
        IPluginLog log)
    {
        try
        {
            if (!File.Exists(path))
                return [];

            return System.Text.Json.JsonSerializer.Deserialize<List<RetainerSaleEvidenceCreateRequest>>(
                       File.ReadAllText(path)) ??
                   [];
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Failed to load the retainer sale outbox.");
            return [];
        }
    }

    internal static bool TryReadTotalGil(string message, out ulong totalGil)
    {
        foreach (var pattern in SalePatterns)
        {
            var match = pattern.Match(message);
            if (!match.Success)
                continue;

            var digits = string.Concat(match.Groups["value"].Value.Where(char.IsDigit));
            if (ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out totalGil))
                return true;
        }

        totalGil = 0;
        return false;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        chatGui.ChatMessage -= OnChatMessage;
        lock (outboxGate)
            SaveOutbox();
        httpClient.Dispose();
    }
}
