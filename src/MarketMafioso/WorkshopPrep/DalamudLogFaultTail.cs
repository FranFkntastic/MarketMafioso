using System;
using System.IO;
using System.Text;

namespace MarketMafioso.WorkshopPrep;

// Dalamud wraps every agent's ReceiveEvent and swallows native crashes inside it,
// logging one line per crash. The caller (us, firing an addon callback) never
// learns the game handler died halfway, and a half-run handler can leave the
// character stuck "occupied". Dalamud's log sink is async and unbuffered, so the
// line is on disk within milliseconds: tail it while a run is active.
internal sealed class DalamudLogFaultTail
{
    internal const string AgentReceiveEventFaultMarker = "Caught exception when calling original Agent ReceiveEvent";
    private const int MaxReadBytes = 4 * 1024 * 1024;

    private readonly string? path;
    private long position;
    private string carry = string.Empty;

    public DalamudLogFaultTail(string? path) => this.path = path;

    public bool IsAvailable => path != null && File.Exists(path);

    public string? Path => path;

    public void MarkStart()
    {
        carry = string.Empty;
        position = TryGetLength() ?? 0;
    }

    public int ReadNewFaults()
    {
        if (path == null)
            return 0;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < position)
            {
                // Rotated or truncated: start over from the top of the new file.
                position = 0;
                carry = string.Empty;
            }

            if (stream.Length == position)
                return 0;

            if (stream.Length - position > MaxReadBytes)
                position = stream.Length - MaxReadBytes;

            stream.Seek(position, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - position];
            var read = stream.Read(buffer, 0, buffer.Length);
            position += read;

            var text = carry + Encoding.UTF8.GetString(buffer, 0, read);
            var faults = CountOccurrences(text, AgentReceiveEventFaultMarker);
            var keep = AgentReceiveEventFaultMarker.Length - 1;
            carry = text.Length > keep ? text[^keep..] : text;
            return faults;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private long? TryGetLength()
    {
        try
        {
            return path != null && File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static int CountOccurrences(string text, string marker)
    {
        var count = 0;
        for (var index = text.IndexOf(marker, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
