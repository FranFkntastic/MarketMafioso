using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace InventoryReporter2;

public class HttpReporter : IDisposable
{
    private readonly HttpClient httpClient = new();
    private readonly Configuration config;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly IChatGui chatGui;
    private readonly InventoryScanner scanner;

    private static readonly JsonSerializerOptions SerialiserOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public DateTime? LastSentAt { get; private set; }
    public string LastStatus { get; private set; } = "Never sent";
    public string? LastPayload { get; private set; }

    public HttpReporter(
        Configuration config,
        IPlayerState playerState,
        IPluginLog log,
        IChatGui chatGui,
        InventoryScanner scanner)
    {
        this.config = config;
        this.playerState = playerState;
        this.log = log;
        this.chatGui = chatGui;
        this.scanner = scanner;
    }

    public async Task SendReportAsync()
    {
        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            chatGui.PrintError("[InventoryReporter2] No server URL configured. Use /invreport to set one.");
            return;
        }

        try
        {
            string? charName = null;
            string? homeWorld = null;

            if (config.IncludeCharacterInfo)
            {
                charName = playerState.CharacterName;
                homeWorld = playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : null;
            }

            var playerInventory = scanner.ScanPlayerInventory(config);

            var retainers = config.RetainerCache.Values
                .Select(r => new RetainerReport
                {
                    RetainerName = r.RetainerName,
                    RetainerId = r.RetainerId,
                    LastUpdated = r.LastUpdated.ToString("o"),
                    Bags = r.Bags
                        .Select(b => new InventoryBag
                        {
                            BagName = b.BagName,
                            Items = b.Items
                                .Select(i => new ItemSlot
                                {
                                    ItemId = i.ItemId,
                                    ItemName = i.ItemName,
                                    Quantity = i.Quantity,
                                    IsHQ = i.IsHQ,
                                    Condition = i.Condition,
                                })
                                .ToList(),
                        })
                        .ToList(),
                })
                .ToList();

            var report = new InventoryReport
            {
                CharacterName = charName,
                HomeWorld = homeWorld,
                Timestamp = DateTime.UtcNow.ToString("o"),
                PlayerInventory = playerInventory,
                Retainers = retainers,
            };

            LastPayload = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, config.ServerUrl)
            {
                Content = JsonContent.Create(report, options: SerialiserOptions),
            };

            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                request.Headers.Add("X-Api-Key", config.ApiKey);

            var response = await httpClient.SendAsync(request).ConfigureAwait(false);

            LastSentAt = DateTime.Now;
            LastStatus = $"{(int)response.StatusCode} {response.ReasonPhrase}";

            if (response.IsSuccessStatusCode)
            {
                var itemCount = playerInventory.Sum(b => b.Items.Count);
                chatGui.Print(
                    $"[InventoryReporter2] ✓ Sent {itemCount} player items + {retainers.Count} retainer(s). " +
                    $"Status: {LastStatus}");
                log.Information($"[InventoryReporter2] Report sent — {LastStatus}");
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                chatGui.PrintError($"[InventoryReporter2] Server error {LastStatus}: {body[..Math.Min(body.Length, 200)]}");
                log.Warning($"[InventoryReporter2] Server returned {LastStatus}: {body}");
            }
        }
        catch (Exception ex)
        {
            LastStatus = $"Error: {ex.Message}";
            chatGui.PrintError($"[InventoryReporter2] Failed to send: {ex.Message}");
            log.Error(ex, "[InventoryReporter2] Error sending report");
        }
    }

    public void Dispose() => httpClient.Dispose();
}
