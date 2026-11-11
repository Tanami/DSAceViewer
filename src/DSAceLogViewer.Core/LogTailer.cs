using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DSAceLogViewer.Core;

public sealed record TailUpdate(IReadOnlyList<string> Lines, bool Reset, string Status, bool Available);

/// <summary>Polling avoids lost FileSystemWatcher notifications. Every read permits the game to write, truncate or rotate the log.</summary>
public sealed class LogTailer
{
    private readonly string _path;
    private readonly string _encodingName;
    private long _position;
    private string? _identity;
    private byte[] _anchor = [];
    private readonly StringBuilder _pending = new();
    private Decoder? _decoder;
    private bool _first = true;
    private bool _discardingLongLine;
    public const int MaximumLineCharacters = 256 * 1024;
    public const int MaximumReadBytes = 1024 * 1024;

    public LogTailer(string path, string encodingName = "Auto")
    {
        _path = path;
        _encodingName = encodingName;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task RunAsync(Func<TailUpdate, Task> receive, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var update = await ReadOnceAsync(cancellationToken);
            await receive(update);
            await Task.Delay(200, cancellationToken);
        }
    }

    public async Task<TailUpdate> ReadOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
            var identity = GetIdentity(stream);
            var reset = _first || identity != _identity || stream.Length < _position;
            if (!reset && _anchor.Length > 0)
            {
                stream.Position = _position - _anchor.Length;
                var check = new byte[_anchor.Length];
                var read = await stream.ReadAsync(check, cancellationToken);
                reset = read != check.Length || !check.AsSpan().SequenceEqual(_anchor);
            }
            if (reset)
            {
                _position = 0;
                _anchor = [];
                _pending.Clear();
                _discardingLongLine = false;
                _decoder = null;
                _identity = identity;
                _first = false;
            }

            stream.Position = _position;
            var bytes = new byte[Math.Min(MaximumReadBytes, Math.Max(0, stream.Length - _position))];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
                if (read == 0) break;
                count += read;
            }
            var offset = 0;
            if (_decoder is null && count > 0)
            {
                // Wait for enough bytes to distinguish a split BOM from actual text.
                if (count < 3 && (bytes[0] == 0xEF || bytes[0] == 0xFF || bytes[0] == 0xFE))
                    return new([], reset, "Waiting for complete text header…", true);
                var (encoding, bomBytes) = DetectEncoding(bytes.AsSpan(0, count), _encodingName);
                _decoder = encoding.GetDecoder();
                offset = bomBytes;
            }
            _position += count;
            var anchorLength = (int)Math.Min(64, _position);
            _anchor = new byte[anchorLength];
            stream.Position = _position - anchorLength;
            await stream.ReadExactlyAsync(_anchor, cancellationToken);
            var lines = new List<string>();
            if (_decoder is not null && count > offset)
            {
                var characters = new char[(count - offset) * 2 + 4];
                var characterCount = _decoder.GetChars(bytes, offset, count - offset, characters, 0, false);
                for (var index = 0; index < characterCount; index++)
                {
                    var character = characters[index];
                    if (character == '\n')
                    {
                        if (!_discardingLongLine)
                            lines.AddRange(LogParser.SplitJoinedEntries(_pending.ToString().TrimEnd('\r')));
                        _pending.Clear();
                        _discardingLongLine = false;
                    }
                    else if (!_discardingLongLine)
                    {
                        _pending.Append(character);
                        if (_pending.Length > MaximumLineCharacters)
                        {
                            lines.Add(_pending.ToString(0, MaximumLineCharacters) + " [line truncated: exceeds 256K characters]");
                            _pending.Clear();
                            _discardingLongLine = true;
                        }
                    }
                }
            }
            var status = stream.Length > _position ? "Loading log history…"
                : _pending.Length > 0 ? "Watching · waiting for the last line to finish" : "Watching for new entries";
            return new(lines, reset, status, true);
        }
        catch (FileNotFoundException) { return new([], false, "Waiting for DSAce.log to be created…", false); }
        catch (DirectoryNotFoundException) { return new([], false, "Folder not found · choose a log in Settings", false); }
        catch (UnauthorizedAccessException) { return new([], false, "Access denied · choose a readable log in Settings", false); }
        catch (IOException) { return new([], false, "Log temporarily unavailable · retrying…", false); }
    }

    private static (Encoding Encoding, int BomBytes) DetectEncoding(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (Encoding.UTF8, 3);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return (Encoding.Unicode, 2);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return (Encoding.BigEndianUnicode, 2);
        if (name == "UTF-8") return (Encoding.UTF8, 0);
        if (name == "Windows-1252") return (Encoding.GetEncoding(1252), 0);
        // A decoder with flush=false accepts a UTF-8 character split at the read boundary.
        try { new UTF8Encoding(false, true).GetDecoder().GetCharCount(bytes, false); return (Encoding.UTF8, 0); }
        catch (DecoderFallbackException) { return (Encoding.GetEncoding(1252), 0); }
    }

    private static string GetIdentity(FileStream stream)
    {
        if (OperatingSystem.IsWindows() && GetFileInformationByHandle(stream.SafeFileHandle, out var information))
            return $"{information.VolumeSerialNumber}:{information.FileIndexHigh}:{information.FileIndexLow}";
        return File.GetCreationTimeUtc(stream.Name).Ticks.ToString();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
}
