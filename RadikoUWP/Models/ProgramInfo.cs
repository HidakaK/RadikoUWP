using System;

namespace RadikoUWP.Models
{
    /// <summary>放送中の番組情報。</summary>
    public class ProgramInfo
    {
        public string Title { get; set; }
        public string Performer { get; set; }
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset End { get; set; }

        public string TimeText =>
            $"{Start.ToLocalTime():HH\\:mm} – {End.ToLocalTime():HH\\:mm}";

        /// <summary>番組の経過割合 (0～100)。</summary>
        public double ProgressPercent(DateTimeOffset now)
        {
            var total = (End - Start).TotalSeconds;
            if (total <= 0)
                return 0;
            var elapsed = (now - Start).TotalSeconds;
            return Math.Max(0, Math.Min(100, elapsed / total * 100));
        }
    }
}
