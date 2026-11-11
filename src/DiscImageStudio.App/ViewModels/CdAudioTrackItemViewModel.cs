using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Cd;
using DiscImageStudio.Localization;

namespace DiscImageStudio.ViewModels;

/// <summary>One playable CD lead-in track row on the design page. The duration text fills
/// in after the shared transcoder validates the file; a row that fails validation keeps its
/// place in the list with the error so the user can fix or remove it before burning.</summary>
public partial class CdAudioTrackItemViewModel(string path) : ObservableObject
{
    public string Path { get; } = path;

    public string FileName => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    private string _durationText = L.T("校验中…");

    /// <summary>Decoded CD-DA playing time in seconds; 0 while unknown or invalid.</summary>
    public double DecodedSeconds { get; private set; }

    [ObservableProperty]
    private string? _errorText;

    public bool IsInvalid => ErrorText is not null;

    internal void FillFromPrepared(CdAudioPcmFile prepared)
    {
        DecodedSeconds = prepared.PcmBytes / 176400.0;
        DurationText = DesignViewModel.FormatTrackTime(DecodedSeconds);
        ErrorText = null;
    }

    internal void MarkInvalid(string message)
    {
        DecodedSeconds = 0;
        DurationText = L.T("不可用");
        ErrorText = message;
    }

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(IsInvalid));
}
