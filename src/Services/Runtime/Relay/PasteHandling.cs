using OpencodeWrap.Services.Runtime.Infrastructure;
using System.Text;

namespace OpencodeWrap.Services.Runtime.Relay;

internal interface INormalInputInterceptor
{
    void Forward(ReadOnlySpan<byte> bytes, Stream relayInput);
    void Flush(Stream relayInput);
}

internal sealed class BracketedPasteRelay(
    InteractiveSessionContext session,
    string? hostWorkingDirectory,
    PastedImagePathService pastedImagePathService,
    INormalInputInterceptor? normalInputInterceptor = null)
{
    private static readonly byte[] _pasteStartMarker = "\u001b[200~"u8.ToArray();
    private static readonly byte[] _pasteEndMarker = "\u001b[201~"u8.ToArray();

    private readonly InteractiveSessionContext _session = session;
    private readonly string? _hostWorkingDirectory = hostWorkingDirectory;
    private readonly PastedImagePathService _pastedImagePathService = pastedImagePathService;
    private readonly INormalInputInterceptor? _normalInputInterceptor = normalInputInterceptor;
    private readonly List<byte> _pasteBuffer = [];
    private readonly List<byte> _startMarkerCandidate = [];
    private readonly List<byte> _endMarkerCandidate = [];
    private bool _insidePaste;

    public void Forward(ReadOnlySpan<byte> buffer, Stream relayInput)
    {
        if(buffer.Length == 0)
        {
            return;
        }

        var current = PrepareCurrentBuffer(buffer);
        int offset = 0;

        while(offset < current.Length)
        {
            if(_insidePaste)
            {
                int endMarkerIndex = current[offset..].IndexOf(_pasteEndMarker);
                if(endMarkerIndex >= 0)
                {
                    if(endMarkerIndex > 0)
                    {
                        _pasteBuffer.AddRange(current.Slice(offset, endMarkerIndex).ToArray());
                    }

                    FlushNormalInput(relayInput);
                    WriteBytes(relayInput, _pasteStartMarker);
                    WriteBytes(relayInput, RewritePasteBytes(_pasteBuffer));
                    WriteBytes(relayInput, _pasteEndMarker);
                    _pasteBuffer.Clear();
                    _insidePaste = false;
                    offset += endMarkerIndex + _pasteEndMarker.Length;
                    continue;
                }

                int endTrailingPrefixLength = GetTrailingPrefixLength(current[offset..], _pasteEndMarker);
                int pasteLength = current.Length - offset - endTrailingPrefixLength;
                if(pasteLength > 0)
                {
                    _pasteBuffer.AddRange(current.Slice(offset, pasteLength).ToArray());
                }

                if(endTrailingPrefixLength > 0)
                {
                    _endMarkerCandidate.AddRange(current[^endTrailingPrefixLength..].ToArray());
                }

                return;
            }

            int startMarkerIndex = current[offset..].IndexOf(_pasteStartMarker);
            if(startMarkerIndex >= 0)
            {
                if(startMarkerIndex > 0)
                {
                    WriteNormalInput(relayInput, current.Slice(offset, startMarkerIndex).ToArray());
                }

                FlushNormalInput(relayInput);
                _insidePaste = true;
                offset += startMarkerIndex + _pasteStartMarker.Length;
                continue;
            }

            int trailingPrefixLength = GetTrailingPrefixLength(current[offset..], _pasteStartMarker);
            int forwardLength = current.Length - offset - trailingPrefixLength;
            if(forwardLength > 0)
            {
                WriteNormalInput(relayInput, current.Slice(offset, forwardLength).ToArray());
            }

            if(trailingPrefixLength > 0)
            {
                _startMarkerCandidate.AddRange(current[^trailingPrefixLength..].ToArray());
            }

            return;
        }
    }

    public void Flush(Stream relayInput)
    {
        if(_insidePaste)
        {
            if(_endMarkerCandidate.Count > 0)
            {
                _pasteBuffer.AddRange(_endMarkerCandidate);
                _endMarkerCandidate.Clear();
            }

            WriteBytes(relayInput, _pasteStartMarker);
            WriteBytes(relayInput, [.. _pasteBuffer]);
            _pasteBuffer.Clear();
            _insidePaste = false;
        }

        if(_startMarkerCandidate.Count > 0)
        {
            WriteNormalInput(relayInput, [.. _startMarkerCandidate]);
            _startMarkerCandidate.Clear();
        }

        FlushNormalInput(relayInput);
    }

    private ReadOnlySpan<byte> PrepareCurrentBuffer(ReadOnlySpan<byte> buffer)
    {
        if(_insidePaste && _endMarkerCandidate.Count > 0)
        {
            byte[] combined = new byte[_endMarkerCandidate.Count + buffer.Length];
            _endMarkerCandidate.CopyTo(combined, 0);
            buffer.CopyTo(combined.AsSpan(_endMarkerCandidate.Count));
            _endMarkerCandidate.Clear();
            return combined;
        }

        if(!_insidePaste && _startMarkerCandidate.Count > 0)
        {
            byte[] combined = new byte[_startMarkerCandidate.Count + buffer.Length];
            _startMarkerCandidate.CopyTo(combined, 0);
            buffer.CopyTo(combined.AsSpan(_startMarkerCandidate.Count));
            _startMarkerCandidate.Clear();
            return combined;
        }

        return buffer;
    }

    private static int GetTrailingPrefixLength(ReadOnlySpan<byte> buffer, byte[] marker)
    {
        int maxPrefixLength = Math.Min(marker.Length - 1, buffer.Length);
        for(int prefixLength = maxPrefixLength; prefixLength > 0; prefixLength--)
        {
            if(buffer[^prefixLength..].SequenceEqual(marker.AsSpan(0, prefixLength)))
            {
                return prefixLength;
            }
        }

        return 0;
    }

    private byte[] RewritePasteBytes(List<byte> pasteBytes)
    {
        string pastedText = Encoding.UTF8.GetString([.. pasteBytes]);
        var rewriteResult = _pastedImagePathService.RewritePaste(pastedText, _session, _hostWorkingDirectory);
        return Encoding.UTF8.GetBytes(rewriteResult.Text);
    }

    private static void WriteBytes(Stream relayInput, byte[] bytes)
    {
        if(bytes.Length == 0)
        {
            return;
        }

        InteractiveDockerRunnerService.WriteRelayBytes(relayInput, bytes);
    }

    private void WriteNormalInput(Stream relayInput, byte[] bytes)
    {
        if(_normalInputInterceptor is null)
        {
            WriteBytes(relayInput, bytes);
            return;
        }

        _normalInputInterceptor.Forward(bytes, relayInput);
    }

    private void FlushNormalInput(Stream relayInput)
        => _normalInputInterceptor?.Flush(relayInput);
}

internal sealed class WindowsPlainTextPasteInterceptor(
    InteractiveSessionContext session,
    string? hostWorkingDirectory,
    PastedImagePathService pastedImagePathService) : INormalInputInterceptor
{
    private readonly InteractiveSessionContext _session = session;
    private readonly string? _hostWorkingDirectory = hostWorkingDirectory;
    private readonly PastedImagePathService _pastedImagePathService = pastedImagePathService;

    public void Forward(ReadOnlySpan<byte> bytes, Stream relayInput)
    {
        if(bytes.Length == 0)
        {
            return;
        }

        if(bytes.Length > 1 && TryEmitDirectInputRewrite(bytes, relayInput))
        {
            return;
        }

        WriteForwardedBytes(relayInput, bytes.ToArray());
    }

    public void Flush(Stream relayInput)
    {
    }

    private bool TryEmitDirectInputRewrite(ReadOnlySpan<byte> bytes, Stream relayInput)
    {
        string inputText;

        try
        {
            inputText = Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return false;
        }

        if(!_pastedImagePathService.CanRewritePaste(inputText, _hostWorkingDirectory))
        {
            return false;
        }

        var rewriteResult = _pastedImagePathService.RewritePaste(inputText, _session, _hostWorkingDirectory);
        if(!rewriteResult.Rewritten)
        {
            return false;
        }

        WriteForwardedBytes(relayInput, Encoding.UTF8.GetBytes(rewriteResult.Text));
        return true;
    }

    private static void WriteForwardedBytes(Stream relayInput, byte[] bytes)
        => InteractiveDockerRunnerService.WriteRelayBytes(relayInput, bytes);
}
