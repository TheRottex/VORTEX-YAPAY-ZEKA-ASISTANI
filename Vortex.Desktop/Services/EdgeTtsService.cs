using System;
using System.IO;
using System.Net.WebSockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Vortex.Desktop.Services;

public sealed class EdgeTtsService
{
    private const string WssUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken=6A5AA1D4EAFF4E9FB37E23D68491D6F4";

    public async Task<byte[]?> SynthesizeAsync(string text, string voice = "tr-TR-AhmetNeural", CancellationToken cancellationToken = default)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edge/120.0.0.0");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibdchkecoahfibglcbgj");

        try
        {
            await ws.ConnectAsync(new Uri(WssUrl), cancellationToken);
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Edge TTS WebSocket bağlantısı başarısız oldu.", ex);
            return null;
        }

        // Send config
        var configMessage = "Content-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
                            "{\"context\":{\"system\":{\"name\":\"SpeechSDK\",\"version\":\"1.30.0\",\"build\":\"JavaScript\",\"lang\":\"javascript\"}}}";
        var configBytes = Encoding.UTF8.GetBytes(configMessage);
        await ws.SendAsync(new ArraySegment<byte>(configBytes), WebSocketMessageType.Text, true, cancellationToken);

        var ssml = BuildSsml(text, voice);
        var ssmlMessage = $"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nPath:ssml\r\n\r\n{ssml}";
        var ssmlBytes = Encoding.UTF8.GetBytes(ssmlMessage);
        await ws.SendAsync(new ArraySegment<byte>(ssmlBytes), WebSocketMessageType.Text, true, cancellationToken);

        using var ms = new MemoryStream();
        var buffer = new byte[8192];

        while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            using var chunkStream = new MemoryStream();
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                chunkStream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) break;

            var chunkBytes = chunkStream.ToArray();
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Binary message format has text header, then \r\n\r\n, then actual binary audio
                var headerEndIndex = FindHeaderEnd(chunkBytes);
                if (headerEndIndex != -1)
                {
                    var audioOffset = headerEndIndex + 4;
                    if (chunkBytes.Length > audioOffset)
                    {
                        ms.Write(chunkBytes, audioOffset, chunkBytes.Length - audioOffset);
                    }
                }
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                var textContent = Encoding.UTF8.GetString(chunkBytes);
                if (textContent.Contains("Path:turn.end")) break; // End of transmission
            }
        }

        try
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
        }
        catch (Exception ex)
        {
            DesktopLogService.Error("Edge TTS WebSocket kapatılamadı.", ex);
        }

        var output = ms.ToArray();
        return output.Length > 0 ? output : null;
    }

    private static string BuildSsml(string text, string voice)
    {
        var parts = voice.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var locale = parts.Length >= 2 ? $"{parts[0]}-{parts[1]}" : "tr-TR";
        var voiceShortName = parts.Length >= 3 ? parts[2].Replace("Neural", string.Empty, StringComparison.OrdinalIgnoreCase) : "Ahmet";
        var voiceName = SecurityElement.Escape($"Microsoft Server Speech Text to Speech Voice ({locale}, {voiceShortName}Neural)") ?? string.Empty;
        var escapedText = SecurityElement.Escape(text) ?? string.Empty;
        return $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='tr-TR'><voice name='{voiceName}'><prosody rate='+0%'>{escapedText}</prosody></voice></speak>";
    }

    private static int FindHeaderEnd(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length - 3; i++)
        {
            if (bytes[i] == 0x0D && bytes[i + 1] == 0x0A && bytes[i + 2] == 0x0D && bytes[i + 3] == 0x0A)
            {
                return i;
            }
        }
        return -1;
    }
}
