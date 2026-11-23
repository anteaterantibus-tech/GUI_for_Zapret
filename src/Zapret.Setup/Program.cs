using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Zapret.Setup;

/// <summary>
/// Single-file installer: extracts the zip payload appended to this executable
/// and runs install.ps1 from it. No external tools, no .NET runtime required.
/// </summary>
internal static class Program
{
    private const string Magic = "ZCSPAY01";
    private const int FooterSize = 16;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Zapret Control: установка";

        var quiet = args.Contains("--no-pause", StringComparer.OrdinalIgnoreCase);
        var forwarded = args.Where(a => !a.Equals("--no-pause", StringComparison.OrdinalIgnoreCase)).ToArray();

        var tempRoot = Path.Combine(Path.GetTempPath(), "ZapretControlSetup-" + Guid.NewGuid().ToString("N"));

        try
        {
            Console.WriteLine("Zapret Control: установка");
            Console.WriteLine();

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                return Fail("Не удалось определить файл установщика.", quiet);
            }

            using var file = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length < FooterSize + 1024)
            {
                return Fail("Установщик повреждён: нет встроенных файлов.", quiet);
            }

            file.Seek(-FooterSize, SeekOrigin.End);
            using var footer = new BinaryReader(file, Encoding.ASCII, leaveOpen: true);
            var payloadLength = footer.ReadInt64();
            var magic = Encoding.ASCII.GetString(footer.ReadBytes(8));

            if (magic != Magic)
            {
                return Fail("Установщик повреждён: не найдены встроенные файлы.", quiet);
            }

            var payloadOffset = file.Length - FooterSize - payloadLength;
            if (payloadOffset <= 0 || payloadLength <= 0)
            {
                return Fail("Установщик повреждён: неверный размер встроенных файлов.", quiet);
            }

            Console.WriteLine("Распаковка файлов...");
            Directory.CreateDirectory(tempRoot);

            using (var payload = new SubStream(file, payloadOffset, payloadLength))
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(tempRoot, overwriteFiles: true);
            }

            var script = Path.Combine(tempRoot, "install.ps1");
            if (!File.Exists(script))
            {
                return Fail("В установщике отсутствует install.ps1.", quiet);
            }

            Console.WriteLine("Установка...");
            Console.WriteLine();

            var psi = new ProcessStartInfo("powershell.exe")
            {
                WorkingDirectory = tempRoot,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(script);
            foreach (var arg in forwarded) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return Fail("Не удалось запустить PowerShell.", quiet);
            }

            process.WaitForExit();
            Console.WriteLine();

            if (process.ExitCode != 0)
            {
                return Fail($"Установка завершилась с кодом {process.ExitCode}.", quiet);
            }

            Console.WriteLine("Готово. Ярлык «Zapret Control» создан.");
            return Finish(0, quiet);
        }
        catch (Exception ex)
        {
            return Fail("Ошибка установки: " + ex.Message, quiet);
        }
        finally
        {
            TryDelete(tempRoot);
        }
    }

    private static int Fail(string message, bool quiet)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
        return Finish(1, quiet);
    }

    private static int Finish(int code, bool quiet)
    {
        if (!quiet)
        {
            Console.WriteLine();
            Console.WriteLine("Нажмите Enter, чтобы закрыть окно...");
            Console.ReadLine();
        }
        return code;
    }

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                return;
            }
            catch
            {
                Thread.Sleep(300);
            }
        }
    }
}

/// <summary>Read-only window over a region of a file, used by ZipArchive.</summary>
internal sealed class SubStream : Stream
{
    private readonly Stream _base;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public SubStream(Stream baseStream, long start, long length)
    {
        _base = baseStream;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;
        if (remaining <= 0) return 0;

        var toRead = (int)Math.Min(count, remaining);
        _base.Position = _start + _position;
        var read = _base.Read(buffer, offset, toRead);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => _position
        };
        _position = Math.Clamp(_position, 0, _length);
        return _position;
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
