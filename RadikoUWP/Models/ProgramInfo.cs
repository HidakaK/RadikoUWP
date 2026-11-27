using System;

namespace RadikoUWP.Models
{
    /// <summary>放送中の番組情報。</summary>
    public class ProgramInfo
    {
        public string Title { get; set; }
        public string Performer { get; set; }

        /// <summary>番組の概要 (HTML を除いたテキスト)。詳細ペインに全文を表示する。</summary>
        public string Description { get; set; }

        /// <summary>番組画像の URL (無い場合は null)。</summary>
        public string ImageUrl { get; set; }

        /// <summary>一覧用の概要 (改行を詰めた 1 段落。一覧では 2 行に切り詰めて表示)。</summary>
        public string Summary =>
            string.IsNullOrEmpty(Description) ? "" : System.Text.RegularExpressions.Regex.Replace(Description, @"\s+", " ").Trim();
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
