using System.Globalization;
using System.Text;

namespace InterpolatIt.Core.Pipeline;

/// <summary>
/// One CSV row per display refresh seen by the presenter: what was due, what was shown, and when. Rows are
/// buffered in memory and written by <see cref="Flush"/> so that logging never blocks the presentation loop.
/// </summary>
public sealed class PacingLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly StringBuilder _buffer = new(1 << 16);
    private readonly Lock _lock = new();

    public const string Header = "time_s,refresh,action,planned_refresh,kind,frame_a,frame_b,t,late,queue,cloaked,lag,present_count,stat_present_count,stat_present_refresh,stat_sync_refresh,stat_sync_time_s";

    public PacingLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, false, new UTF8Encoding(false));
        _writer.WriteLine(Header);
    }

    public void Row(double time, long refresh, string action, long plannedRefresh, string kind, long frameA, long frameB,
        float t, int late, int queue, bool cloaked, int lag, uint presentCount, uint statPresentCount,
        uint statPresentRefresh, uint statSyncRefresh, double statSyncTime)
    {
        lock (_lock)
        {
            _buffer.Append(CultureInfo.InvariantCulture,
                $"{time:F6},{refresh},{action},{plannedRefresh},{kind},{frameA},{frameB},{t:F4},{late},{queue},{(cloaked ? 1 : 0)},{lag},{presentCount},{statPresentCount},{statPresentRefresh},{statSyncRefresh},{statSyncTime:F6}\n");
        }
    }

    public void Flush()
    {
        string text;
        lock (_lock)
        {
            if (_buffer.Length == 0) return;
            text = _buffer.ToString();
            _buffer.Clear();
        }
        _writer.Write(text);
        _writer.Flush();
    }

    public void Dispose()
    {
        Flush();
        _writer.Dispose();
    }
}
