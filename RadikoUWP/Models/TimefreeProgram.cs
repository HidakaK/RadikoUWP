using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.UI.Xaml;

namespace RadikoUWP.Models
{
    public enum TimefreeAvailability
    {
        Available,
        NotYetBroadcast,   // 放送前
        OnAir,             // 放送中 (終了後に聴ける)
        Expired,           // 公開期間 (7 日) 外
        NotAllowed         // タイムフリー対象外の番組
    }

    /// <summary>タイムフリーで聴く番組。</summary>
    public class TimefreeProgram : INotifyPropertyChanged
    {
        private bool _isPlaying;

        public Station Station { get; set; }
        public ProgramInfo Info { get; set; }

        /// <summary>radiko の放送日 (5:00 区切り、yyyyMMdd)。</summary>
        public string BroadcastDate { get; set; }

        public TimefreeAvailability Availability { get; set; }

        public string Title => Info?.Title ?? "";
        public string Performer => Info?.Performer ?? "";
        public string TimeText => Info?.TimeText ?? "";
        public TimeSpan Duration => Info != null ? Info.End - Info.Start : TimeSpan.Zero;

        public string DateTimeText =>
            Info == null ? "" : $"{Info.Start.ToLocalTime():M/d} ({DayOfWeekText(Info.Start.ToLocalTime().DayOfWeek)}) {TimeText}";

        public bool IsAvailable => Availability == TimefreeAvailability.Available;

        /// <summary>聴けない理由 (聴ける場合は空)。色だけに頼らずテキストで示す。</summary>
        public string StatusText
        {
            get
            {
                switch (Availability)
                {
                    case TimefreeAvailability.NotYetBroadcast: return "放送前";
                    case TimefreeAvailability.OnAir: return "放送中 (終了後に聴けます)";
                    case TimefreeAvailability.Expired: return "公開期間外";
                    case TimefreeAvailability.NotAllowed: return "タイムフリー対象外";
                    default: return "";
                }
            }
        }

        public Visibility StatusVisibility => IsAvailable ? Visibility.Collapsed : Visibility.Visible;
        public double ItemOpacity => IsAvailable ? 1.0 : 0.6;

        public bool IsPlaying
        {
            get { return _isPlaying; }
            set
            {
                if (_isPlaying == value)
                    return;
                _isPlaying = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlayingVisibility));
            }
        }

        public Visibility PlayingVisibility => _isPlaying ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public static string DayOfWeekText(DayOfWeek day)
        {
            return "日月火水木金土".Substring((int)day, 1);
        }
    }

    /// <summary>タイムフリーの日付選択肢。</summary>
    public class BroadcastDay
    {
        public string Date { get; set; }   // yyyyMMdd
        public string Label { get; set; }

        public override string ToString()
        {
            return Label;
        }
    }
}
