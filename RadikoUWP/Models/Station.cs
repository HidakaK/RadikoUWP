using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace RadikoUWP.Models
{
    public class Station : INotifyPropertyChanged
    {
        private ImageSource _logo;
        private ImageSource _largeLogo;
        private ProgramInfo _program;
        private bool _isPlaying;

        public string Id { get; set; }
        public string Name { get; set; }
        public string AreaId { get; set; }
        public string LogoUrl { get; set; }

        /// <summary>一覧用のロゴ (モバイルのメモリ節約のため縮小デコード)。</summary>
        public ImageSource Logo
        {
            get
            {
                if (_logo == null && !string.IsNullOrEmpty(LogoUrl))
                    _logo = new BitmapImage(new Uri(LogoUrl)) { DecodePixelWidth = 192 };
                return _logo;
            }
        }

        /// <summary>詳細表示用のロゴ。</summary>
        public ImageSource LargeLogo
        {
            get
            {
                if (_largeLogo == null && !string.IsNullOrEmpty(LogoUrl))
                    _largeLogo = new BitmapImage(new Uri(LogoUrl));
                return _largeLogo;
            }
        }

        public ProgramInfo Program
        {
            get { return _program; }
            set
            {
                _program = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgramTitle));
                OnPropertyChanged(nameof(ProgramPerformer));
                OnPropertyChanged(nameof(ProgramTime));
                OnPropertyChanged(nameof(ProgramSummary));
                OnPropertyChanged(nameof(ProgramSummaryVisibility));
            }
        }

        public string ProgramTitle => _program?.Title ?? "";
        public string ProgramPerformer => _program?.Performer ?? "";
        public string ProgramTime => _program?.TimeText ?? "";
        public string ProgramSummary => _program?.Summary ?? "";
        public Visibility ProgramSummaryVisibility =>
            string.IsNullOrEmpty(ProgramSummary) ? Visibility.Collapsed : Visibility.Visible;

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

        public override string ToString()
        {
            return Name;
        }
    }
}
