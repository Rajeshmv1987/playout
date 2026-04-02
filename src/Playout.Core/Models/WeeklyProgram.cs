using System;
using System.ComponentModel;

namespace Playout.Core.Models;

public sealed class WeeklyProgram : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DayOfWeek DayOfWeek { get; set; }

    TimeSpan _startTime;
    public TimeSpan StartTime
    {
        get => _startTime;
        set
        {
            if (_startTime == value) return;
            _startTime = value;
            OnPropertyChanged(nameof(StartTime));
            OnPropertyChanged(nameof(EndTime));
        }
    }

    string _mediaPath = "";
    public string MediaPath
    {
        get => _mediaPath;
        set
        {
            if (_mediaPath == value) return;
            _mediaPath = value;
            OnPropertyChanged(nameof(MediaPath));
            OnPropertyChanged(nameof(FileName));
        }
    }

    public string FileName => System.IO.Path.GetFileName(MediaPath);

    TimeSpan _duration;
    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            if (_duration == value) return;
            _duration = value;
            OnPropertyChanged(nameof(Duration));
            OnPropertyChanged(nameof(EndTime));
        }
    }

    public TimeSpan EndTime => StartTime + Duration;

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
