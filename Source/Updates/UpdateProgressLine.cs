using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace JustEnoughTools.Updates
{
    // Adapted from LANMinecraft's update bar. Keep its player-facing wording,
    // six-second rate window and coarse ETA rather than reporting an instant.
    internal static class UpdateProgressLine
    {
        internal const int Budget = 68;
        internal static string Checking() { return "Проверка обновлений"; }
        internal static string UpToDate(int release) { return "Вы на последней версии (" + release + ")"; }
        internal static string Ready(int release) { return "Обновление " + release + " готово к установке"; }
        internal static string Applying(int release) { return "Применение обновления " + release; }
        internal static string Starting(int release) { return "Обновление " + release + ": скачивается"; }
        internal static string CheckFailed() { return "Не удалось проверить обновления"; }
        internal static string DownloadFailed(int release) { return "Не удалось скачать обновление " + release; }

        internal static string Downloading(int release, long downloaded, long total, double bytesPerSecond, TimeSpan? remaining)
        {
            string head = "Обновление " + release + ": " + TransferProgressLine.FormatPair(downloaded, total);
            string speed = TransferProgressLine.FormatBytes((long)Math.Max(0, bytesPerSecond)) + "/с";
            string left = TransferProgressLine.Remaining(remaining);
            if (left == null) return head + " (" + speed + ")";
            string full = head + " (" + speed + ", " + left + ")";
            return full.Length <= Budget ? full : head + " (" + left + ")";
        }

        internal static TimeSpan? Estimate(long downloaded, long total, double bytesPerSecond)
        {
            if (bytesPerSecond <= 0 || total <= downloaded) return null;
            double seconds = (total - downloaded) / bytesPerSecond;
            return double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds > TimeSpan.MaxValue.TotalSeconds
                ? (TimeSpan?)null : TimeSpan.FromSeconds(seconds);
        }
    }

    internal static class TransferProgressLine
    {
        internal static string Remaining(TimeSpan? remaining)
        {
            if (!remaining.HasValue || remaining.Value < TimeSpan.Zero || remaining.Value > TimeSpan.FromDays(1)) return null;
            double seconds = remaining.Value.TotalSeconds;
            int step = seconds < 60 ? 5 : seconds < 600 ? 10 : seconds < 3600 ? 60 : 300;
            long rounded = (long)Math.Round(seconds / step, MidpointRounding.AwayFromZero) * step;
            TimeSpan span = TimeSpan.FromSeconds(Math.Max(step, rounded));
            if (span.TotalHours >= 1)
                return span.Minutes == 0 ? "ещё ≈ " + (int)span.TotalHours + " ч" : "ещё ≈ " + (int)span.TotalHours + " ч " + span.Minutes + " мин";
            if (span.TotalSeconds < 60) return "ещё ≈ " + span.Seconds + " с";
            if (span.TotalSeconds >= 600 || span.Seconds == 0) return "ещё ≈ " + (int)span.TotalMinutes + " мин";
            return "ещё ≈ " + span.Minutes + " мин " + span.Seconds + " с";
        }

        internal static string FormatPair(long current, long total)
        {
            string done = FormatBytes(current);
            string whole = FormatBytes(total);
            string unit = whole.Substring(whole.LastIndexOf(' ') + 1);
            return done.EndsWith(" " + unit, StringComparison.Ordinal)
                ? done.Substring(0, done.Length - unit.Length - 1) + " / " + whole : done + " / " + whole;
        }

        internal static string FormatBytes(long bytes)
        {
            const long kb = 1024, mb = kb * 1024, gb = mb * 1024;
            if (bytes >= gb) return (bytes / (double)gb).ToString("0.##") + " ГБ";
            if (bytes >= mb) return (bytes / (double)mb).ToString("0.##") + " МБ";
            if (bytes >= kb) return (bytes / (double)kb).ToString("0.##") + " КБ";
            return bytes + " Б";
        }
    }

    internal sealed class TransferRateTracker
    {
        private readonly Queue<Sample> _samples = new Queue<Sample>();
        private string _scope = "";
        private double _bytesPerSecond;

        internal double Update(long currentBytes, string scope)
        {
            long now = Stopwatch.GetTimestamp();
            currentBytes = Math.Max(0, currentBytes);
            if (!string.Equals(_scope, scope, StringComparison.Ordinal)) { Reset(); _scope = scope; }
            if (_samples.Count > 0 && currentBytes < _samples.Last().Bytes)
            {
                if (currentBytes < _samples.Peek().Bytes) { _samples.Clear(); _bytesPerSecond = 0; }
                else currentBytes = _samples.Last().Bytes;
            }
            _samples.Enqueue(new Sample(now, currentBytes));
            long horizon = now - 6L * Stopwatch.Frequency;
            while (_samples.Count > 2 && _samples.Peek().Timestamp < horizon) _samples.Dequeue();
            Sample oldest = _samples.Peek();
            double elapsed = (now - oldest.Timestamp) / (double)Stopwatch.Frequency;
            if (elapsed >= 1d) _bytesPerSecond = Math.Max(0, currentBytes - oldest.Bytes) / elapsed;
            return _bytesPerSecond;
        }

        internal void Reset() { _scope = ""; _samples.Clear(); _bytesPerSecond = 0; }

        private struct Sample
        {
            internal Sample(long timestamp, long bytes) { Timestamp = timestamp; Bytes = bytes; }
            internal readonly long Timestamp;
            internal readonly long Bytes;
        }
    }
}
